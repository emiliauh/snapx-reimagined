using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.Media;
using SnapX.Core.ScreenCapture;
using SnapX.Core.Upload;
using SnapX.Core.Utils.Native;
using SnapX.Avalonia.Views.Controls;
using DesktopPoint = SixLabors.ImageSharp.Point;
using DesktopRectangle = SixLabors.ImageSharp.Rectangle;
using CapturedImage = SixLabors.ImageSharp.Image;
using IPlatformHandle = Avalonia.Platform.IPlatformHandle;

namespace SnapX.Avalonia.Views;

public partial class RegionSelectorWindow
{
    private const string CoreGraphicsFramework =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const int ScreenSaverWindowLevelKey = 13;
    private const nuint CanJoinAllSpaces = 1 << 0;
    private const nuint FullScreenAuxiliary = 1 << 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public NativePoint Origin;
        public NativeSize Size;
    }

    [DllImport(CoreGraphicsFramework)]
    private static extern int CGWindowLevelForKey(int key);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendObjectiveCInteger(IntPtr receiver, IntPtr selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendObjectiveCBool(IntPtr receiver, IntPtr selector, byte value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendObjectiveCRect(
        IntPtr receiver,
        IntPtr selector,
        NativeRect frame,
        byte display);

    private MacOSLiveSelectionSession? _liveSession;

    private async Task<bool> PrepareLiveDisplaysAsync(CancellationToken cancellationToken)
    {
        if (!TryAcquireSelectorGate())
        {
            DebugHelper.WriteLine("A region selector is already active; ignoring the duplicate request.");
            return false;
        }
        try
        {
            if (cancellationToken.IsCancellationRequested || IsCancellationRequested)
            {
                ReleaseSelectorGate();
                return false;
            }
            var screens = MacOSAPI.GetScreens();
            if (screens.Count == 0) throw new InvalidOperationException("No active displays are available.");
            var cursor = Methods.GetCursorPosition();
            screens = screens.OrderByDescending(screen => screen.Bounds.Contains(cursor)).ToList();
            _liveSession = new MacOSLiveSelectionSession(this, screens);
            return await _liveSession.ShowAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _liveSession?.Cancel();
            RestoreHiddenWindows();
            ReleaseSelectorGate();
            ShowErrorDialog(ex);
            return false;
        }
    }

    /// <summary>
    /// One result and one selector gate, with an input surface on each display.
    /// Screenshot selection presents frames captured before the surfaces are
    /// mapped; recording geometry selection stays live and transparent.
    /// Selection geometry always stays in CoreGraphics desktop points.
    /// </summary>
    private sealed class MacOSLiveSelectionSession
    {
        private readonly RegionSelectorWindow _owner;
        private readonly List<Screen> _screens;
        private readonly List<RegionSelectorWindow> _overlays = [];
        private readonly Dictionary<RegionSelectorWindow, TaskCompletionSource<bool>> _opened = [];
        private readonly Dictionary<RegionSelectorWindow, FrozenDisplayFrame> _frozenFrames = [];
        private List<WindowInfo> _windows = [];
        private DesktopPoint _start;
        private DesktopRectangle _selection;
        private IPointer? _capturedPointer;
        private WindowInfo? _hoveredWindow;
        private bool _ready;
        private bool _dragging;
        private bool _annotating;
        private bool _regionSelectionMode;
        private bool _completed;
        private bool _cancelled;
        private bool _closing;
        private DesktopRectangle _committedSelection;
        private CapturedImage? _annotationSource;
        private readonly List<AnnotationCanvas> _annotationCanvases = [];
        private RegionAnnotationToolbar? _annotationToolbar;

        public DesktopRectangle DesktopBounds { get; }
        public WindowInfo? SelectedWindow { get; private set; }
        private bool CanPickWindows => _owner._captureOptions.WindowPickerMode ||
            _owner._captureOptions.WindowOrRegionPickerMode ||
            (_owner._captureOptions.DetectWindows && !_owner._captureOptions.IsFixedSize && !_owner._captureOptions.MonitorPickerMode);

        private sealed class FrozenDisplayFrame(Screen screen, CapturedImage source, Bitmap preview) : IDisposable
        {
            public Screen Screen { get; } = screen;
            public CapturedImage Source { get; } = source;
            public Bitmap Preview { get; } = preview;

            public void Dispose()
            {
                Preview.Dispose();
                Source.Dispose();
            }
        }

        public MacOSLiveSelectionSession(RegionSelectorWindow owner, List<Screen> screens)
        {
            _owner = owner;
            _owner._resultImg = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _owner._resultRect = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _screens = screens;
            DesktopBounds = screens.Select(screen => screen.Bounds).Aggregate(DesktopRectangle.Union);
            for (int index = 0; index < screens.Count; index++)
            {
                var overlay = index == 0 ? owner : new RegionSelectorWindow(owner.IsSilentMode, owner.TakeScreenshot)
                {
                    _captureOptions = owner._captureOptions
                };
                overlay._liveSession = this;
                overlay.ShowActivated = index == 0;
                var bounds = screens[index].Bounds;
                overlay.Position = new PixelPoint(bounds.X, bounds.Y);
                overlay.Width = bounds.Width;
                overlay.Height = bounds.Height;
                overlay._screenBounds = new PixelRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                overlay._requestedScreenBounds = overlay._screenBounds;
                overlay._canvas.Width = bounds.Width;
                overlay._canvas.Height = bounds.Height;
                overlay._imageBounds = new Rect(0, 0, bounds.Width, bounds.Height);
                if (overlay._canvas.Parent is Viewbox viewbox)
                {
                    viewbox.Width = bounds.Width;
                    viewbox.Height = bounds.Height;
                }
                overlay.WindowState = global::Avalonia.Controls.WindowState.Normal;
                overlay.Background = owner.TakeScreenshot ? Brushes.Black : Brushes.Transparent;
                overlay._selectionRect.IsVisible = false;
                overlay._infoBox.IsVisible = false;
                overlay._preparedForDisplay = true;
                overlay._captureReady = false;
                overlay.Opacity = 1;
                _overlays.Add(overlay);
                _opened[overlay] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public async Task<bool> ShowAsync(CancellationToken cancellationToken)
        {
            _owner.HideSnapXWindows();
            // AppKit commits Hide asynchronously. Wait one compositor frame so
            // SnapX itself is absent from every frozen display frame.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(32);
            if (_cancelled || cancellationToken.IsCancellationRequested) { Cancel(); return false; }

            // Enumerate before mapping any sibling so no overlay can be picked.
            if (CanPickWindows)
            {
                _windows = Methods.GetWindowList()
                    .Where(window => window.ProcessId != Environment.ProcessId && window.IsVisible && !window.Rectangle.IsEmpty)
                    .ToList(); // CoreGraphics returns front-to-back stacking order.
            }
            if (_owner.TakeScreenshot && !await CaptureFrozenDisplaysAsync(cancellationToken))
            {
                Cancel();
                return false;
            }
            foreach (var overlay in _overlays)
            {
                if (_cancelled || cancellationToken.IsCancellationRequested) { Cancel(); return false; }
                overlay.Show();
            }
            var outcomes = await Task.WhenAll(_opened.Values.Select(source => source.Task));
            if (_cancelled || cancellationToken.IsCancellationRequested || outcomes.Any(opened => !opened))
            {
                Cancel();
                return false;
            }
            if (_owner.TakeScreenshot && _owner._annotationRequested)
                InitializeLiveAnnotation();
            if (_cancelled) return false;
            _ready = true;
            _owner.Activate();
            _owner.Focus();
            DebugHelper.WriteLine(_owner.TakeScreenshot
                ? $"Frozen macOS selector ready across {_overlays.Count} displays; all frames were captured before overlays were mapped."
                : $"Live macOS geometry selector ready across {_overlays.Count} displays; no screenshot was requested.");
            return true;
        }

        private async Task<bool> CaptureFrozenDisplaysAsync(CancellationToken cancellationToken)
        {
            Task<CapturedImage?>[] captures = _screens
                .Select(screen => Methods.CaptureRectangle(screen.Bounds))
                .ToArray();
            CapturedImage?[] images;
            try
            {
                images = await Task.WhenAll(captures);
            }
            catch
            {
                foreach (Task<CapturedImage?> capture in captures)
                {
                    if (capture.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                        capture.Result?.Dispose();
                }
                throw;
            }

            try
            {
                if (_cancelled || cancellationToken.IsCancellationRequested)
                    return false;

                for (int index = 0; index < _overlays.Count; index++)
                {
                    CapturedImage source = images[index]
                        ?? throw new InvalidOperationException($"Display {_screens[index].Index} could not be captured.");
                    images[index] = null;
                    Bitmap preview;
                    try
                    {
                        preview = App.SnapX.ConvertImageSharpImgToAvalonia(source);
                    }
                    catch
                    {
                        source.Dispose();
                        throw;
                    }

                    var frame = new FrozenDisplayFrame(_screens[index], source, preview);
                    RegionSelectorWindow overlay = _overlays[index];
                    _frozenFrames.Add(overlay, frame);
                    // Put the frozen pixels on the selection canvas itself.
                    // Window.Background also feeds the native window clear
                    // color; an ImageBrush there does not reliably paint the
                    // opaque macOS surface with every window theme/backend.
                    // The canvas has explicit desktop-point dimensions, so its
                    // image and selection geometry use the same transform.
                    overlay._canvas.Background = new ImageBrush
                    {
                        Source = preview,
                        Stretch = Stretch.Fill
                    };
                    DebugHelper.WriteLine(
                        $"Frozen display {_screens[index].Index}: points={_screens[index].Bounds}; pixels={source.Width}x{source.Height}.");
                }

                return true;
            }
            finally
            {
                foreach (CapturedImage? image in images)
                    image?.Dispose();
            }
        }

        public async Task OverlayOpenedAsync(RegionSelectorWindow overlay)
        {
            try
            {
                overlay.IsVisible = true;
                await Task.Yield();
                overlay.ApplyMacOSFullDisplayFrame();
                await overlay.SynchronizeLiveWindowBoundsAsync();
                if (_cancelled) { _opened[overlay].TrySetResult(false); return; }
                overlay._captureReady = true;
                _opened[overlay].TrySetResult(true);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "A macOS display overlay could not determine its bounds");
                _opened[overlay].TrySetResult(false);
                Cancel();
            }
        }

        public void PointerPressed(RegionSelectorWindow overlay, PointerPressedEventArgs e)
        {
            if (!_ready || _completed || _cancelled || (_annotating && !_regionSelectionMode)) return;
            var properties = e.GetCurrentPoint(overlay._canvas).Properties;
            if (properties.IsRightButtonPressed) { Cancel(); e.Handled = true; return; }
            if (!properties.IsLeftButtonPressed) return;
            var point = Methods.GetCursorPosition();
            UpdateHover(point, preserveCommittedSelection: false);
            if (_owner._captureOptions.WindowPickerMode && _hoveredWindow is null)
            {
                e.Handled = true;
                return;
            }
            _start = point;
            _selection = DesktopRectangle.Empty;
            SelectedWindow = null;
            _annotationToolbar?.SetCanAccept(false);
            _dragging = true;
            _capturedPointer = e.Pointer;
            e.Pointer.Capture(overlay._canvas);
            e.Handled = true;
            if (_owner._captureOptions.WindowPickerMode && _hoveredWindow is { } window)
            {
                SelectedWindow = window;
                _ = CompleteAsync(window.Rectangle);
            }
            else if (_owner._captureOptions.MonitorPickerMode)
            {
                var screen = _screens.FirstOrDefault(candidate => candidate.Bounds.Contains(point));
                if (screen != null) _ = CompleteAsync(screen.Bounds);
            }
        }

        public void PointerMoved(PointerEventArgs e)
        {
            if (!_ready || _completed || _cancelled || (_annotating && !_regionSelectionMode)) return;
            var point = Methods.GetCursorPosition();
            if (_dragging)
            {
                _selection = GetDragRectangle(point);
                DrawSelection(_selection, point);
            }
            else UpdateHover(point);
            e.Handled = true;
        }

        public async Task PointerReleasedAsync(PointerReleasedEventArgs? e)
        {
            if (!_dragging || _completed || _cancelled || (_annotating && !_regionSelectionMode)) return;
            var point = Methods.GetCursorPosition();
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            _dragging = false;
            if (e != null) e.Handled = true;
            if (CanPickWindows && !_owner._captureOptions.IsFixedSize &&
                !IsDragBeyondThreshold(new Point(_start.X, _start.Y), new Point(point.X, point.Y)))
            {
                UpdateHover(point);
                if (_hoveredWindow is { } window)
                {
                    SelectedWindow = window;
                    await CompleteAsync(window.Rectangle);
                    return;
                }
            }
            await CompleteAsync(GetDragRectangle(point));
        }

        public void KeyDown(KeyEventArgs e)
        {
            if (_annotating && e.Key == Key.Enter && _dragging)
            {
                _ = PointerReleasedAsync(null);
                e.Handled = true;
                return;
            }
            if (_annotating && _annotationToolbar is not null)
            {
                _annotationToolbar.HandleKeyDown(e);
            }
            else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
            else if (e.Key == Key.Enter && _dragging)
            {
                _ = PointerReleasedAsync(null);
                e.Handled = true;
            }
        }

        private DesktopRectangle GetDragRectangle(DesktopPoint end)
        {
            int x = Math.Min(_start.X, end.X), y = Math.Min(_start.Y, end.Y);
            int width = Math.Abs(_start.X - end.X), height = Math.Abs(_start.Y - end.Y);
            var options = _owner._captureOptions;
            if (options.IsFixedSize && options.FixedSize.Width > 0 && options.FixedSize.Height > 0)
            {
                width = options.FixedSize.Width;
                height = options.FixedSize.Height;
                x = end.X < _start.X ? _start.X - width : _start.X;
                y = end.Y < _start.Y ? _start.Y - height : _start.Y;
            }
            return DesktopRectangle.Intersect(new DesktopRectangle(x, y, width, height), DesktopBounds);
        }

        private void UpdateHover(DesktopPoint point, bool preserveCommittedSelection = true)
        {
            if (preserveCommittedSelection && _annotating && !_committedSelection.IsEmpty)
            {
                DrawSelection(_committedSelection, point);
                return;
            }
            _hoveredWindow = CanPickWindows ? _windows.FirstOrDefault(window => window.Rectangle.Contains(point)) : null;
            var rectangle = _owner._captureOptions.MonitorPickerMode
                ? _screens.FirstOrDefault(screen => screen.Bounds.Contains(point))?.Bounds ?? DesktopRectangle.Empty
                : _hoveredWindow?.Rectangle ?? DesktopRectangle.Empty;
            DrawSelection(rectangle, point);
        }

        private void DrawSelection(DesktopRectangle rectangle, DesktopPoint cursor)
        {
            foreach (var overlay in _overlays)
            {
                var bounds = new DesktopRectangle(overlay._screenBounds.X, overlay._screenBounds.Y,
                    overlay._screenBounds.Width, overlay._screenBounds.Height);
                var visible = DesktopRectangle.Intersect(rectangle, bounds);
                if (visible.Width <= 0 || visible.Height <= 0)
                {
                    overlay._selectionRect.IsVisible = false;
                    overlay._infoBox.IsVisible = false;
                    continue;
                }
                double scaleX = overlay.ClientSize.Width / bounds.Width;
                double scaleY = overlay.ClientSize.Height / bounds.Height;
                double x = (visible.X - bounds.X) * scaleX, y = (visible.Y - bounds.Y) * scaleY;
                overlay._selectionRect.Margin = new Thickness(x, y, 0, 0);
                overlay._selectionRect.Width = visible.Width * scaleX;
                overlay._selectionRect.Height = visible.Height * scaleY;
                overlay._selectionRect.IsVisible = true;
                overlay._infoBox.IsVisible = _owner._captureOptions.ShowInfo && bounds.Contains(cursor);
                overlay._infoBox.Text = $"X: {rectangle.X}, Y: {rectangle.Y}, Width: {rectangle.Width}, Height: {rectangle.Height}";
                overlay._infoBox.Margin = new Thickness(x, Math.Max(0, y - 30), 0, 0);
            }
        }

        private async Task CompleteAsync(DesktopRectangle rectangle)
        {
            if (_completed || _cancelled || (_annotating && !_regionSelectionMode)) return;
            rectangle = DesktopRectangle.Intersect(rectangle, DesktopBounds);
            int minimum = Math.Max(1, _owner._captureOptions.MinimumSize);
            if (rectangle.Width < minimum || rectangle.Height < minimum ||
                !_screens.Any(screen => screen.Bounds.IntersectsWith(rectangle)))
            {
                if (_annotating)
                {
                    _selection = _committedSelection;
                    DrawSelection(_committedSelection, Methods.GetCursorPosition());
                    _annotationToolbar?.SetCanAccept(!_committedSelection.IsEmpty);
                }
                else Cancel();
                return;
            }
            _dragging = false;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;

            if (_owner.TakeScreenshot && _owner._annotationRequested)
            {
                _committedSelection = rectangle;
                _selection = rectangle;
                SelectedWindow ??= _windows.FirstOrDefault(window => window.Rectangle.Contains(rectangle));
                DrawSelection(rectangle, Methods.GetCursorPosition());
                _annotationToolbar?.SetCanAccept(true);
                return;
            }

            await FinalizeSelectionAsync(rectangle, null, annotationCompleted: false);
        }

        private void InitializeLiveAnnotation()
        {
            if (_completed || _cancelled || _annotating) return;

            CapturedImage? image = null;
            try
            {
                FrozenDisplaySource[] sources = _frozenFrames.Values
                    .Select(frame => new FrozenDisplaySource(frame.Screen.Bounds, frame.Source))
                    .ToArray();
                image = FrozenRegionComposer.Compose(sources, DesktopBounds);
                if (_cancelled) return;

                _annotationSource = image;
                image = null;
                _annotating = true;
                _regionSelectionMode = true;

                var document = new AnnotationDocument();
                foreach (RegionSelectorWindow overlay in _overlays)
                {
                    var bounds = new DesktopRectangle(
                        overlay._screenBounds.X,
                        overlay._screenBounds.Y,
                        overlay._screenBounds.Width,
                        overlay._screenBounds.Height);
                    double scaleX = overlay.ClientSize.Width / bounds.Width;
                    double scaleY = overlay.ClientSize.Height / bounds.Height;
                    var annotationCanvas = new AnnotationCanvas(_annotationSource, document, createPreview: false)
                    {
                        Width = overlay.ClientSize.Width,
                        Height = overlay.ClientSize.Height,
                        DrawBackground = false,
                        DrawSourceImage = false,
                        IsHitTestVisible = false,
                        ImageViewport = new Rect(
                            (DesktopBounds.X - bounds.X) * scaleX,
                            (DesktopBounds.Y - bounds.Y) * scaleY,
                            DesktopBounds.Width * scaleX,
                            DesktopBounds.Height * scaleY)
                    };
                    Canvas.SetLeft(annotationCanvas, 0);
                    Canvas.SetTop(annotationCanvas, 0);
                    annotationCanvas.SetValue(Panel.ZIndexProperty, 20);
                    overlay._selectionRect.SetValue(Panel.ZIndexProperty, 30);
                    overlay._canvas.Children.Add(annotationCanvas);
                    overlay.Cursor = Cursor.Default;
                    overlay._infoBox.IsVisible = false;
                    _annotationCanvases.Add(annotationCanvas);
                }

                if (_annotationCanvases.Count == 0)
                    throw new InvalidOperationException("The selected region does not intersect an annotation surface.");

                _annotationToolbar = new RegionAnnotationToolbar(_annotationCanvases, enableRegionSelection: true);
                _annotationToolbar.Accepted += (_, _) => _ = FinalizeAnnotationAsync();
                _annotationToolbar.Cancelled += (_, _) => Cancel();
                _annotationToolbar.RegionSelectionRequested += (_, _) => SetRegionSelectionMode(true);
                _annotationToolbar.AnnotationToolSelected += (_, _) => SetRegionSelectionMode(false);

                RegionSelectorWindow toolbarOverlay = FindToolbarOverlay(Methods.GetCursorPosition());
                double toolbarWidth = Math.Max(1, toolbarOverlay.ClientSize.Width - 24);
                _annotationToolbar.Width = Math.Min(900, toolbarWidth);
                Canvas.SetLeft(_annotationToolbar, Math.Max(12, (toolbarOverlay.ClientSize.Width - _annotationToolbar.Width) / 2));
                Canvas.SetTop(_annotationToolbar, 12);
                _annotationToolbar.SetValue(Panel.ZIndexProperty, 100);
                _annotationToolbar.Moved += (_, movement) =>
                {
                    if (_annotationToolbar is null) return;
                    double width = _annotationToolbar.Bounds.Width > 0
                        ? _annotationToolbar.Bounds.Width
                        : _annotationToolbar.Width;
                    double height = _annotationToolbar.Bounds.Height > 0
                        ? _annotationToolbar.Bounds.Height
                        : 50;
                    double x = Math.Clamp(
                        Canvas.GetLeft(_annotationToolbar) + movement.DeltaX,
                        0,
                        Math.Max(0, toolbarOverlay.ClientSize.Width - width));
                    double y = Math.Clamp(
                        Canvas.GetTop(_annotationToolbar) + movement.DeltaY,
                        0,
                        Math.Max(0, toolbarOverlay.ClientSize.Height - height));
                    Canvas.SetLeft(_annotationToolbar, x);
                    Canvas.SetTop(_annotationToolbar, y);
                };
                toolbarOverlay._canvas.Children.Add(_annotationToolbar);
                SetRegionSelectionMode(true);
                toolbarOverlay.Activate();
                toolbarOverlay.Focus();
                DebugHelper.WriteLine(
                    $"macOS frozen selector started with live annotation across {DesktopBounds}; surfaces={_annotationCanvases.Count}.");
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Inline region annotation could not start");
                ShowErrorDialog(ex);
                Cancel();
            }
            finally
            {
                image?.Dispose();
            }
        }

        private void SetRegionSelectionMode(bool selectingRegion)
        {
            _regionSelectionMode = selectingRegion;
            foreach (AnnotationCanvas canvas in _annotationCanvases)
                canvas.IsHitTestVisible = !selectingRegion;
            foreach (RegionSelectorWindow overlay in _overlays)
                overlay.Cursor = selectingRegion
                    ? new Cursor(StandardCursorType.Cross)
                    : Cursor.Default;
            if (selectingRegion)
                _owner.Focus();
            else
                _annotationCanvases[0].Focus();
        }

        private RegionSelectorWindow FindToolbarOverlay(DesktopPoint point)
        {
            return _overlays.FirstOrDefault(overlay =>
            {
                var bounds = new DesktopRectangle(
                    overlay._screenBounds.X, overlay._screenBounds.Y,
                    overlay._screenBounds.Width, overlay._screenBounds.Height);
                return bounds.Contains(point);
            }) ?? _owner;
        }

        private async Task FinalizeAnnotationAsync()
        {
            if (!_annotating || _completed || _cancelled || _annotationSource is null ||
                _committedSelection.IsEmpty) return;
            CapturedImage? output = null;
            try
            {
                _completed = true;
                _annotating = false;
                FrozenDisplaySource[] sources = _frozenFrames.Values
                    .Select(frame => new FrozenDisplaySource(frame.Screen.Bounds, frame.Source))
                    .ToArray();
                output = FrozenRegionComposer.Compose(sources, _committedSelection);
                AnnotationDocument document = _annotationCanvases[0].Document;
                if (document.Elements.Count > 0)
                {
                    float scaleX = _annotationSource.Width / (float)DesktopBounds.Width;
                    float scaleY = _annotationSource.Height / (float)DesktopBounds.Height;
                    var sourceArea = new SixLabors.ImageSharp.RectangleF(
                        (_committedSelection.X - DesktopBounds.X) * scaleX,
                        (_committedSelection.Y - DesktopBounds.Y) * scaleY,
                        _committedSelection.Width * scaleX,
                        _committedSelection.Height * scaleY);
                    AnnotationDocument transformed = document.Transform(sourceArea, output.Width, output.Height);
                    CapturedImage rendered = transformed.Render(output);
                    output.Dispose();
                    output = rendered;
                }
                await FinalizeSelectionAsync(_committedSelection, output, annotationCompleted: true);
                output = null;
            }
            catch (Exception ex)
            {
                output?.Dispose();
                DebugHelper.WriteException(ex, "Inline region annotation could not be rendered");
                ShowErrorDialog(ex);
                Cancel();
            }
        }

        private Task FinalizeSelectionAsync(
            DesktopRectangle rectangle,
            CapturedImage? preparedImage,
            bool annotationCompleted)
        {
            if (_cancelled)
            {
                preparedImage?.Dispose();
                return Task.CompletedTask;
            }
            _completed = true;
            _owner._selectionCompleted = true;
            foreach (RegionSelectorWindow overlay in _overlays) overlay.Hide();
            CapturedImage? image = preparedImage;
            try
            {
                if (_owner.TakeScreenshot && image is null)
                {
                    FrozenDisplaySource[] sources = _frozenFrames.Values
                        .Select(frame => new FrozenDisplaySource(frame.Screen.Bounds, frame.Source))
                        .ToArray();
                    image = FrozenRegionComposer.Compose(sources, rectangle);
                }
                _owner._selectionSucceeded = true;
                _owner._annotationCompleted = annotationCompleted;
                _owner._resultRect.TrySetResult(rectangle);
                _owner._resultImg.TrySetResult(image);
                if (image != null && !_owner.IsSilentMode)
                    UploadManager.RunImageTask(image, TaskSettings.GetDefaultTaskSettings());
                image = null;
                DebugHelper.WriteLine(
                    $"macOS multi-display selection complete from {(_owner.TakeScreenshot ? "frozen frames" : "geometry")}: " +
                    $"{rectangle}; inlineAnnotation={annotationCompleted}; overlays={_overlays.Count}");
            }
            catch (Exception ex)
            {
                _owner._resultRect.TrySetResult(null);
                _owner._resultImg.TrySetResult(null);
                DebugHelper.WriteException(ex, "Multi-display region capture failed");
                ShowErrorDialog(ex);
            }
            finally
            {
                image?.Dispose();
                CloseAll();
            }
            return Task.CompletedTask;
        }

        public void Cancel()
        {
            if (_closing) return;
            _cancelled = true;
            _completed = true;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            foreach (var overlay in _overlays)
            {
                Interlocked.Exchange(ref overlay._cancellationRequested, 1);
                _opened[overlay].TrySetResult(false);
            }
            _owner._resultRect.TrySetResult(null);
            _owner._resultImg.TrySetResult(null);
            CloseAll();
        }

        public void OverlayClosed(RegionSelectorWindow overlay)
        {
            _opened[overlay].TrySetResult(false);
            if (!_closing) Cancel();
            else if (overlay == _owner)
            {
                // XAML's Closed handler runs before result waiters registered by
                // callers, so all siblings and the gate are settled first.
                _owner.RestoreHiddenWindows();
                _owner.ReleaseSelectorGate();
            }
        }

        private void CloseAll()
        {
            if (_closing) return;
            _closing = true;
            foreach (AnnotationCanvas canvas in _annotationCanvases) canvas.Dispose();
            _annotationCanvases.Clear();
            _annotationSource?.Dispose();
            _annotationSource = null;
            foreach (var overlay in _overlays.Where(window => window != _owner)) overlay.Close();
            _owner.Close();
            foreach (var overlay in _overlays)
            {
                overlay._canvas.Background = Brushes.Transparent;
                overlay.Background = _owner.TakeScreenshot ? Brushes.Black : Brushes.Transparent;
            }
            foreach (FrozenDisplayFrame frame in _frozenFrames.Values) frame.Dispose();
            _frozenFrames.Clear();
            _owner.RestoreHiddenWindows();
            _owner.ReleaseSelectorGate();
        }
    }

    private void ApplyMacOSFullDisplayFrame()
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is not { HandleDescriptor: "NSWindow" } || handle.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The macOS selector NSWindow is unavailable.");
        }

        Screen primary = MacOSAPI.GetScreens().FirstOrDefault(screen => screen.IsPrimary)
            ?? throw new InvalidOperationException("The primary macOS display is unavailable.");
        PixelRect requested = _requestedScreenBounds;
        double appKitY = primary.Bounds.Bottom - (requested.Y + requested.Height);
        var frame = new NativeRect
        {
            Origin = new NativePoint { X = requested.X, Y = appKitY },
            Size = new NativeSize { Width = requested.Width, Height = requested.Height }
        };

        SendObjectiveCInteger(
            handle.Handle,
            GetObjectiveCSelector("setCollectionBehavior:"),
            (nint)(CanJoinAllSpaces | FullScreenAuxiliary));
        SendObjectiveCInteger(
            handle.Handle,
            GetObjectiveCSelector("setLevel:"),
            CGWindowLevelForKey(ScreenSaverWindowLevelKey));
        SendObjectiveCBool(
            handle.Handle,
            GetObjectiveCSelector("setIgnoresMouseEvents:"),
            0);
        SendObjectiveCBool(
            handle.Handle,
            GetObjectiveCSelector("setAcceptsMouseMovedEvents:"),
            1);
        SendObjectiveCBool(
            handle.Handle,
            GetObjectiveCSelector("setOpaque:"),
            TakeScreenshot ? (byte)1 : (byte)0);
        SendObjectiveCRect(
            handle.Handle,
            GetObjectiveCSelector("setFrame:display:"),
            frame,
            1);
    }
}
