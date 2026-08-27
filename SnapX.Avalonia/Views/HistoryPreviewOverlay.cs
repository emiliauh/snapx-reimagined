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
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(16, 8, 16, 0)
        };

        if (_isImage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_item.FilePath))
                {
                    scroll.Content = MakeUnavailableText("The image path is missing.");
                }
                else
                {
                    var bitmap = new Bitmap(_item.FilePath);
                    var image = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    scroll.Content = image;
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Failed to decode the history image.");
                scroll.Content = MakeUnavailableText($"Unable to decode the image: {ex.Message}");
            }
            return scroll;
        }

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
                scroll.Content = MakeUnavailableText($"Unable to read the text file: {ex.Message}");
            }
            return scroll;
        }

        scroll.Content = MakeUnavailableText("This file type cannot be previewed inside SnapX.");
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
        WriteableBitmap? videoBitmap = _videoBitmap;
        _videoBitmap = null;
        _videoImage = null;
        videoBitmap?.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
