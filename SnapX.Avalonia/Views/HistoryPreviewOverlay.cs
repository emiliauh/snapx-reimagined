// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Reactive;
using Avalonia.Threading;
using SnapX.Core;
using SnapX.Core.History;

namespace SnapX.Avalonia.Views;

/// <summary>
/// A persistent in-SnapX preview for a history item (image, video or text).
/// This deliberately renders inside the owner's OverlayLayer rather than as a
/// new top-level Window (which becomes a tiled Wayland toplevel) or a Popup /
/// Flyout (whose transient EGL surface is unreliable under native Wayland and
/// has produced repeated eglMakeCurrent failures). It never delegates to an
/// external viewer. Video is decoded internally with ffmpeg and the frames are
/// pushed into a WriteableBitmap.
/// </summary>
public sealed class HistoryPreviewOverlay
{
    private static HistoryPreviewOverlay? _current;

    private readonly Window _owner;
    private readonly OverlayLayer _overlay;
    private readonly HistoryItem _item;
    private readonly Border _scrim;
    private readonly Action _copyAction;
    private readonly Action _deleteAction;
    private readonly Action _openFolderAction;
    private readonly bool _isVideo;
    private readonly bool _isImage;
    private readonly bool _isText;
    private Process? _videoProcess;
    private WriteableBitmap? _videoBitmap;
    private Image? _videoImage;
    private ZoomableImagePreview? _imagePreview;
    private byte[]? _pendingVideoFrame;
    private int _videoUpdateScheduled;
    private volatile bool _videoClosed;
    private bool _disposed;
    private IDisposable? _ownerClientSizeSubscription;

    /// <summary>
    /// Shows the overlay for <paramref name="item"/>. The supplied actions are
    /// already bound to that history item by the caller so the overlay does not
    /// need to know how to resolve selection/refresh.
    /// </summary>
    public static void Show(
        HistoryItem item,
        Window? owner,
        Action copy,
        Action delete,
        Action openFolder)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _current?.Dispose();

            if (owner is not { IsVisible: true } visibleOwner)
            {
                return;
            }

            OverlayLayer? overlay = OverlayLayer.GetOverlayLayer(visibleOwner);
            if (overlay is null)
            {
                return;
            }

            var preview = new HistoryPreviewOverlay(
                visibleOwner, overlay, item, copy, delete, openFolder);
            _current = preview;
            preview.Show();
        });
    }

    /// <summary>
    /// Removes a live preview before a compositor-owned region selection
    /// begins. A preview is in the main window's visual tree (not a separate
    /// Wayland popup), but a video preview can continue scheduling GPU redraws
    /// while slurp owns the pointer. Closing it first also stops its decoder.
    /// </summary>
    public static async Task CloseForRegionCaptureAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => _current?.Dispose());

        // Dispose only mutates the visual tree; the removal is not committed to
        // the compositor until a layout/render pass runs. Yield at a priority
        // below Render so the detached preview (and its bitmap-backed
        // composition content) is actually gone before slurp takes over.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private HistoryPreviewOverlay(
        Window owner,
        OverlayLayer overlay,
        HistoryItem item,
        Action copy,
        Action delete,
        Action openFolder)
    {
        _owner = owner;
        _overlay = overlay;
        _item = item;
        _copyAction = copy;
        _deleteAction = delete;
        _openFolderAction = openFolder;

        string extension = Path.GetExtension(item.FilePath ?? string.Empty);
        _isImage = IsImageExtension(extension);
        _isVideo = IsVideoExtension(extension);
        _isText = IsTextExtension(extension);
        _owner.Closed += Owner_OnClosed;
        _owner.Resized += Owner_OnResized;
        _scrim = BuildScrim();
    }

    private void Show()
    {
        // OverlayLayer derives from Canvas. A Canvas always arranges a child
        // at its DesiredSize, so Horizontal/VerticalAlignment.Stretch alone
        // cannot make this Border cover it. Keep the scrim explicitly sized
        // to the layer's arranged bounds and refresh that size on every
        // window/layout resize.
        _overlay.SizeChanged += Overlay_OnSizeChanged;
        // Window.Resized only fires for discrete platform resize notifications.
        // A continuous drag also updates ClientSize between those events, and
        // that is the window in which a stale scrim let the live application
        // content show through at the edges. Observing the property closes it.
        _ownerClientSizeSubscription = _owner
            .GetObservable(TopLevel.ClientSizeProperty)
            .Subscribe(new AnonymousObserver<Size>(size => UpdateScrimSize(ResolveOverlaySize(size))));
        UpdateScrimSize(ResolveOverlaySize());
        _overlay.Children.Add(_scrim);
        // The first arrange of the layer can land after this call, so re-apply
        // the size once layout has settled.
        Dispatcher.UIThread.Post(() => UpdateScrimSize(ResolveOverlaySize()), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => _scrim.Focus(), DispatcherPriority.Input);

        if (_isVideo)
        {
            TryStartVideo();
        }
    }

    private void Overlay_OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        UpdateScrimSize(ResolveOverlaySize(e.NewSize));

    private void Owner_OnResized(object? sender, WindowResizedEventArgs e) =>
        UpdateScrimSize(ResolveOverlaySize(e.ClientSize));

    private void UpdateScrimSize(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        // Canvas ignores Stretch while arranging its direct children. Keep the
        // explicit dimensions and Stretch together so the scrim covers both
        // the platform resize notification and the later overlay layout pass.
        _scrim.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scrim.VerticalAlignment = VerticalAlignment.Stretch;
        _scrim.Width = size.Width;
        _scrim.Height = size.Height;
    }

    /// <summary>
    /// The OverlayLayer is a Canvas, so it can report a zero/stale arranged
    /// size at the moment the preview is added (nothing has forced a layout
    /// pass yet). Falling back to the owner window's client size keeps the
    /// preview genuinely full-window instead of collapsing to the media's
    /// desired size, which is what made it look like a small floating card.
    /// </summary>
    private Size ResolveOverlaySize(Size? reportedSize = null)
    {
        // During a live platform resize Window.Resized arrives before the
        // Canvas has been laid out. Use the largest current/reported size so
        // a stale overlay bound can never expose foreground content at an edge.
        Size overlaySize = _overlay.Bounds.Size;
        Size ownerSize = _owner.ClientSize;
        Size eventSize = reportedSize ?? default;
        return new Size(
            Math.Max(overlaySize.Width, Math.Max(ownerSize.Width, eventSize.Width)),
            Math.Max(overlaySize.Height, Math.Max(ownerSize.Height, eventSize.Height)));
    }

    private Border BuildScrim()
    {
        var titleRow = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(16, 12, 16, 0)
        };
        var fileName = new TextBlock
        {
            Text = _item.FileName ?? "Preview",
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        DockPanel.SetDock(fileName, Dock.Left);
        titleRow.Children.Add(fileName);

        var closeButton = MakeTitleButton("×", "Close preview");
        closeButton.Click += (_, _) => Dispose();
        DockPanel.SetDock(closeButton, Dock.Right);
        titleRow.Children.Add(closeButton);

        var content = BuildContentSurface();

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(16, 12, 16, 16),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actionRow.Children.Add(MakeActionButton("Copy", "Copy content to the clipboard", Copy_OnClick));
        actionRow.Children.Add(MakeActionButton("Delete", "Delete the local file", Delete_OnClick));
        actionRow.Children.Add(MakeActionButton("Open containing folder", "Reveal the file in its folder", OpenFolder_OnClick));
        actionRow.Children.Add(MakeActionButton("Close", "Close the preview", (_, _) => Dispose()));

        // The OverlayLayer is window-sized. Keep both this scrim and the
        // preview layout stretched so this is a genuine in-window overlay,
        // rather than a capped, centered card that exposes the main UI around
        // it. The media itself remains centered by BuildContentSurface.
        var previewLayout = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 24, 30)),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        Grid.SetRow(titleRow, 0);
        Grid.SetRow(content, 1);
        Grid.SetRow(actionRow, 2);
        previewLayout.Children.Add(titleRow);
        previewLayout.Children.Add(content);
        previewLayout.Children.Add(actionRow);

        var scrim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = true,
            IsTabStop = true,
            Child = previewLayout
        };
        Canvas.SetLeft(scrim, 0);
        Canvas.SetTop(scrim, 0);
        scrim.KeyDown += Scrim_OnKeyDown;
        return scrim;
    }

    private Control BuildContentSurface()
    {
        if (_isImage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_item.FilePath))
                {
                    return MakeUnavailableSurface("The image path is not available.");
                }

                var bitmap = new Bitmap(_item.FilePath);
                _imagePreview = new ZoomableImagePreview(bitmap);
                return _imagePreview;
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Failed to decode the history image.");
                return MakeUnavailableSurface($"SnapX cannot read the image: {ex.Message}");
            }
        }

        var scroll = CreateContentScroller();

        if (_isVideo)
        {
            _videoBitmap = new WriteableBitmap(
                new PixelSize(16, 16),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            _videoImage = new Image
            {
                Source = _videoBitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            scroll.Content = _videoImage;
            return scroll;
        }

        if (_isText)
        {
            try
            {
                string text = ReadTextPreview(_item.FilePath);
                scroll.Content = new SelectableTextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                    FontSize = 13
                };
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Failed to read the history text file.");
                scroll.Content = MakeUnavailableText($"SnapX cannot read the text file: {ex.Message}");
            }
            return scroll;
        }

        scroll.Content = MakeUnavailableText("SnapX cannot show a preview of this file type.");
        return scroll;
    }

    private static ScrollViewer CreateContentScroller() => new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(16, 8, 16, 0)
    };

    private static Control MakeUnavailableSurface(string message)
    {
        var scroll = CreateContentScroller();
        scroll.Content = MakeUnavailableText(message);
        return scroll;
    }

    private static Control MakeUnavailableText(string message) => new TextBlock
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.Gray,
        Margin = new Thickness(8),
        MaxWidth = 640
    };

    private static Button MakeTitleButton(string content, string tooltip)
    {
        var button = new Button
        {
            Content = content,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button MakeActionButton(string text, string tooltip, EventHandler<RoutedEventArgs> click)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 5, 12, 5),
            MinHeight = 32
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += click;
        return button;
    }

    private void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        try { _copyAction(); }
        catch (Exception ex) { DebugHelper.WriteException(ex, "History preview copy failed."); }
    }

    private void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _deleteAction();
            Dispose();
        }
        catch (Exception ex) { DebugHelper.WriteException(ex, "History preview delete failed."); }
    }

    private void OpenFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        try { _openFolderAction(); }
        catch (Exception ex) { DebugHelper.WriteException(ex, "History preview open-folder failed."); }
    }

    private void Scrim_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Dispose();
    }

    private void TryStartVideo()
    {
        if (string.IsNullOrWhiteSpace(_item.FilePath) || !File.Exists(_item.FilePath))
        {
            return;
        }

        try
        {
            string? ffmpeg = ResolveFfmpeg();
            if (ffmpeg is null)
            {
                return;
            }

            if (!TryProbeDimensions(_item.FilePath, ffmpeg, out int srcW, out int srcH))
            {
                srcW = 640;
                srcH = 360;
            }

            double scale = Math.Min(1.0, Math.Min(960.0 / Math.Max(1, srcW), 540.0 / Math.Max(1, srcH)));
            int outW = (int)Math.Round(srcW * scale);
            int outH = (int)Math.Round(srcH * scale);
            outW = Math.Max(2, outW - outW % 2);
            outH = Math.Max(2, outH - outH % 2);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-re");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(_item.FilePath);
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"scale={outW}:{outH}");
            startInfo.ArgumentList.Add("-r");
            startInfo.ArgumentList.Add("30");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("rawvideo");
            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("bgra");
            startInfo.ArgumentList.Add("-an");
            startInfo.ArgumentList.Add("pipe:1");

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            int frameBytes = outW * outH * 4;
            WriteableBitmap? placeholder = _videoBitmap;
            _videoBitmap = new WriteableBitmap(
                new PixelSize(outW, outH),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            ReplaceVideoImage();
            placeholder?.Dispose();
            _videoProcess = process;
            _ = Task.Run(() => ReadVideoFrames(process, frameBytes, outW, outH));
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to start the in-app video decoder.");
        }
    }

    private void ReplaceVideoImage()
    {
        if (_videoImage is not null)
        {
            _videoImage.Source = _videoBitmap;
        }
    }

    private void ReadVideoFrames(Process process, int frameBytes, int width, int height)
    {
        try
        {
            var output = process.StandardOutput.BaseStream;
            while (!_videoClosed)
            {
                // Allocate a fresh buffer per frame so the UI thread can copy
                // it safely while the reader decodes the next frame, instead
                // of reusing a single buffer that would race with the copy.
                var buffer = new byte[frameBytes];
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = output.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0)
                    {
                        break;
                    }
                    offset += read;
                }

                if (offset != buffer.Length)
                {
                    // Truncated frame near end of stream; stop cleanly.
                    break;
                }

                QueueVideoFrame(buffer, width, height);
            }
        }
        catch (Exception ex)
        {
            // Killing ffmpeg during Dispose closes its pipe while this reader
            // may be blocked in Read. That is an expected shutdown path, not
            // a preview failure worth reporting.
            if (!_videoClosed)
            {
                DebugHelper.WriteException(ex, "In-app video frame reader stopped.");
            }
        }
    }

    private void QueueVideoFrame(byte[] frame, int width, int height)
    {
        if (_videoClosed)
        {
            return;
        }

        // The decoder runs off the UI thread. Retain only its newest, distinct
        // frame so a slow UI cannot accumulate a long dispatcher backlog.
        Interlocked.Exchange(ref _pendingVideoFrame, frame);
        if (Interlocked.Exchange(ref _videoUpdateScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(() => ApplyPendingVideoFrames(width, height));
        }
    }

    private void ApplyPendingVideoFrames(int width, int height)
    {
        while (!_videoClosed)
        {
            byte[]? frame = Interlocked.Exchange(ref _pendingVideoFrame, null);
            if (frame is not null)
            {
                ApplyVideoFrame(frame, width, height);
            }

            Volatile.Write(ref _videoUpdateScheduled, 0);
            if (Volatile.Read(ref _pendingVideoFrame) is null ||
                Interlocked.CompareExchange(ref _videoUpdateScheduled, 1, 0) != 0)
            {
                return;
            }
        }

        Volatile.Write(ref _videoUpdateScheduled, 0);
    }

    private void ApplyVideoFrame(byte[] frame, int width, int height)
    {
        if (_videoBitmap is null || _videoClosed)
        {
            return;
        }

        try
        {
            // frame is tightly packed BGRA: width * height * 4 bytes.
            int srcRowBytes = checked(width * 4);
            int expectedFrameBytes = checked(srcRowBytes * height);
            if (frame.Length != expectedFrameBytes)
            {
                return;
            }

            using var fb = _videoBitmap.Lock();

            // Copy only the rows and bytes that exist in BOTH buffers. The
            // previous version bailed out whenever the locked framebuffer was
            // smaller than the decoded frame, and otherwise trusted
            // fb.RowBytes * height to stay inside the mapped region. A
            // WriteableBitmap resized between frames (the placeholder is 16x16
            // until ffmpeg reports real dimensions) therefore threw
            // ArgumentOutOfRangeException out of Marshal.Copy on every frame.
            // Clamping makes the copy total-size-safe in both directions.
            int copyRows = Math.Min(height, fb.Size.Height);
            int copyBytes = Math.Min(srcRowBytes, fb.RowBytes);
            if (copyRows <= 0 || copyBytes <= 0)
            {
                return;
            }

            for (int y = 0; y < copyRows; y++)
            {
                int srcOffset = y * srcRowBytes;
                long dstOffset = (long)y * fb.RowBytes;
                Marshal.Copy(frame, srcOffset, fb.Address + (nint)dstOffset, copyBytes);
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to apply a video frame to the preview surface.");
        }

        // A WriteableBitmap write is invisible until the Image that presents it
        // is invalidated, so without this the first frame was the only frame
        // the user ever saw.
        _videoImage?.InvalidateVisual();
    }

    private static bool TryProbeDimensions(string path, string ffmpeg, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=width,height");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("v:0");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add(path);

            using var process = Process.Start(startInfo);
            if (process is null) return false;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            using JsonDocument doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("streams", out JsonElement streams) &&
                streams.GetArrayLength() > 0)
            {
                JsonElement stream = streams[0];
                if (stream.TryGetProperty("width", out JsonElement w) &&
                    stream.TryGetProperty("height", out JsonElement h))
                {
                    width = w.GetInt32();
                    height = h.GetInt32();
                    return width > 0 && height > 0;
                }
            }
        }
        catch
        {
            // ffprobe is optional; the caller falls back to a fixed size.
        }
        return false;
    }

    private static string? ResolveFfmpeg()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "ffmpeg"),
            Path.Combine(baseDir, "lib", "snapx", "ffmpeg"),
            "ffmpeg"
        ];
        foreach (string candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return candidate;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string full = Path.Combine(dir, "ffmpeg");
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static string ReadTextPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(no file path)";
        }
        long maxBytes = 256 * 1024;
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
        {
            return ReadTextPrefix(path, (int)(maxBytes - 512));
        }
        return File.ReadAllText(path);
    }

    private static string ReadTextPrefix(string path, int maxChars)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var buffer = new char[maxChars];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private bool IsImageExtension(string ext) => ext is
        ".bmp" or ".gif" or ".ico" or ".jpeg" or ".jpg" or ".png" or ".tif" or ".tiff" or ".webp";

    private bool IsVideoExtension(string ext) => ext is
        ".avi" or ".m4v" or ".mkv" or ".mov" or ".mp4" or ".mpeg" or ".mpg" or ".ogv" or ".webm";

    private bool IsTextExtension(string ext) => ext is
        ".csv" or ".json" or ".log" or ".md" or ".rtf" or ".text" or ".txt" or ".xml" or ".yaml" or ".yml";

    /// <summary>
    /// Image-only preview surface with pixel-aware fit/zoom and pointer-aware
    /// panning. The displayed size participates in layout (rather than using a
    /// render transform) so the ScrollViewer always has a truthful extent.
    /// </summary>
    private sealed class ZoomableImagePreview : Grid, IDisposable
    {
        private const double MaximumZoom = 8.0;
        private const double DragThreshold = 5.0;
        private const double ZoomStep = 1.25;

        private readonly Bitmap _bitmap;
        private readonly ScrollViewer _scroll;
        private readonly Image _image;
        private readonly TextBlock _zoomText;
        private readonly Button _zoomOutButton;
        private readonly Button _zoomInButton;
        private readonly Cursor _clickCursor = new(StandardCursorType.Hand);
        private readonly Cursor _panCursor = new(StandardCursorType.SizeAll);
        private double _fitZoom = 1.0;
        private double _zoom = 1.0;
        private double _pinchStartZoom;
        private bool _fitMode = true;
        private bool _pinchActive;
        private bool _pointerDown;
        private bool _dragging;
        private Point _pressPoint;
        private Vector _pressOffset;
        private long _zoomRevision;
        private bool _disposed;

        public ZoomableImagePreview(Bitmap bitmap)
        {
            _bitmap = bitmap;
            Margin = new Thickness(16, 8, 16, 0);
            ClipToBounds = true;

            _image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = _clickCursor
            };

            _scroll = new ScrollViewer
            {
                Content = _image,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _scroll.SizeChanged += Scroll_OnSizeChanged;
            _scroll.PointerWheelChanged += Scroll_OnPointerWheelChanged;
            _scroll.PointerTouchPadGestureMagnify += Scroll_OnTouchPadMagnify;
            _scroll.Pinch += Scroll_OnPinch;
            _scroll.PinchEnded += Scroll_OnPinchEnded;
            _scroll.GestureRecognizers.Add(new PinchGestureRecognizer());

            _image.PointerPressed += Image_OnPointerPressed;
            _image.PointerMoved += Image_OnPointerMoved;
            _image.PointerReleased += Image_OnPointerReleased;
            _image.PointerCaptureLost += Image_OnPointerCaptureLost;

            _zoomOutButton = MakeZoomButton("-", "Zoom out", (_, _) => ZoomBy(1.0 / ZoomStep));
            _zoomInButton = MakeZoomButton("+", "Zoom in", (_, _) => ZoomBy(ZoomStep));
            _zoomText = new TextBlock
            {
                Width = 56,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontSize = 12
            };
            var fitButton = MakeZoomButton("Fit", "Fit the whole image in the preview", (_, _) => SetFitZoom());
            fitButton.Width = 44;

            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2
            };
            controls.Children.Add(_zoomOutButton);
            controls.Children.Add(_zoomText);
            controls.Children.Add(_zoomInButton);
            controls.Children.Add(fitButton);

            var controlsBackground = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 38, 38, 44)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Margin = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = controls
            };

            Children.Add(_scroll);
            Children.Add(controlsBackground);
            UpdateImageSize();
        }

        private static Button MakeZoomButton(string text, string tooltip, EventHandler<RoutedEventArgs> click)
        {
            var button = new Button
            {
                Content = text,
                Width = 32,
                Height = 28,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(button, tooltip);
            button.Click += click;
            return button;
        }

        private void Scroll_OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            double availableWidth = Math.Max(1, e.NewSize.Width);
            double availableHeight = Math.Max(1, e.NewSize.Height);
            double nextFit = Math.Min(1.0, Math.Min(
                availableWidth / Math.Max(1, _bitmap.Size.Width),
                availableHeight / Math.Max(1, _bitmap.Size.Height)));
            nextFit = Math.Clamp(nextFit, 0.01, 1.0);

            bool fitChanged = Math.Abs(nextFit - _fitZoom) > 0.0001;
            _fitZoom = nextFit;
            if (_fitMode && fitChanged)
            {
                ApplyZoom(_fitZoom, GetViewportCenter(), remainInFitMode: true);
            }
            else
            {
                UpdateInteractionState();
            }
        }

        private void Scroll_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            // Preserve unmodified wheel/two-finger scrolling for panning. A
            // command/control modifier turns it into pointer-anchored zoom,
            // matching image editors on macOS and the other desktop targets.
            if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0 || e.Delta.Y == 0)
            {
                return;
            }

            e.Handled = true;
            ZoomBy(e.Delta.Y > 0 ? ZoomStep : 1.0 / ZoomStep, e.GetPosition(_scroll));
        }

        private void Scroll_OnTouchPadMagnify(object? sender, PointerDeltaEventArgs e)
        {
            e.Handled = true;
            // Native macOS magnification arrives as a small signed delta.
            // Exponential scaling remains smooth for both very small and
            // coalesced events without ever producing a negative factor.
            double factor = Math.Exp(e.Delta.Y);
            ZoomBy(factor, e.GetPosition(_scroll));
        }

        private void Scroll_OnPinch(object? sender, PinchEventArgs e)
        {
            if (!_pinchActive)
            {
                _pinchActive = true;
                _pinchStartZoom = _zoom;
            }

            e.Handled = true;
            ApplyZoom(_pinchStartZoom * e.Scale, e.ScaleOrigin, remainInFitMode: false);
        }

        private void Scroll_OnPinchEnded(object? sender, PinchEndedEventArgs e)
        {
            _pinchActive = false;
            e.Handled = true;
        }

        private void Image_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_image).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _pointerDown = true;
            _dragging = false;
            _pressPoint = e.GetPosition(_scroll);
            _pressOffset = _scroll.Offset;
            e.Pointer.Capture(_image);
        }

        private void Image_OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_pointerDown)
            {
                return;
            }

            Point current = e.GetPosition(_scroll);
            Vector delta = current - _pressPoint;
            if (!_dragging && Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) >= DragThreshold && CanPan())
            {
                _dragging = true;
            }

            if (!_dragging)
            {
                return;
            }

            _scroll.Offset = ClampOffset(new Vector(_pressOffset.X - delta.X, _pressOffset.Y - delta.Y));
            e.Handled = true;
        }

        private void Image_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_pointerDown)
            {
                return;
            }

            bool wasDragging = _dragging;
            Point releasePoint = e.GetPosition(_scroll);
            EndPointerInteraction(e.Pointer);
            e.Handled = true;

            if (!wasDragging)
            {
                ToggleZoom(releasePoint);
            }
        }

        private void Image_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            _pointerDown = false;
            _dragging = false;
            UpdateInteractionState();
        }

        private void EndPointerInteraction(IPointer pointer)
        {
            _pointerDown = false;
            _dragging = false;
            pointer.Capture(null);
            UpdateInteractionState();
        }

        private void ToggleZoom(Point anchor)
        {
            if (_fitMode)
            {
                double usefulZoom = _fitZoom < 0.999 ? 1.0 : Math.Min(MaximumZoom, 2.0);
                ApplyZoom(usefulZoom, anchor, remainInFitMode: false);
            }
            else
            {
                SetFitZoom(anchor);
            }
        }

        private void ZoomBy(double factor, Point? anchor = null) =>
            ApplyZoom(_zoom * factor, anchor ?? GetViewportCenter(), remainInFitMode: false);

        private void SetFitZoom(Point? anchor = null) =>
            ApplyZoom(_fitZoom, anchor ?? GetViewportCenter(), remainInFitMode: true);

        private void ApplyZoom(double requestedZoom, Point anchor, bool remainInFitMode)
        {
            double nextZoom = Math.Clamp(requestedZoom, _fitZoom, MaximumZoom);
            double oldZoom = Math.Max(0.01, _zoom);
            Vector oldOffset = _scroll.Offset;
            Size viewport = GetViewportSize();
            Vector oldPadding = GetCenterPadding(oldZoom, viewport);
            double imageX = (anchor.X + oldOffset.X - oldPadding.X) / oldZoom;
            double imageY = (anchor.Y + oldOffset.Y - oldPadding.Y) / oldZoom;

            _zoom = nextZoom;
            _fitMode = remainInFitMode || Math.Abs(_zoom - _fitZoom) < 0.0001;
            UpdateImageSize();

            Vector newPadding = GetCenterPadding(_zoom, viewport);
            Vector requestedOffset = new(
                newPadding.X + imageX * _zoom - anchor.X,
                newPadding.Y + imageY * _zoom - anchor.Y);
            long revision = ++_zoomRevision;

            // Explicit image dimensions update the extent during the next
            // layout pass. Re-apply the pointer-anchored offset afterwards so
            // zooming does not make the detail under the cursor jump away.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && revision == _zoomRevision)
                {
                    _scroll.Offset = ClampOffset(requestedOffset);
                }
            }, DispatcherPriority.Loaded);
        }

        private void UpdateImageSize()
        {
            _image.Width = Math.Max(1, _bitmap.Size.Width * _zoom);
            _image.Height = Math.Max(1, _bitmap.Size.Height * _zoom);
            UpdateInteractionState();
        }

        private void UpdateInteractionState()
        {
            _zoomText.Text = $"{Math.Round(_zoom * 100):0}%";
            _zoomOutButton.IsEnabled = _zoom > _fitZoom + 0.0001;
            _zoomInButton.IsEnabled = _zoom < MaximumZoom - 0.0001;
            _image.Cursor = CanPan() ? _panCursor : _clickCursor;
            ToolTip.SetTip(_image, CanPan()
                ? "Drag to move the image. Select the image to fit it in the preview. Pinch or use Command or Ctrl and the scroll wheel to change the zoom."
                : "Select the image to change the zoom. Pinch or use Command or Ctrl and the scroll wheel to change the zoom.");
        }

        private bool CanPan()
        {
            Size viewport = GetViewportSize();
            return _bitmap.Size.Width * _zoom > viewport.Width + 0.5 ||
                   _bitmap.Size.Height * _zoom > viewport.Height + 0.5;
        }

        private Point GetViewportCenter()
        {
            Size viewport = GetViewportSize();
            return new Point(viewport.Width / 2.0, viewport.Height / 2.0);
        }

        private Size GetViewportSize()
        {
            Size viewport = _scroll.Viewport;
            if (viewport.Width <= 0 || viewport.Height <= 0)
            {
                viewport = _scroll.Bounds.Size;
            }
            return new Size(Math.Max(1, viewport.Width), Math.Max(1, viewport.Height));
        }

        private Vector GetCenterPadding(double zoom, Size viewport) => new(
            Math.Max(0, (viewport.Width - _bitmap.Size.Width * zoom) / 2.0),
            Math.Max(0, (viewport.Height - _bitmap.Size.Height * zoom) / 2.0));

        private Vector ClampOffset(Vector offset)
        {
            Size viewport = GetViewportSize();
            double maxX = Math.Max(0, _bitmap.Size.Width * _zoom - viewport.Width);
            double maxY = Math.Max(0, _bitmap.Size.Height * _zoom - viewport.Height);
            return new Vector(Math.Clamp(offset.X, 0, maxX), Math.Clamp(offset.Y, 0, maxY));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scroll.SizeChanged -= Scroll_OnSizeChanged;
            _scroll.PointerWheelChanged -= Scroll_OnPointerWheelChanged;
            _scroll.PointerTouchPadGestureMagnify -= Scroll_OnTouchPadMagnify;
            _scroll.Pinch -= Scroll_OnPinch;
            _scroll.PinchEnded -= Scroll_OnPinchEnded;
            _image.PointerPressed -= Image_OnPointerPressed;
            _image.PointerMoved -= Image_OnPointerMoved;
            _image.PointerReleased -= Image_OnPointerReleased;
            _image.PointerCaptureLost -= Image_OnPointerCaptureLost;
            _image.Source = null;
            _bitmap.Dispose();
            _clickCursor.Dispose();
            _panCursor.Dispose();
        }
    }

    private void Owner_OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _videoClosed = true;
        Interlocked.Exchange(ref _pendingVideoFrame, null);

        try
        {
            if (_videoProcess is { } process)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                process.Dispose();
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to stop the in-app video decoder.");
        }

        _owner.Closed -= Owner_OnClosed;
        _owner.Resized -= Owner_OnResized;
        _ownerClientSizeSubscription?.Dispose();
        _ownerClientSizeSubscription = null;
        _overlay.SizeChanged -= Overlay_OnSizeChanged;
        _scrim.KeyDown -= Scrim_OnKeyDown;
        _overlay.Children.Remove(_scrim);

        // Drop every reference the removed subtree still holds to GPU/decoder
        // resources. Removing the scrim detaches it from the visual tree, but
        // the Image would otherwise keep the WriteableBitmap alive (and thus a
        // composition surface referencing it) until the next GC.
        if (_videoImage is not null)
        {
            _videoImage.Source = null;
        }
        _scrim.Child = null;
        _imagePreview?.Dispose();
        _imagePreview = null;
        WriteableBitmap? videoBitmap = _videoBitmap;
        _videoBitmap = null;
        _videoImage = null;
        videoBitmap?.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
