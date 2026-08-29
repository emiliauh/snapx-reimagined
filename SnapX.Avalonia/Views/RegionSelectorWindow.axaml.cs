using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SnapX.Avalonia.ViewModels;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.Media;
using SnapX.Core.ScreenCapture;
using SnapX.Core.Upload;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Native;
using SnapX.Core.ImageEffects.Annotations;
using FluentIcons.Avalonia.Fluent;
using FluentIcons.Common;
using Image = SixLabors.ImageSharp.Image;
using Point = Avalonia.Point;
using PointF = SixLabors.ImageSharp.PointF;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using WindowState = Avalonia.Controls.WindowState;
using SharpColor = SixLabors.ImageSharp.Color;
using AvaloniaColor = Avalonia.Media.Color;

namespace SnapX.Avalonia.Views;

public partial class RegionSelectorWindow : Window
{
    private const string NativePickerHelperName = "snapx-picker";
    private static int selectorOpen;
    private Point _startPoint;
    private const int DragDistanceSquaredLimit = 25;
    private Point _pressedPoint;
    private bool _isSelecting;
    private bool _selectionCompleted;
    private bool _captureReady;
    private bool _ownsSelector;
    private int _selectorGateReleased;
    private int _cancellationRequested;

    private readonly Rectangle _selectionRect;
    private readonly TextBox _infoBox;
    private readonly Canvas _canvas;
    private readonly Panel? _cursorMarker;
    private Image? _image;
    private List<WindowInfo> _pickableWindows = [];
    private WindowInfo? _hoveredWindow;
    private Stream? _imageStream;
    private Rect _imageBounds;
    private PixelRect _screenBounds;
    private List<Window> windowsHiddenByUs = [];
    private TaskCompletionSource<Image?> _resultImg = new();
    private TaskCompletionSource<SixLabors.ImageSharp.Rectangle?> _resultRect = new();
    private RegionCaptureOptions _captureOptions = new();
    private bool _preparedForDisplay;
    private bool _layoutApplied;
    private readonly Lock _preparationLock = new();
    private Task<bool>? _preparationTask;
    private bool IsSilentMode { get; set; } = false;

    private bool TakeScreenshot { get; set; } = true;
    // Inline (non-modal) annotation state: set after the region is selected and
    // its image cropped, before the capture is committed. Lets the user annotate
    // the capture in place (ShareX screenshot-editor style), then Save composites
    // the marks or Cancel commits the plain image.
    private readonly List<ImageAnnotation> _annotations = [];
    private readonly Stack<ImageAnnotation> _undoStack = new();
    private AnnotationSurface? _annotationSurface;
    private Control? _annotationToolbar;
    private ImageAnnotation.Tool _annotationTool = ImageAnnotation.Tool.Rectangle;
    private string _annotationText = "";
    private bool _annotationMode;
    private WriteableBitmap? _annotationBitmap;
    // ShareX-style live annotate session: toolbar + marks on the frozen
    // desktop before the region is committed (QuickCrop on mouse-up).
    private bool _liveAnnotateSession;
    private bool _regionToolActive = true;
    private bool _highlightMode;
    private LiveAnnotationOverlay? _liveAnnotateOverlay;
    private Border? _liveToolbarHost;
    private Panel? _liveAnnotateHost;
    private global::Avalonia.Controls.Image? _backgroundImage;
    private TextBox? _annotationTextBox;
    private readonly Dictionary<ImageAnnotation.Tool, Button> _liveToolButtons = new();
    private double _captureScaleX = 1;
    private double _captureScaleY = 1;
    private long _lastPointerMoveTicks;
    private int _toolbarTopMargin = 30;
    private const int PointerMoveThrottleMs = 24;
    private const int DefaultTopBarLogicalHeight = 26;

    [ModuleInitializer]
    internal static void RegisterCoreRegionSelector()
    {
        RegionCaptureTasks.SetRegionSelector(SelectRegionForCoreAsync);
    }

    /// <summary>
    /// Free-form region capture that uses the ShareX-style frozen-desktop
    /// selector with a live floating toolbar (annotate before commit).
    /// </summary>
    private static bool NeedsLiveAnnotateSession(RegionCaptureOptions options) =>
        options.AnnotateCapture
        && !options.WindowPickerMode
        && !options.MonitorPickerMode
        && !options.WindowOrRegionPickerMode;


    private static async Task<RegionCaptureSelection?> SelectRegionForCoreAsync(
        RegionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (IsNativeWayland)
        {
            // This is deliberately completed before starting slurp. It removes
            // an in-window history preview (and its video-frame redraw loop)
            // without hiding/remapping the main Wayland toplevel.
            await HistoryPreviewOverlay.CloseForRegionCaptureAsync();
        }

        try
        {
            if (request.Options.WindowOrRegionPickerMode)
            {
                var pickerResult = await TrySelectWindowOrRegionNativeAsync(request, cancellationToken);
                if (pickerResult.Handled)
                {
                    return pickerResult.Selection;
                }

                DebugHelper.WriteLine(
                    "Native window-or-region picker is unavailable; retaining the existing Wayland region selector behavior.");
            }

            if (NeedsLiveAnnotateSession(request.Options))
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    return await SelectRegionForCoreOnUIThreadAsync(request, cancellationToken);
                }

                return await Dispatcher.UIThread.InvokeAsync(
                    () => SelectRegionForCoreOnUIThreadAsync(request, cancellationToken));
            }

            // slurp is a compositor-native Wayland selector. It displays only
            // its selection outline, then exits before grim captures the
            // chosen geometry. This avoids both an XWayland overlay and
            // mixed-DPI crop calculations on wlroots compositors.
            var slurpResult = await TrySelectRegionWithSlurpAsync(request, cancellationToken);
            if (slurpResult.Handled)
            {
                return slurpResult.Selection;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is a terminal selector outcome. Falling back to an
            // Avalonia overlay after the native selector was cancelled can
            // unexpectedly put a second selector over the user's desktop.
            return null;
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"Wayland region selection failed; falling back to the Avalonia selector: {ex.Message}");
        }

        // The Avalonia selector is a full-screen Window. On native Wayland it
        // maps a separate xdg-toplevel over a grim desktop mirror, which is
        // both visually wrong and crash-prone on the affected NVIDIA/Hyprland
        // renderer. slurp (plus InlineCaptureAnnotateWindow when needed) is the
        // native Wayland path; if slurp cannot complete, report cancellation.
        if (IsNativeWayland)
        {
            DebugHelper.WriteLine(
                "slurp did not provide a native Wayland selection; the Avalonia RegionSelectorWindow fallback is disabled.");
            return null;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            return await SelectRegionForCoreOnUIThreadAsync(request, cancellationToken);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => SelectRegionForCoreOnUIThreadAsync(request, cancellationToken));
    }

    private sealed record NativePickerResult(
        bool Available,
        bool Cancelled,
        string? SelectionKind,
        SixLabors.ImageSharp.Rectangle Rectangle);

    private static async Task<(bool Handled, RegionCaptureSelection? Selection)>
        TrySelectWindowOrRegionNativeAsync(
            RegionCaptureRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsNativeWayland)
        {
            return (false, null);
        }

        string? helper = ResolveNativePickerPath();
        if (helper is null)
        {
            DebugHelper.WriteLine("Native window-or-region picker helper was not found.");
            return (false, null);
        }

        List<WindowInfo> windows;
        List<WindowInfo> monitors;
        try
        {
            DebugHelper.WriteLine("Native window-or-region selector phase=query-layout.");
            (windows, monitors) = await Task.WhenAll(
                    GetHyprlandClientWindowsAsync(cancellationToken),
                    GetHyprlandMonitorsAsync(cancellationToken))
                .ContinueWith(tasks => (tasks.Result[0], tasks.Result[1]), cancellationToken,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugHelper.WriteLine($"Native window-or-region selector layout query failed: {ex.Message}");
            return (false, null);
        }

        if (monitors.Count == 0)
        {
            DebugHelper.WriteLine("Native window-or-region selector found no Hyprland outputs.");
            return (false, null);
        }

        WindowInfo? cursorMonitor = ResolveHyprlandMonitorForCursor(monitors);
        var activeMonitors = monitors
            .Where(monitor =>
                ReferenceEquals(monitor, cursorMonitor) ||
                windows.Any(window => window.Rectangle.IntersectsWith(monitor.Rectangle)))
            .Distinct()
            .ToList();
        if (activeMonitors.Count == 0 && cursorMonitor is not null)
        {
            activeMonitors.Add(cursorMonitor);
        }

        DebugHelper.WriteLine(
            $"Native window-or-region selector phase=launch outputs={activeMonitors.Count} windows={windows.Count}.");
        using var pickerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = activeMonitors
            .Select(monitor => RunNativePickerProcessAsync(
                helper, monitor, windows, pickerCancellation.Token))
            .ToList();

        NativePickerResult? completedSelection = null;
        while (pending.Count > 0)
        {
            Task<NativePickerResult> completedTask = await Task.WhenAny(pending);
            pending.Remove(completedTask);
            NativePickerResult result = await completedTask;
            if (!result.Available)
            {
                continue;
            }

            if (result.Cancelled || !result.Rectangle.IsEmpty)
            {
                completedSelection = result;
                break;
            }
        }

        pickerCancellation.Cancel();
        try
        {
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException)
        {
            // Expected while removing sibling output overlays.
        }

        if (cancellationToken.IsCancellationRequested || completedSelection?.Cancelled == true)
        {
            DebugHelper.WriteLine("Native window-or-region selector phase=cancelled.");
            return (true, null);
        }
        if (completedSelection is null || completedSelection.Rectangle.IsEmpty)
        {
            return (false, null);
        }

        SixLabors.ImageSharp.Rectangle rectangle = completedSelection.Rectangle;
        string selectionKind = completedSelection.SelectionKind ?? "region";
        DebugHelper.WriteLine(
            $"Native window-or-region selector phase=selected kind={selectionKind} " +
            $"geometry={rectangle.X},{rectangle.Y} {rectangle.Width}x{rectangle.Height}.");

        Image? image = null;
        if (request.CaptureImage)
        {
            image = await Methods.CaptureRectangle(rectangle).WaitAsync(cancellationToken);
            if (image is null)
            {
                DebugHelper.WriteLine("grim returned no image for the native window-or-region selection.");
                return (true, null);
            }
        }

        WindowInfo? windowInfo = selectionKind == "window"
            ? windows.LastOrDefault(window => window.Rectangle == rectangle)
            : null;
        int left = monitors.Min(monitor => monitor.Rectangle.Left);
        int top = monitors.Min(monitor => monitor.Rectangle.Top);
        int right = monitors.Max(monitor => monitor.Rectangle.Right);
        int bottom = monitors.Max(monitor => monitor.Rectangle.Bottom);
        return (true, new RegionCaptureSelection
        {
            Rectangle = rectangle,
            CaptureBounds = new SixLabors.ImageSharp.Rectangle(left, top, right - left, bottom - top),
            Image = image,
            WindowInfo = windowInfo
        });
    }

    private static async Task<(bool Handled, RegionCaptureSelection? Selection)>
        TrySelectRegionWithNativeLayerPickerAsync(
            RegionCaptureRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsNativeWayland)
        {
            return (false, null);
        }

        string? helper = ResolveNativePickerPath();
        if (helper is null)
        {
            DebugHelper.WriteLine("Native region picker helper was not found.");
            return (false, null);
        }

        List<WindowInfo> monitors;
        try
        {
            monitors = await GetHyprlandMonitorsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugHelper.WriteLine($"Native region selector layout query failed: {ex.Message}");
            return (false, null);
        }

        WindowInfo? monitor = ResolveHyprlandMonitorForCursor(monitors);
        if (monitor is null)
        {
            DebugHelper.WriteLine("Native region selector found no Hyprland output for the cursor.");
            return (false, null);
        }

        DebugHelper.WriteLine(
            $"Native live region selector phase=launch output={monitor.ProcessName}.");
        NativePickerResult result = await RunNativePickerProcessAsync(
            helper,
            monitor,
            [],
            cancellationToken);

        if (!result.Available)
        {
            return (false, null);
        }

        if (cancellationToken.IsCancellationRequested || result.Cancelled)
        {
            DebugHelper.WriteLine("Native live region selector phase=cancelled.");
            return (true, null);
        }

        if (result.Rectangle.IsEmpty)
        {
            return (false, null);
        }

        SixLabors.ImageSharp.Rectangle rectangle = result.Rectangle;
        DebugHelper.WriteLine(
            $"Native live region selector phase=selected geometry={rectangle.X},{rectangle.Y} {rectangle.Width}x{rectangle.Height}.");

        Image? image = null;
        if (request.CaptureImage)
        {
            image = await Methods.CaptureRectangle(rectangle).WaitAsync(cancellationToken);
            if (image is null)
            {
                DebugHelper.WriteLine("grim returned no image for the native annotated region selection.");
                return (true, null);
            }
        }

        WindowInfo? windowInfo = null;
        if (request.Options.DetectWindows)
        {
            try
            {
                windowInfo = Methods.GetWindowList()
                    .Where(window => window.IsVisible && !window.Rectangle.IsEmpty)
                    .OrderByDescending(window => window.IsActive)
                    .FirstOrDefault(window => window.Rectangle.Contains(rectangle));
            }
            catch (Exception ex)
            {
                DebugHelper.WriteLine($"Window detection is unavailable for this region capture: {ex.Message}");
            }
        }

        return (true, new RegionCaptureSelection
        {
            Rectangle = rectangle,
            CaptureBounds = rectangle,
            Image = image,
            WindowInfo = windowInfo
        });
    }

    private static WindowInfo? ResolveHyprlandMonitorForCursor(IReadOnlyList<WindowInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        var cursor = Methods.GetCursorPosition();
        var center = new SixLabors.ImageSharp.Point(cursor.X, cursor.Y);
        return monitors.FirstOrDefault(monitor => monitor.Rectangle.Contains(center))
            ?? monitors.FirstOrDefault(monitor => monitor.IsActive)
            ?? monitors[0];
    }

    private static async Task<AnnotateOverlayLayout?> FindAnnotateOverlayLayoutAsync(
        SixLabors.ImageSharp.Rectangle rectangle,
        CancellationToken cancellationToken)
    {
        try
        {
            var center = new SixLabors.ImageSharp.Point(
                rectangle.X + rectangle.Width / 2,
                rectangle.Y + rectangle.Height / 2);
            List<WindowInfo> monitors = await GetHyprlandMonitorsAsync(cancellationToken);
            WindowInfo? monitor = monitors.FirstOrDefault(m => m.Rectangle.Contains(center))
                ?? monitors.FirstOrDefault();
            if (monitor is not null)
            {
                int reservedTop = CaptureAnnotationToolbar.DefaultTopMargin - 4;
                if (IsNativeWayland)
                {
                    var layout = await GetHyprlandMonitorLayoutAsync(
                        new SixLabors.ImageSharp.Point(center.X, center.Y),
                        cancellationToken);
                    if (layout is not null)
                    {
                        reservedTop = layout.Value.ReservedTop;
                    }
                }

                return new AnnotateOverlayLayout(
                    new PixelRect(
                        monitor.Rectangle.X,
                        monitor.Rectangle.Y,
                        monitor.Rectangle.Width,
                        monitor.Rectangle.Height),
                    reservedTop + 4);
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"Could not resolve annotate overlay bounds: {ex.Message}");
        }

        return null;
    }

    private static async Task<NativePickerResult> RunNativePickerProcessAsync(
        string helper,
        WindowInfo monitor,
        IReadOnlyList<WindowInfo> windows,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helper,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(monitor.ProcessName);
        startInfo.ArgumentList.Add("--origin");
        startInfo.ArgumentList.Add(monitor.Rectangle.X.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(monitor.Rectangle.Y.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--size");
        startInfo.ArgumentList.Add(monitor.Rectangle.Width.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(monitor.Rectangle.Height.ToString(CultureInfo.InvariantCulture));
        foreach (WindowInfo window in windows)
        {
            startInfo.ArgumentList.Add("--window");
            startInfo.ArgumentList.Add(
                $"{window.Rectangle.X},{window.Rectangle.Y} {window.Rectangle.Width}x{window.Rectangle.Height}");
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            DebugHelper.WriteLine($"Native picker could not start for output {monitor.ProcessName}: {ex.Message}");
            return new NativePickerResult(false, false, null, SixLabors.ImageSharp.Rectangle.Empty);
        }
        if (process is null)
        {
            return new NativePickerResult(false, false, null, SixLabors.ImageSharp.Rectangle.Empty);
        }

        using (process)
        using (cancellationToken.Register(() =>
        {
            try
            {
                process.StandardInput.Close();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                // The helper already exited.
            }
        }))
        {
            Task<string?> outputTask = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new NativePickerResult(true, true, null, SixLabors.ImageSharp.Rectangle.Empty);
            }

            string? output = await outputTask;
            string error = await errorTask;
            if (!string.IsNullOrWhiteSpace(error))
            {
                foreach (string line in error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    DebugHelper.WriteLine($"Native picker output={monitor.ProcessName} {line}");
                }
            }

            if (process.ExitCode != 0)
            {
                DebugHelper.WriteLine(
                    $"Native picker output={monitor.ProcessName} failed with exit code {process.ExitCode}.");
                return new NativePickerResult(false, false, null, SixLabors.ImageSharp.Rectangle.Empty);
            }
            if (string.IsNullOrWhiteSpace(output))
            {
                return new NativePickerResult(true, true, null, SixLabors.ImageSharp.Rectangle.Empty);
            }

            int separator = output.IndexOf(' ');
            if (separator <= 0 ||
                !TryParseSlurpGeometry(output[(separator + 1)..], out SixLabors.ImageSharp.Rectangle rectangle))
            {
                DebugHelper.WriteLine(
                    $"Native picker output={monitor.ProcessName} returned invalid selection: {output.Trim()}");
                return new NativePickerResult(false, false, null, SixLabors.ImageSharp.Rectangle.Empty);
            }

            return new NativePickerResult(true, false, output[..separator], rectangle);
        }
    }

    private static string? ResolveNativePickerPath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, NativePickerHelperName),
            Path.Combine(baseDirectory, "native", NativePickerHelperName),
            Path.Combine(baseDirectory, "lib", "snapx", NativePickerHelperName)
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate = Path.Combine(directory, NativePickerHelperName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static async Task<RegionCaptureSelection?> SelectRegionForCoreOnUIThreadAsync(
        RegionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var selector = new RegionSelectorWindow(true, request.CaptureImage)
        {
            _captureOptions = request.Options
        };
        // Do not return a completed selection until its window has completely
        // closed.  The selector gate is released in OnClosed, so returning on
        // the result task alone leaves a short interval in which an immediate
        // second hotkey is incorrectly treated as a duplicate selector.
        var closedTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        selector.Closed += (_, _) => closedTask.TrySetResult();
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(selector.RequestCancellation));
        if (!await selector.PrepareAndShowAsync(cancellationToken))
        {
            return null;
        }

        await Task.WhenAny(selector._resultRect.Task, closedTask.Task);
        if (!selector._resultRect.Task.IsCompletedSuccessfully || selector._resultRect.Task.Result == null)
        {
            return null;
        }

        Image? image = null;
        if (request.CaptureImage)
        {
            await Task.WhenAny(selector._resultImg.Task, closedTask.Task);
            if (selector._resultImg.Task.IsCompletedSuccessfully)
                image = selector._resultImg.Task.Result;
        }

        await closedTask.Task;
        if (request.CaptureImage && image is null)
            return null;

        WindowInfo? windowInfo = null;
        if (request.Options.DetectWindows)
        {
            try
            {
                SixLabors.ImageSharp.Rectangle selectedRectangle = selector._resultRect.Task.Result.Value;
                windowInfo = Methods.GetWindowList()
                    .Where(window => window.IsVisible && !window.Rectangle.IsEmpty)
                    .OrderByDescending(window => window.IsActive)
                    .FirstOrDefault(window => window.Rectangle.Contains(selectedRectangle));
            }
            catch (Exception ex)
            {
                DebugHelper.WriteLine($"Window detection is unavailable for this region capture: {ex.Message}");
            }
        }

        return new RegionCaptureSelection
        {
            Rectangle = selector._resultRect.Task.Result.Value,
            CaptureBounds = new SixLabors.ImageSharp.Rectangle(
                selector._screenBounds.X,
                selector._screenBounds.Y,
                selector._screenBounds.Width,
                selector._screenBounds.Height),
            Image = image,
            WindowInfo = windowInfo
        };
    }

    private static async Task<(bool Handled, RegionCaptureSelection? Selection)> TrySelectRegionWithSlurpAsync(
        RegionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !LinuxAPI.IsWayland())
        {
            return (false, null);
        }

        bool windowPickerMode = request.Options.WindowPickerMode;
        bool monitorPickerMode = request.Options.MonitorPickerMode;
        bool boxPickerMode = windowPickerMode || monitorPickerMode;
        List<WindowInfo> pickableBoxes = [];
        if (boxPickerMode)
        {
            try
            {
                // Methods.GetWindowList() reads window geometry through
                // XWayland/X11, whose coordinate space does not match
                // Hyprland's own logical layout that slurp draws in - the
                // boxes it would produce land on the wrong parts of the
                // screen. Ask Hyprland directly instead, the same source
                // slurp's own coordinates come from.
                pickableBoxes = monitorPickerMode
                    ? await GetHyprlandMonitorsAsync(cancellationToken)
                    : await GetHyprlandClientWindowsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteLine($"{(monitorPickerMode ? "Monitor" : "Window")} list is unavailable for the picker: {ex.Message}");
            }

            if (pickableBoxes.Count == 0)
            {
                // Nothing to pick from; let the caller fall back to its own path.
                return (false, null);
            }
        }

        var slurpArguments = new List<string>
        {
            "-b", "#00000000",
            "-s", boxPickerMode ? "#4c8dff33" : "#00000000",
            "-c", "#4c8dffff",
            "-w", "2",
            "-f", "%x,%y %wx%h"
        };
        if (boxPickerMode)
        {
            // Restrict the selection to the rectangles fed over stdin below:
            // slurp highlights whichever one is under the cursor and a click
            // selects it, instead of a free-form drag.
            slurpArguments.Add("-r");
            slurpArguments.Add("-B");
            slurpArguments.Add("#4c8dff22");
        }

        // slurp interprets every non-terminal stdin stream as a list of
        // predefined rectangles. A desktop app launched by a tool host can
        // inherit JSON-RPC/control input instead, which makes slurp reject it
        // as an invalid rectangle and immediately cancel region capture. For
        // free-form selection, run it through `script` so slurp receives a
        // clean pseudo-terminal rather than the host's control input.
        bool usePseudoTerminal = !boxPickerMode;
        var startInfo = new ProcessStartInfo
        {
            FileName = usePseudoTerminal ? "script" : "slurp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (usePseudoTerminal)
        {
            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildSlurpCommand(slurpArguments));
            startInfo.ArgumentList.Add("/dev/null");
        }
        else
        {
            foreach (string argument in slurpArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            DebugHelper.WriteLine("slurp is unavailable; falling back to the Avalonia region selector.");
            return (false, null);
        }

        if (process is null)
        {
            return (false, null);
        }

        using (process)
        using (cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The selector already exited.
            }
        }))
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (boxPickerMode)
            {
                foreach (WindowInfo box in pickableBoxes)
                {
                    string label = SanitizeSlurpLabel(
                        string.IsNullOrWhiteSpace(box.Title) ? box.ProcessName : box.Title);
                    await process.StandardInput.WriteLineAsync(
                        $"{box.Rectangle.X},{box.Rectangle.Y} {box.Rectangle.Width}x{box.Rectangle.Height} {label}");
                }
            }
            // Do not allow a host's stdin protocol to reach slurp. In
            // free-form mode `script` gives slurp a pseudo-terminal; in picker
            // mode this completes the explicit list of allowed rectangles.
            process.StandardInput.Close();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return (true, null);
            }

            string output, error;
            try
            {
                output = await outputTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                error = await errorTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (TimeoutException)
            {
                DebugHelper.WriteLine("slurp exited but output was not drained in time; treating the selection as cancelled.");
                return (true, null);
            }

            if (process.ExitCode != 0 || !TryParseSlurpGeometry(output, out var rectangle))
            {
                if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
                {
                    DebugHelper.WriteLine($"slurp exited with code {process.ExitCode}: {error.Trim()}");
                }
                return (true, null);
            }

            Image? image = null;
            if (request.CaptureImage)
            {
                image = await Methods.CaptureRectangle(rectangle).WaitAsync(cancellationToken);
                if (image is null)
                {
                    DebugHelper.WriteLine("grim returned no image for the slurp selection.");
                    return (true, null);
                }
            }

            WindowInfo? windowInfo = null;
            if (boxPickerMode)
            {
                // slurp -r only ever returns one of the boxes it was fed,
                // so an exact match is reliable here (unlike the heuristic
                // "smallest window containing an arbitrary point" used for
                // a free-form region below).
                windowInfo = pickableBoxes.FirstOrDefault(box => box.Rectangle == rectangle);
            }
            else if (request.Options.DetectWindows)
            {
                try
                {
                    windowInfo = Methods.GetWindowList()
                        .Where(window => window.IsVisible && !window.Rectangle.IsEmpty)
                        .OrderByDescending(window => window.IsActive)
                        .FirstOrDefault(window => window.Rectangle.Contains(rectangle));
                }
                catch (Exception ex)
                {
                    DebugHelper.WriteLine($"Window detection is unavailable for this region capture: {ex.Message}");
                }
            }

            return (true, new RegionCaptureSelection
            {
                Rectangle = rectangle,
                // slurp coordinates are compositor logical coordinates. The
                // selected rectangle itself is the valid coordinate extent.
                CaptureBounds = rectangle,
                Image = image,
                WindowInfo = windowInfo
            });
        }
    }

    private static async Task<List<WindowInfo>> GetHyprlandClientWindowsAsync(CancellationToken cancellationToken)
    {
        var windows = new List<WindowInfo>();

        var startInfo = new ProcessStartInfo
        {
            FileName = "hyprctl",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("clients");
        startInfo.ArgumentList.Add("-j");

        using var process = Process.Start(startInfo);
        if (process is null) return windows;

        string json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) return windows;

        using JsonDocument document = JsonDocument.Parse(json);
        int selfPid = Environment.ProcessId;
        foreach (JsonElement client in document.RootElement.EnumerateArray())
        {
            if (!client.TryGetProperty("mapped", out JsonElement mapped) || !mapped.GetBoolean()) continue;
            if (client.TryGetProperty("hidden", out JsonElement hidden) && hidden.GetBoolean()) continue;
            if (!client.TryGetProperty("at", out JsonElement at) || !client.TryGetProperty("size", out JsonElement size)) continue;
            if (at.GetArrayLength() < 2 || size.GetArrayLength() < 2) continue;

            int width = size[0].GetInt32();
            int height = size[1].GetInt32();
            if (width <= 0 || height <= 0) continue;

            int pid = client.TryGetProperty("pid", out JsonElement pidElement) ? pidElement.GetInt32() : 0;
            if (pid == selfPid) continue;

            string title = client.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() ?? "" : "";
            string cls = client.TryGetProperty("class", out JsonElement classElement) ? classElement.GetString() ?? "" : "";

            windows.Add(new WindowInfo
            {
                Rectangle = new SixLabors.ImageSharp.Rectangle(at[0].GetInt32(), at[1].GetInt32(), width, height),
                Title = string.IsNullOrWhiteSpace(title) ? cls : title,
                ProcessName = cls,
                ProcessId = pid,
                IsVisible = true
            });
        }

        return windows;
    }

    private static async Task<List<WindowInfo>> GetHyprlandMonitorsAsync(CancellationToken cancellationToken)
    {
        static bool IsQuarterTurn(int transform) => transform is 1 or 3 or 5 or 7;

        var monitors = new List<WindowInfo>();

        var startInfo = new ProcessStartInfo
        {
            FileName = "hyprctl",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("monitors");
        startInfo.ArgumentList.Add("-j");

        using var process = Process.Start(startInfo);
        if (process is null) return monitors;

        string json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) return monitors;

        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement monitor in document.RootElement.EnumerateArray())
        {
            if (!monitor.TryGetProperty("x", out JsonElement xElement) ||
                !monitor.TryGetProperty("y", out JsonElement yElement) ||
                !monitor.TryGetProperty("width", out JsonElement widthElement) ||
                !monitor.TryGetProperty("height", out JsonElement heightElement) ||
                !monitor.TryGetProperty("scale", out JsonElement scaleElement))
            {
                continue;
            }

            double scale = scaleElement.GetDouble();
            if (scale <= 0) scale = 1;
            int transform = monitor.TryGetProperty("transform", out JsonElement transformElement)
                ? transformElement.GetInt32()
                : 0;
            bool rotated = IsQuarterTurn(transform);
            double physicalWidth = widthElement.GetDouble();
            double physicalHeight = heightElement.GetDouble();
            int logicalWidth = (int)Math.Round((rotated ? physicalHeight : physicalWidth) / scale);
            int logicalHeight = (int)Math.Round((rotated ? physicalWidth : physicalHeight) / scale);
            if (logicalWidth <= 0 || logicalHeight <= 0) continue;

            string name = monitor.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";

            monitors.Add(new WindowInfo
            {
                Rectangle = new SixLabors.ImageSharp.Rectangle(xElement.GetInt32(), yElement.GetInt32(), logicalWidth, logicalHeight),
                Title = name,
                ProcessName = name,
                IsVisible = true
            });
        }

        return monitors;
    }

    private static string SanitizeSlurpLabel(string label)
    {
        // slurp's predefined-box format is one box per line; a title
        // containing a newline would otherwise be read as extra boxes.
        string sanitized = label.Replace("\r", " ").Replace("\n", " ").Trim();
        return string.IsNullOrEmpty(sanitized) ? "window" : sanitized;
    }

    private static string BuildSlurpCommand(IEnumerable<string> arguments)
    {
        return "exec slurp " + string.Join(' ', arguments.Select(QuoteForPosixShell));
    }

    private static string QuoteForPosixShell(string argument)
    {
        return "'" + argument.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static bool TryParseSlurpGeometry(string output, out SixLabors.ImageSharp.Rectangle rectangle)
    {
        rectangle = SixLabors.ImageSharp.Rectangle.Empty;
        string[] values = output.Trim().Split([' ', ',', 'x'], StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 4 ||
            !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            !int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) ||
            width <= 0 || height <= 0)
        {
            return false;
        }

        rectangle = new SixLabors.ImageSharp.Rectangle(x, y, width, height);
        return true;
    }

    public RegionSelectorWindow(RegionSelectorViewModel vm, bool IsSilent = false, bool takeScreenshot = true)
    {
        DataContext = vm;
        IsSilentMode = IsSilent;
        TakeScreenshot = takeScreenshot;
        InitializeComponent();

        _selectionRect = this.FindControl<Rectangle>("SelectionRect");
        _infoBox = this.FindControl<TextBox>("InfoBox");
        _canvas = this.FindControl<Canvas>("Canvas");
        _cursorMarker = this.FindControl<Panel>("CursorMarker");
        _liveToolbarHost = this.FindControl<Border>("LiveToolbarHost");
        _liveAnnotateHost = this.FindControl<Panel>("LiveAnnotateHost");

        // Set initial state to invisible/minimized to prevent flicker
        // until the async position logic finishes.
        Opacity = 0;
    }
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_ownsSelector)
        {
            Close();
            return;
        }

        // A selector must never begin its capture after it is mapped: on
        // Wayland that would capture the selector itself. All supported entry
        // points prepare it before Show(); close an unsupported direct Show()
        // rather than presenting a black or stale overlay.
        if (!_preparedForDisplay)
        {
            await CancelSelection();
            return;
        }

        if (!_layoutApplied)
        {
            try
            {
                await SetupSelectorLayoutAsync();
                _layoutApplied = true;
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Region selector could not determine its display bounds");
                await CancelSelection();
                return;
            }
        }

        IsVisible = true;
        Activate();
        Focus();

        if (NeedsLiveAnnotateSession(_captureOptions))
        {
            InitializeLiveAnnotateSession();
            UpdateLiveToolbarPlacement();
        }

        if (IsNativeWayland && _layoutApplied)
        {
            _ = EnsureHyprlandSelectorOverlayAsync(_screenBounds).ContinueWith(
                _ => Dispatcher.UIThread.Post(UpdateLiveToolbarPlacement),
                TaskScheduler.Default);
        }
    }
    public static async Task<Image?> SelectRegionAsync()
    {
        if (IsNativeWayland)
        {
            return (await SelectRegionForCoreAsync(new RegionCaptureRequest
            {
                CaptureImage = true,
                Options = new RegionCaptureOptions { AnnotateCapture = false }
            }, CancellationToken.None))?.Image;
        }

        var selector = new RegionSelectorWindow(true);
        var windowClosedTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        selector.Closed += (_, _) => windowClosedTask.TrySetResult();
        if (!await selector.PrepareAndShowAsync())
        {
            return null;
        }

        await Task.WhenAny(selector._resultImg.Task, windowClosedTask.Task);

        if (selector._resultImg.Task.IsCompleted)
        {
            await windowClosedTask.Task;
            return selector._resultImg.Task.IsCompletedSuccessfully
                ? await selector._resultImg.Task
                : null;
        }

        return null;
    }
    public static async Task<SixLabors.ImageSharp.Rectangle?> SelectRegionRectAsync()
    {
        if (IsNativeWayland)
        {
            return (await SelectRegionForCoreAsync(new RegionCaptureRequest
            {
                CaptureImage = false,
                Options = new RegionCaptureOptions { AnnotateCapture = false }
            }, CancellationToken.None))?.Rectangle;
        }

        var selector = new RegionSelectorWindow(true, false);
        var windowClosedTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        selector.Closed += (_, _) => windowClosedTask.TrySetResult();
        if (!await selector.PrepareAndShowAsync())
        {
            return null;
        }

        await Task.WhenAny(selector._resultRect.Task, windowClosedTask.Task);

        if (selector._resultRect.Task.IsCompleted)
        {
            await windowClosedTask.Task;
            return selector._resultRect.Task.IsCompletedSuccessfully
                ? await selector._resultRect.Task
                : null;
        }

        return null;
    }

    private async Task<PixelRect> ResolveSelectorBoundsAsync()
    {
        var cursorPos = Methods.GetCursorPosition();

        if (IsNativeWayland)
        {
            (PixelRect Bounds, int ReservedTop)? layout =
                await GetHyprlandMonitorLayoutAsync(cursorPos, CancellationToken.None);
            if (layout is not null)
            {
                _toolbarTopMargin = layout.Value.ReservedTop + 4;
                DebugHelper.WriteLine(
                    $"Selector toolbar offset: reserved top {layout.Value.ReservedTop}px, margin {_toolbarTopMargin}px");
                return layout.Value.Bounds;
            }
        }

        return ResolveBoundsFromScreens(cursorPos);
    }

    internal static async Task<int> ResolveToolbarTopMarginAsync(CancellationToken cancellationToken = default)
    {
        if (!IsNativeWayland)
        {
            return CaptureAnnotationToolbar.DefaultTopMargin;
        }

        var cursorPos = Methods.GetCursorPosition();
        (PixelRect Bounds, int ReservedTop)? layout =
            await GetHyprlandMonitorLayoutAsync(cursorPos, cancellationToken);
        if (layout is null)
        {
            return CaptureAnnotationToolbar.DefaultTopMargin;
        }

        int margin = layout.Value.ReservedTop + 4;
        DebugHelper.WriteLine(
            $"Annotate toolbar offset: reserved top {layout.Value.ReservedTop}px, margin {margin}px");
        return margin;
    }

    private static async Task<(PixelRect Bounds, int ReservedTop)?> GetHyprlandMonitorLayoutAsync(
        SixLabors.ImageSharp.Point cursorPos,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "hyprctl",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("monitors");
        startInfo.ArgumentList.Add("-j");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        string json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            return null;
        }

        static bool IsQuarterTurn(int transform) => transform is 1 or 3 or 5 or 7;

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement? selectedMonitor = null;
        JsonElement? focusedMonitor = null;
        foreach (JsonElement monitor in document.RootElement.EnumerateArray())
        {
            if (!monitor.TryGetProperty("x", out JsonElement xElement) ||
                !monitor.TryGetProperty("y", out JsonElement yElement) ||
                !monitor.TryGetProperty("width", out JsonElement widthElement) ||
                !monitor.TryGetProperty("height", out JsonElement heightElement) ||
                !monitor.TryGetProperty("scale", out JsonElement scaleElement))
            {
                continue;
            }

            double scale = scaleElement.GetDouble();
            if (scale <= 0)
            {
                scale = 1;
            }

            int transform = monitor.TryGetProperty("transform", out JsonElement transformElement)
                ? transformElement.GetInt32()
                : 0;
            bool rotated = IsQuarterTurn(transform);
            double physicalWidth = widthElement.GetDouble();
            double physicalHeight = heightElement.GetDouble();
            int logicalWidth = (int)Math.Round((rotated ? physicalHeight : physicalWidth) / scale);
            int logicalHeight = (int)Math.Round((rotated ? physicalWidth : physicalHeight) / scale);
            if (logicalWidth <= 0 || logicalHeight <= 0)
            {
                continue;
            }

            int x = xElement.GetInt32();
            int y = yElement.GetInt32();
            if (cursorPos.X >= x && cursorPos.X < x + logicalWidth &&
                cursorPos.Y >= y && cursorPos.Y < y + logicalHeight)
            {
                selectedMonitor = monitor;
                break;
            }

            if (monitor.TryGetProperty("focused", out JsonElement focusedElement) &&
                focusedElement.GetBoolean())
            {
                focusedMonitor = monitor;
            }
        }

        JsonElement target = selectedMonitor
            ?? focusedMonitor
            ?? (document.RootElement.GetArrayLength() > 0
                ? document.RootElement[0]
                : default);
        if (target.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        double targetScale = target.GetProperty("scale").GetDouble();
        if (targetScale <= 0)
        {
            targetScale = 1;
        }

        int targetTransform = target.TryGetProperty("transform", out JsonElement targetTransformElement)
            ? targetTransformElement.GetInt32()
            : 0;
        bool targetRotated = IsQuarterTurn(targetTransform);
        double targetPhysicalWidth = target.GetProperty("width").GetDouble();
        double targetPhysicalHeight = target.GetProperty("height").GetDouble();
        int width = (int)Math.Round((targetRotated ? targetPhysicalHeight : targetPhysicalWidth) / targetScale);
        int height = (int)Math.Round((targetRotated ? targetPhysicalWidth : targetPhysicalHeight) / targetScale);
        int monitorX = target.GetProperty("x").GetInt32();
        int monitorY = target.GetProperty("y").GetInt32();
        int reservedTop = 0;
        if (target.TryGetProperty("reserved", out JsonElement reservedElement) &&
            reservedElement.ValueKind == JsonValueKind.Array &&
            reservedElement.GetArrayLength() > 0)
        {
            // Hyprland reports reserved as [top, bottom, left, right] in physical px.
            reservedTop = (int)Math.Round(reservedElement[0].GetDouble() / targetScale);
        }

        if (reservedTop <= 0)
        {
            string monitorName = target.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            reservedTop = await GetHyprlandTopBarHeightFromLayersAsync(
                monitorName,
                monitorX,
                monitorY,
                width,
                cancellationToken);
        }

        if (reservedTop <= 0)
        {
            reservedTop = DefaultTopBarLogicalHeight;
        }

        return (new PixelRect(monitorX, monitorY, width, height), reservedTop);
    }

    private static async Task<int> GetHyprlandTopBarHeightFromLayersAsync(
        string monitorName,
        int monitorX,
        int monitorY,
        int logicalWidth,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "hyprctl",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("layers");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 0;
        }

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }

        bool inTargetMonitor = string.IsNullOrWhiteSpace(monitorName);
        int maxBarHeight = 0;
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("Monitor ", StringComparison.Ordinal))
            {
                inTargetMonitor = string.IsNullOrWhiteSpace(monitorName) ||
                                  line.Contains(monitorName, StringComparison.Ordinal);
                continue;
            }

            if (!inTargetMonitor || !line.Contains("xywh:", StringComparison.Ordinal))
            {
                continue;
            }

            int xywhIndex = line.IndexOf("xywh:", StringComparison.Ordinal);
            string xywhPart = line[(xywhIndex + 5)..];
            int commaIndex = xywhPart.IndexOf(',');
            if (commaIndex >= 0)
            {
                xywhPart = xywhPart[..commaIndex];
            }

            string[] parts = xywhPart.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 4 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int layerX) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int layerY) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int layerWidth) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int layerHeight))
            {
                continue;
            }

            if (layerX != monitorX ||
                layerY != monitorY ||
                layerWidth < logicalWidth - 2 ||
                layerHeight <= 0 ||
                layerHeight > 120)
            {
                continue;
            }

            maxBarHeight = Math.Max(maxBarHeight, layerHeight);
        }

        return maxBarHeight;
    }

    private void ApplySelectorLayout(PixelRect bounds)
    {
        Position = new PixelPoint(bounds.X, bounds.Y);
        _screenBounds = bounds;
        Width = bounds.Width;
        Height = bounds.Height;

        double canvasWidth = bounds.Width;
        double canvasHeight = bounds.Height;
        _canvas.Width = canvasWidth;
        _canvas.Height = canvasHeight;
        _imageBounds = new Rect(0, 0, canvasWidth, canvasHeight);

        if (_image is not null && canvasWidth > 0 && canvasHeight > 0)
        {
            _captureScaleX = _image.Width / canvasWidth;
            _captureScaleY = _image.Height / canvasHeight;
        }
        else
        {
            _captureScaleX = 1;
            _captureScaleY = 1;
        }

        if (_liveAnnotateOverlay is not null)
        {
            _liveAnnotateOverlay.Width = canvasWidth;
            _liveAnnotateOverlay.Height = canvasHeight;
        }

        if (_backgroundImage is not null)
        {
            _backgroundImage.Width = canvasWidth;
            _backgroundImage.Height = canvasHeight;
        }

        if (_liveToolbarHost is not null)
        {
            _liveToolbarHost.Margin = new Thickness(0, _toolbarTopMargin, 0, 0);
        }

        WindowState = OperatingSystem.IsMacOS() ? WindowState.Maximized : WindowState.Normal;

        DebugHelper.WriteLine(
            $"Selector Ready: window {bounds.Width}x{bounds.Height} at {bounds.X},{bounds.Y}; capture scale {_captureScaleX:0.###}x{_captureScaleY:0.###}");
    }

    private async Task SetupSelectorLayoutAsync()
    {
        PixelRect bounds = await ResolveSelectorBoundsAsync();
        await Dispatcher.UIThread.InvokeAsync(() => ApplySelectorLayout(bounds));
    }

    private PixelRect ResolveBoundsFromScreens(SixLabors.ImageSharp.Point cursorPos)
    {
        var screen = Screens.ScreenFromPoint(new PixelPoint(cursorPos.X, cursorPos.Y));
        if (screen != null)
        {
            return screen.Bounds;
        }

        try
        {
            var snapXScreen = Methods.GetScreen(cursorPos);
            if (snapXScreen is not null)
            {
                var (x, y, width, height) = snapXScreen.Bounds;
                return new PixelRect(x, y, width, height);
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"Region selector could not resolve screen bounds: {ex.Message}");
        }

        return new PixelRect(0, 0, 1920, 1080);
    }
    public RegionSelectorWindow() : this(new RegionSelectorViewModel()) { }
    public RegionSelectorWindow(bool IsSilent, bool takeScreenShot = true) : this(new RegionSelectorViewModel(), IsSilent, takeScreenShot) { }
    private void OnPointerPressed(object? Sender, PointerPressedEventArgs E)
    {
        if (_selectionCompleted || !_captureReady)
        {
            if (!_captureReady)
            {
                DebugHelper.WriteLine("The region selector is still preparing its screenshot; ignoring pointer input.");
            }
            return;
        }

        if (IsPointerOverLiveToolbar(E.Source))
        {
            return;
        }

        if (_liveAnnotateSession && !_regionToolActive)
        {
            return;
        }

        if (_captureOptions.WindowPickerMode)
        {
            // Nothing under the cursor is pickable at this point; ignore the
            // click rather than falling back to an unrelated drag-selection.
            if (_hoveredWindow is { } window)
            {
                double x = window.Rectangle.X - _screenBounds.X;
                double y = window.Rectangle.Y - _screenBounds.Y;
                _selectionRect.Width = window.Rectangle.Width;
                _selectionRect.Height = window.Rectangle.Height;
                SetSelectionRect(x, y, window.Rectangle.Width, window.Rectangle.Height);
                _isSelecting = true;
                E.Pointer.Capture(_canvas);
                OnPointerReleased(this, null);
            }
            return;
        }

        _startPoint = E.GetPosition(_canvas);
        _isSelecting = true;
        RecordPressPoint(_startPoint);
        E.Pointer.Capture(_canvas);
        MoveCursorMarker(_startPoint);

        _selectionRect.Width = 0;
        _selectionRect.Height = 0;
        SetSelectionRect(_startPoint.X, _startPoint.Y, 0, 0);
        _selectionRect.IsVisible = true;

        _infoBox.IsVisible = _captureOptions.ShowInfo;
    }
    private static void ShowErrorDialog(Exception ex, string? userMessage = null)
    {
        DebugHelper.WriteException(ex);
        TaskHelpers.PlayNotificationSoundAsync(NotificationSound.Error);

        // FAContentDialog is hosted in Avalonia's overlay/popup machinery.
        // Do not create that transient EGL surface on native Wayland while a
        // failed selector is restoring the main window. The desktop
        // notification is compositor-native and keeps the failure visible.
        if (OperatingSystem.IsLinux() && LinuxAPI.IsWayland())
        {
            App.SendDesktopNotification(Lang.Error, userMessage ?? Lang.FailedToScreenshot);
            return;
        }

        var dialog = new FAContentDialog
        {
            Title = Lang.Error,
            Width = 800,
            Height = 450,
            CloseButtonText = Lang.Ok
        };

        var autoCloseCts = new CancellationTokenSource();

        var messageText = new SelectableTextBlock
        {
            Text = userMessage ?? Lang.FailedToScreenshot,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var errorDetails = new ScrollViewer
        {
            Margin = new Thickness(2, 0, 0, 10),
            Content = new SelectableTextBlock
            {
                Text = ex.ToString(),
                TextWrapping = TextWrapping.Wrap
            }
        };
        dialog.CloseButtonClick += (_, _) =>
        {
            autoCloseCts.Cancel();
        };

        void CancelAutoCloseOnInteraction(object? s, EventArgs e) => autoCloseCts.Cancel();

        messageText.PointerPressed += CancelAutoCloseOnInteraction;
        messageText.PointerReleased += CancelAutoCloseOnInteraction;
        errorDetails.PointerPressed += CancelAutoCloseOnInteraction;
        errorDetails.PointerReleased += CancelAutoCloseOnInteraction;

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(10),
            Children =
            {
                messageText,
                errorDetails,
            }
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), autoCloseCts.Token);
                await Dispatcher.UIThread.InvokeAsync(dialog.Hide);
            }
            catch (TaskCanceledException)
            {
                // Ignored: user interacted with the dialog
            }
        }, autoCloseCts.Token);

        if (App.MyMainWindow is { IsVisible: true, IsLoaded: true } mainWindow)
        {
            dialog.ShowAsync(mainWindow);
        }
        else
        {
            dialog.ShowAsync();
        }
    }
    private async void OnPointerReleased(object? Sender, PointerReleasedEventArgs? E)
    {
        if (_selectionCompleted || !_isSelecting)
        {
            return;
        }

        _isSelecting = false;
        E?.Pointer.Capture(null);

        if (_captureOptions.WindowOrRegionPickerMode)
        {
            // The native Wayland picker treats a short pointer movement on
            // open desktop space as a window click, not a region drag. The
            // release point can differ from the press point after a small
            // drag, so the distance between them decides the outcome.
            // A real pointer release is required: synthetic commits from
            // SelectHoveredWindow and the Enter key arrive with no event.
            if (E is not null &&
                !IsDragBeyondThreshold(_pressedPoint, E.GetPosition(_canvas)))
            {
                UpdateWindowHover(E.GetPosition(_canvas));
                if (_hoveredWindow is { } clickedWindow)
                {
                    SelectHoveredWindow(clickedWindow);
                    return;
                }
            }
        }

        // Do not read _selectionRect.Bounds here: programmatic Width, Height,
        // and Margin changes do not refresh layout bounds until the next
        // layout pass, so freshly drawn selections would look empty. Derive
        // the current rectangle from the explicit shape properties.
        var drawnRect = new Rect(
            Canvas.GetLeft(_selectionRect),
            Canvas.GetTop(_selectionRect),
            _selectionRect.Width,
            _selectionRect.Height);
        _selectionRect.IsVisible = false;
        _infoBox.IsVisible = false;
        if (drawnRect.Width <= 0 || drawnRect.Height <= 0)
        {
            await CancelSelection();
            return;
        }

        var selectedRegion = _imageBounds.Intersect(drawnRect);

        if (selectedRegion.Width <= 0 || selectedRegion.Height <= 0 ||
            selectedRegion.Width > _imageBounds.Width || selectedRegion.Height > _imageBounds.Height)
        {
            await CancelSelection();
            return;
        }

        if (selectedRegion.Width < Math.Max(1, _captureOptions.MinimumSize) ||
            selectedRegion.Height < Math.Max(1, _captureOptions.MinimumSize))
        {
            await CancelSelection();
            return;
        }

        var localRect = new SixLabors.ImageSharp.Rectangle(
            (int)Math.Floor(selectedRegion.X),
            (int)Math.Floor(selectedRegion.Y),
            (int)Math.Ceiling(selectedRegion.Width),
            (int)Math.Ceiling(selectedRegion.Height));
        var screenRect = new SixLabors.ImageSharp.Rectangle(
            _screenBounds.X + localRect.X,
            _screenBounds.Y + localRect.Y,
            localRect.Width,
            localRect.Height);
        _selectionCompleted = true;
        _resultRect.TrySetResult(screenRect);
        DebugHelper.WriteLine($"RegionSelectorWindow.OnPointerReleased: Region: {selectedRegion}");
        if (!TakeScreenshot)
        {
            Close();
            return;
        }

        Image? cropped = null;
        try
        {
            await Task.Run(() =>
            {
                if (_image is null)
                {
                    throw new InvalidOperationException("The selector screenshot is unavailable.");
                }

                double scaleX = _captureScaleX;
                double scaleY = _captureScaleY;

                if (_liveAnnotateSession)
                {
                    foreach (ImageAnnotation annotation in _annotations.Where(x => x is not CropAnnotation))
                    {
                        ScaleAnnotation(annotation, scaleX, scaleY).Apply(_image);
                    }
                }

                var imageRect = new SixLabors.ImageSharp.Rectangle(
                    (int)Math.Floor(localRect.X * scaleX),
                    (int)Math.Floor(localRect.Y * scaleY),
                    Math.Min(_image.Width, (int)Math.Ceiling(localRect.Width * scaleX)),
                    Math.Min(_image.Height, (int)Math.Ceiling(localRect.Height * scaleY)));
                imageRect = SixLabors.ImageSharp.Rectangle.Intersect(imageRect, _image.Bounds);
                if (imageRect.IsEmpty)
                {
                    throw new InvalidOperationException("The selected region does not intersect the captured image.");
                }

                _image.Mutate(Context => Context.Crop(imageRect));
                cropped = _image;
            });
        }
        catch (Exception ex)
        {
            _resultImg.TrySetException(ex);
            ShowErrorDialog(ex);
            RestoreMainWindowIfNeeded();
            Close();
            return;
        }

        if (cropped is null)
        {
            _resultImg.TrySetResult(null);
            RestoreMainWindowIfNeeded();
            Close();
            return;
        }

        if (_liveAnnotateSession)
        {
            FinishCapture(_image!);
            return;
        }

        // Present the inline (non-modal) annotation toolbar over the cropped
        // capture before committing it: Save composites the marks onto the
        // image, Cancel commits the plain capture. This is the ShareX-style
        // "edit on the capture itself", NOT the modal CapturedImageEditorWindow.
        // Skip it whenever the selection is a pre-defined box (window/monitor
        // picker) or the caller disabled annotation (scrolling capture), where
        // the toolbar would force a wasted step. IsSilentMode must NOT gate
        // this: the core region selector runs silent, so returning early would
        // skip the window close and hang the caller's closedTask.
        if (_captureOptions.AnnotateCapture &&
            !_captureOptions.WindowPickerMode &&
            !_captureOptions.WindowOrRegionPickerMode &&
            !_captureOptions.MonitorPickerMode)
        {
            ShowAnnotationToolbar(cropped);
            return;
        }

        _resultImg.TrySetResult(_image);
        if (!IsSilentMode)
        {
            DebugHelper.WriteLine("Running image task");
            UploadManager.RunImageTask(_image, TaskSettings.GetDefaultTaskSettings());
        }
        RestoreMainWindowIfNeeded();
        Close();
    }
    /// <summary>
    /// Builds and shows the inline annotation surface + toolbar over the
    /// cropped capture. Non-modal: the capture is not committed until the user
    /// presses Save (compositing the marks) or Cancel (keeping the plain
    /// image). This is the ShareX-style "edit on the capture itself" and is
    /// deliberately NOT the modal <see cref="CapturedImageEditorWindow"/>.
    /// </summary>
    private void ShowAnnotationToolbar(Image cropped)
    {
        _annotationMode = true;
        var bitmap = App.SnapX.ConvertImageSharpImgToAvalonia(cropped);
        _annotationBitmap = bitmap;
        _annotationSurface = new AnnotationSurface(bitmap, AddAnnotation, () => _annotationText);
        _annotationSurface.Width = bitmap.PixelSize.Width;
        _annotationSurface.Height = bitmap.PixelSize.Height;

        var scroll = new ScrollViewer
        {
            Content = _annotationSurface,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var toolbar = BuildAnnotationToolbar();
        _annotationToolbar = toolbar;

        var layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            }
        };
        layout.Children.Add(toolbar);
        Grid.SetRow(toolbar, 0);
        layout.Children.Add(scroll);
        Grid.SetRow(scroll, 1);

        Content = new Border
        {
            Background = new SolidColorBrush(AvaloniaColor.FromRgb(30, 30, 30)),
            Child = layout
        };
    }

    private Control BuildAnnotationToolbar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        foreach ((string label, ImageAnnotation.Tool tool) in new[]
                 {
                     ("Rect", ImageAnnotation.Tool.Rectangle),
                     ("Redact", ImageAnnotation.Tool.Redaction),
                     ("Freehand", ImageAnnotation.Tool.Freehand),
                     ("Arrow", ImageAnnotation.Tool.Arrow),
                     ("Text", ImageAnnotation.Tool.Text),
                     ("Crop", ImageAnnotation.Tool.Crop)
                 })
        {
            var button = new Button { Content = label, Margin = new Thickness(2) };
            button.Click += (_, _) => SetAnnotationTool(tool);
            panel.Children.Add(button);
        }

        var undo = new Button { Content = "Undo", Margin = new Thickness(2) };
        undo.Click += (_, _) => UndoAnnotation();
        panel.Children.Add(undo);

        var textBox = new TextBox
        {
            Watermark = "Text",
            Width = 160,
            Margin = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center
        };
        textBox.TextChanged += (_, _) => _annotationText = textBox.Text ?? "";
        panel.Children.Add(textBox);

        var cancel = new Button { Content = "Cancel", Margin = new Thickness(2), MinWidth = 80 };
        cancel.Click += (_, _) => CancelAnnotation();
        panel.Children.Add(cancel);

        var save = new Button { Content = "Save", Margin = new Thickness(2), MinWidth = 80 };
        save.Click += (_, _) => SaveAnnotation();
        panel.Children.Add(save);

        return panel;
    }

    private void SetAnnotationTool(ImageAnnotation.Tool tool)
    {
        _annotationTool = tool;
        _annotationSurface?.SetTool(tool);
    }

    private void AddAnnotation(ImageAnnotation annotation, ImageAnnotation.Tool tool)
    {
        if (annotation is not { } a)
        {
            return;
        }
        _annotations.Add(a);
        if (tool != ImageAnnotation.Tool.Crop)
        {
            _undoStack.Push(a);
        }
        _annotationSurface?.InvalidateVisual();
        _liveAnnotateOverlay?.InvalidateVisual();
    }

    private void UndoAnnotation()
    {
        if (_undoStack.TryPop(out ImageAnnotation? last))
        {
            _annotations.Remove(last);
            _annotationSurface?.InvalidateVisual();
            _liveAnnotateOverlay?.InvalidateVisual();
        }
    }

    private void SaveAnnotation()
    {
        // Composite the annotations onto the captured image in place so the
        // caller owns a single result image. A crop changes the image geometry,
        // so it must be applied LAST; otherwise the pixel coordinates of marks
        // drawn before the crop (expressed in the original canvas space) would
        // land at wrong offsets after the crop shrank the image.
        foreach (ImageAnnotation a in _annotations.Where(x => x is not CropAnnotation))
        {
            Image? applied = a.Apply(_image!);
            if (applied == null)
            {
                break;
            }
            if (!ReferenceEquals(applied, _image))
            {
                _image.Dispose();
            }
            _image = applied;
        }
        foreach (ImageAnnotation a in _annotations.Where(x => x is CropAnnotation))
        {
            Image? applied = a.Apply(_image!);
            if (applied == null)
            {
                break;
            }
            if (!ReferenceEquals(applied, _image))
            {
                _image.Dispose();
            }
            _image = applied;
        }
        FinishCapture(_image!);
    }

    private bool IsPointerOverLiveToolbar(object? source)
    {
        if (_liveToolbarHost is null || source is not Visual visual)
        {
            return false;
        }

        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, _liveToolbarHost))
            {
                return true;
            }
        }

        return false;
    }

    private void InitializeLiveAnnotateSession()
    {
        if (_liveToolbarHost is null)
        {
            DebugHelper.WriteLine("Live annotate toolbar host is unavailable.");
            return;
        }

        if (_liveAnnotateSession)
        {
            return;
        }

        _liveAnnotateSession = true;
        _regionToolActive = true;

        _liveAnnotateOverlay = new LiveAnnotationOverlay(
            () => _annotations,
            AddAnnotation,
            () => _annotationText,
            () => _highlightMode)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        if (_liveAnnotateHost is not null)
        {
            _liveAnnotateHost.Children.Clear();
            _liveAnnotateHost.Children.Add(_liveAnnotateOverlay);
            _liveAnnotateHost.IsHitTestVisible = false;
        }

        _liveToolbarHost.Margin = new Thickness(0, _toolbarTopMargin, 0, 0);
        _liveToolbarHost.Child = BuildLiveAnnotationToolbar();
        _liveToolbarHost.IsVisible = true;
        _canvas.IsHitTestVisible = true;

        DebugHelper.WriteLine("Live annotate toolbar initialized.");
    }

    private Control BuildLiveAnnotationToolbar()
    {
        var root = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        _liveToolButtons.Clear();
        _regionToolButton = CreateIconToolButton(Symbol.ScreenCut, "Region", SetRegionToolActive);
        tools.Children.Add(_regionToolButton);
        var rectangleButton = CreateIconToolButton(Symbol.Square, "Rectangle", () =>
        {
            _highlightMode = false;
            SetLiveAnnotationTool(ImageAnnotation.Tool.Rectangle);
        });
        tools.Children.Add(rectangleButton);
        _liveToolButtons[ImageAnnotation.Tool.Rectangle] = rectangleButton;
        tools.Children.Add(CreateIconToolButton(Symbol.Highlight, "Highlight", SetLiveHighlightTool));
        AddIconToolButton(tools, Symbol.Blur, "Blur", ImageAnnotation.Tool.Redaction);
        AddIconToolButton(tools, Symbol.Pen, "Freehand", ImageAnnotation.Tool.Freehand);
        AddIconToolButton(tools, Symbol.ArrowRight, "Arrow", ImageAnnotation.Tool.Arrow);
        AddIconToolButton(tools, Symbol.TextT, "Text", ImageAnnotation.Tool.Text);
        tools.Children.Add(CreateIconToolButton(Symbol.ArrowUndo, "Undo", UndoAnnotation));
        root.Children.Add(tools);

        _annotationTextBox = new TextBox
        {
            Width = 120,
            MinHeight = 24,
            MaxHeight = 24,
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Watermark = "Text"
        };
        _annotationTextBox.TextChanged += (_, _) => _annotationText = _annotationTextBox.Text ?? string.Empty;
        root.Children.Add(_annotationTextBox);

        return root;
    }

    private void AddIconToolButton(
        StackPanel panel,
        Symbol symbol,
        string tip,
        ImageAnnotation.Tool tool)
    {
        Button button = CreateIconToolButton(symbol, tip, () => SetLiveAnnotationTool(tool));
        panel.Children.Add(button);
        _liveToolButtons[tool] = button;
    }

    private Button CreateIconToolButton(Symbol symbol, string tip, Action onClick)
    {
        var button = new Button
        {
            Width = 24,
            Height = 24,
            MinWidth = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(2),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            Content = new SymbolIcon
            {
                Symbol = symbol,
                FontSize = 12,
                IconVariant = IconVariant.Regular
            }
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button? _regionToolButton;

    private void SetLiveHighlightTool()
    {
        _highlightMode = true;
        SetLiveAnnotationTool(ImageAnnotation.Tool.Rectangle);
    }

    private void SetRegionToolActive()
    {
        _regionToolActive = true;
        _highlightMode = false;
        _annotationTool = ImageAnnotation.Tool.Rectangle;
        _canvas.IsHitTestVisible = true;
        if (_liveAnnotateHost is not null)
        {
            _liveAnnotateHost.IsHitTestVisible = false;
        }

        if (_liveAnnotateOverlay is not null)
        {
            _liveAnnotateOverlay.IsHitTestVisible = false;
        }

        UpdateLiveToolbarHighlight();
    }

    private void SetLiveAnnotationTool(ImageAnnotation.Tool tool)
    {
        if (tool != ImageAnnotation.Tool.Rectangle)
        {
            _highlightMode = false;
        }

        _annotationTool = tool;
        _regionToolActive = false;
        _canvas.IsHitTestVisible = false;
        _liveAnnotateOverlay?.SetTool(tool);
        if (_liveAnnotateHost is not null)
        {
            _liveAnnotateHost.IsHitTestVisible = true;
        }

        if (_liveAnnotateOverlay is not null)
        {
            _liveAnnotateOverlay.IsHitTestVisible = true;
        }

        _cursorMarker.IsVisible = false;
        if (tool == ImageAnnotation.Tool.Text)
        {
            _annotationTextBox?.Focus();
        }

        UpdateLiveToolbarHighlight();
    }

    private void UpdateLiveToolbarHighlight()
    {
        foreach (KeyValuePair<ImageAnnotation.Tool, Button> entry in _liveToolButtons)
        {
            bool active = !_regionToolActive && _annotationTool == entry.Key &&
                          !(entry.Key == ImageAnnotation.Tool.Rectangle && _highlightMode);
            entry.Value.Background = active
                ? new SolidColorBrush(AvaloniaColor.FromRgb(62, 62, 66))
                : Brushes.Transparent;
            entry.Value.Opacity = active ? 1 : 0.75;
        }

        if (_regionToolButton is null)
        {
            return;
        }

        _regionToolButton.Background = _regionToolActive
            ? new SolidColorBrush(AvaloniaColor.FromRgb(62, 62, 66))
            : Brushes.Transparent;
        _regionToolButton.Opacity = _regionToolActive ? 1 : 0.75;
    }

    private static SixLabors.ImageSharp.Rectangle ScaleRect(
        SixLabors.ImageSharp.Rectangle rectangle,
        double scaleX,
        double scaleY) =>
        new(
            (int)Math.Round(rectangle.X * scaleX),
            (int)Math.Round(rectangle.Y * scaleY),
            Math.Max(1, (int)Math.Round(rectangle.Width * scaleX)),
            Math.Max(1, (int)Math.Round(rectangle.Height * scaleY)));

    private static ImageAnnotation ScaleAnnotation(ImageAnnotation annotation, double scaleX, double scaleY)
    {
        return annotation switch
        {
            RectangleAnnotation rectangle => new RectangleAnnotation
            {
                Rectangle = ScaleRect(rectangle.Rectangle, scaleX, scaleY),
                Color = rectangle.Color,
                Thickness = rectangle.Thickness,
                Filled = rectangle.Filled
            },
            RedactionAnnotation redaction => new RedactionAnnotation
            {
                Rectangle = ScaleRect(redaction.Rectangle, scaleX, scaleY)
            },
            FreehandAnnotation freehand => new FreehandAnnotation
            {
                Points = freehand.Points
                    .Select(point => new PointF((float)(point.X * scaleX), (float)(point.Y * scaleY)))
                    .ToList(),
                Color = freehand.Color,
                Thickness = freehand.Thickness
            },
            ArrowAnnotation arrow => new ArrowAnnotation
            {
                Start = new PointF((float)(arrow.Start.X * scaleX), (float)(arrow.Start.Y * scaleY)),
                End = new PointF((float)(arrow.End.X * scaleX), (float)(arrow.End.Y * scaleY)),
                Color = arrow.Color,
                Thickness = arrow.Thickness
            },
            TextAnnotation text => new TextAnnotation
            {
                Position = new PointF((float)(text.Position.X * scaleX), (float)(text.Position.Y * scaleY)),
                Text = text.Text,
                FontSize = text.FontSize,
                Color = text.Color
            },
            CropAnnotation crop => new CropAnnotation
            {
                Rectangle = ScaleRect(crop.Rectangle, scaleX, scaleY)
            },
            _ => annotation
        };
    }

    /// <summary>
    /// Transparent overlay for live annotations on the frozen desktop canvas.
    /// </summary>
    private sealed class LiveAnnotationOverlay : Control
    {
        private readonly Func<IReadOnlyList<ImageAnnotation>> _getAnnotations;
        private readonly Action<ImageAnnotation, ImageAnnotation.Tool> _onComplete;
        private readonly Func<string> _textProvider;
        private readonly Func<bool> _isHighlightMode;
        private Point _start;
        private Point _current;
        private bool _dragging;
        private ImageAnnotation.Tool _tool;
        private readonly List<Point> _freehandPoints = [];
        private long _lastInvalidateTicks;

        public LiveAnnotationOverlay(
            Func<IReadOnlyList<ImageAnnotation>> getAnnotations,
            Action<ImageAnnotation, ImageAnnotation.Tool> onComplete,
            Func<string> textProvider,
            Func<bool> isHighlightMode)
        {
            _getAnnotations = getAnnotations;
            _onComplete = onComplete;
            _textProvider = textProvider;
            _isHighlightMode = isHighlightMode;
            ClipToBounds = true;
        }

        public void SetTool(ImageAnnotation.Tool tool) => _tool = tool;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            foreach (ImageAnnotation annotation in _getAnnotations())
            {
                DrawCommittedAnnotation(context, annotation);
            }

            if (_dragging && _tool != ImageAnnotation.Tool.Freehand)
            {
                var rect = MakeRect(_start, _current);
                switch (_tool)
                {
                    case ImageAnnotation.Tool.Rectangle:
                        if (_isHighlightMode())
                        {
                            context.FillRectangle(
                                new SolidColorBrush(AvaloniaColor.FromArgb(128, 255, 255, 0)),
                                rect);
                        }
                        else
                        {
                            DrawOutline(context, rect, Brushes.Red);
                        }

                        break;
                    case ImageAnnotation.Tool.Redaction:
                        context.FillRectangle(Brushes.Black, rect);
                        break;
                    case ImageAnnotation.Tool.Arrow:
                        context.DrawLine(new Pen(Brushes.Green, 3), _start, _current);
                        break;
                }
            }

            if (_dragging && _tool == ImageAnnotation.Tool.Freehand && _freehandPoints.Count >= 2)
            {
                var pen = new Pen(Brushes.Yellow, 3);
                for (int i = 1; i < _freehandPoints.Count; i++)
                {
                    context.DrawLine(pen, _freehandPoints[i - 1], _freehandPoints[i]);
                }
            }
        }

        private static void DrawCommittedAnnotation(DrawingContext context, ImageAnnotation annotation)
        {
            switch (annotation)
            {
                case RectangleAnnotation rectangle:
                    if (rectangle.Filled)
                    {
                        context.FillRectangle(
                            new SolidColorBrush(AvaloniaColor.FromArgb(128, 255, 255, 0)),
                            ToRect(rectangle.Rectangle));
                    }
                    else
                    {
                        DrawOutline(context, ToRect(rectangle.Rectangle), Brushes.Red);
                    }

                    break;
                case RedactionAnnotation redaction:
                    context.FillRectangle(Brushes.Black, ToRect(redaction.Rectangle));
                    break;
                case FreehandAnnotation freehand when freehand.Points.Count >= 2:
                {
                    var pen = new Pen(Brushes.Yellow, freehand.Thickness);
                    for (int i = 1; i < freehand.Points.Count; i++)
                    {
                        var a = freehand.Points[i - 1];
                        var b = freehand.Points[i];
                        context.DrawLine(pen, new Point(a.X, a.Y), new Point(b.X, b.Y));
                    }

                    break;
                }
                case ArrowAnnotation arrow:
                    context.DrawLine(new Pen(Brushes.Green, arrow.Thickness), new Point(arrow.Start.X, arrow.Start.Y), new Point(arrow.End.X, arrow.End.Y));
                    break;
                case TextAnnotation text when !string.IsNullOrWhiteSpace(text.Text):
                    context.DrawText(
                        new FormattedText(
                            text.Text,
                            System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            Typeface.Default,
                            text.FontSize > 0 ? text.FontSize : 18,
                            Brushes.White),
                        new Point(text.Position.X, text.Position.Y));
                    break;
            }
        }

        private static Rect ToRect(SixLabors.ImageSharp.Rectangle rectangle) =>
            new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

        private static void DrawOutline(DrawingContext context, Rect rect, IBrush brush)
        {
            var pen = new Pen(brush, 2);
            context.DrawLine(pen, new Point(rect.X, rect.Y), new Point(rect.Right, rect.Y));
            context.DrawLine(pen, new Point(rect.Right, rect.Y), new Point(rect.Right, rect.Bottom));
            context.DrawLine(pen, new Point(rect.Right, rect.Bottom), new Point(rect.X, rect.Bottom));
            context.DrawLine(pen, new Point(rect.X, rect.Bottom), new Point(rect.X, rect.Y));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _start = e.GetPosition(this);
            _current = _start;
            if (_tool == ImageAnnotation.Tool.Text)
            {
                string value = _textProvider();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _onComplete(new TextAnnotation
                    {
                        Position = new PointF((float)_start.X, (float)_start.Y),
                        Text = value,
                        FontSize = 18,
                        Color = SharpColor.White
                    }, _tool);
                    InvalidateVisual();
                }

                e.Handled = true;
                return;
            }

            _dragging = true;
            _freehandPoints.Clear();
            _freehandPoints.Add(_start);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging)
            {
                return;
            }

            _current = e.GetPosition(this);
            if (_tool == ImageAnnotation.Tool.Freehand)
            {
                _freehandPoints.Add(_current);
            }

            long now = Environment.TickCount64;
            if (now - _lastInvalidateTicks >= 16)
            {
                _lastInvalidateTicks = now;
                InvalidateVisual();
            }

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging)
            {
                return;
            }

            _current = e.GetPosition(this);
            _dragging = false;
            e.Pointer.Capture(null);
            Commit();
            e.Handled = true;
        }

        private void Commit()
        {
            var rect = MakeRect(_start, _current);
            switch (_tool)
            {
                case ImageAnnotation.Tool.Rectangle:
                    _onComplete(new RectangleAnnotation
                    {
                        Rectangle = ToSharp(rect),
                        Color = _isHighlightMode() ? SharpColor.Yellow : SharpColor.Red,
                        Thickness = 2,
                        Filled = _isHighlightMode()
                    }, _tool);
                    break;
                case ImageAnnotation.Tool.Redaction:
                    _onComplete(new RedactionAnnotation { Rectangle = ToSharp(rect) }, _tool);
                    break;
                case ImageAnnotation.Tool.Freehand:
                    _onComplete(new FreehandAnnotation
                    {
                        Points = _freehandPoints.Select(p => new PointF((float)p.X, (float)p.Y)).ToList(),
                        Color = SharpColor.Yellow,
                        Thickness = 3
                    }, _tool);
                    break;
                case ImageAnnotation.Tool.Arrow:
                    _onComplete(new ArrowAnnotation
                    {
                        Start = new PointF((float)_start.X, (float)_start.Y),
                        End = new PointF((float)_current.X, (float)_current.Y),
                        Color = SharpColor.Green,
                        Thickness = 3
                    }, _tool);
                    break;
                case ImageAnnotation.Tool.Text:
                    string value = _textProvider();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _onComplete(new TextAnnotation
                        {
                            Position = new PointF((float)_start.X, (float)_start.Y),
                            Text = value,
                            FontSize = 18,
                            Color = SharpColor.White
                        }, _tool);
                    }

                    break;
            }

            InvalidateVisual();
        }

        private static Rect MakeRect(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            double w = Math.Abs(a.X - b.X);
            double h = Math.Abs(a.Y - b.Y);
            return new Rect(x, y, w, h);
        }

        private static SixLabors.ImageSharp.Rectangle ToSharp(Rect rect) =>
            new((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
    }

    private void CancelAnnotation()
    {
        FinishCapture(_image!);
    }

    private void FinishCapture(Image result)
    {
        _resultImg.TrySetResult(result);
        if (!IsSilentMode)
        {
            DebugHelper.WriteLine("Running image task");
            UploadManager.RunImageTask(result, TaskSettings.GetDefaultTaskSettings());
        }
        _annotationBitmap?.Dispose();
        _annotationBitmap = null;
        RestoreMainWindowIfNeeded();
        Close();
    }

    /// <summary>
    /// Renders the captured region image and lets the user draw inline
    /// annotations over it. Mirrors the annotation canvas in
    /// <see cref="CapturedImageEditorWindow"/> so the inline (non-modal)
    /// annotate surface behaves identically to the modal editor's canvas.
    /// </summary>
    private sealed class AnnotationSurface : Control
    {
        private readonly WriteableBitmap _bitmap;
        private readonly Action<ImageAnnotation, ImageAnnotation.Tool> _onComplete;
        private readonly Func<string> _textProvider;
        private Point _start;
        private Point _current;
        private bool _dragging;
        private ImageAnnotation.Tool _tool;
        private readonly List<Point> _freehandPoints = [];

        public AnnotationSurface(WriteableBitmap bitmap, Action<ImageAnnotation, ImageAnnotation.Tool> onComplete, Func<string> textProvider)
        {
            _bitmap = bitmap;
            _onComplete = onComplete;
            _textProvider = textProvider;
            ClipToBounds = true;
        }

        public void SetTool(ImageAnnotation.Tool tool) => _tool = tool;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height));

            if (_dragging && _tool != ImageAnnotation.Tool.Freehand)
            {
                var rect = MakeRect(_start, _current);
                switch (_tool)
                {
                    case ImageAnnotation.Tool.Rectangle:
                        DrawOutline(context, rect, Brushes.Red);
                        break;
                    case ImageAnnotation.Tool.Redaction:
                        context.FillRectangle(Brushes.Black, rect);
                        break;
                    case ImageAnnotation.Tool.Arrow:
                        context.DrawLine(new Pen(Brushes.Green, 3), _start, _current);
                        break;
                    case ImageAnnotation.Tool.Crop:
                        DrawOutline(context, rect, Brushes.Yellow);
                        break;
                }
            }

            if (_dragging && _tool == ImageAnnotation.Tool.Freehand && _freehandPoints.Count >= 2)
            {
                var pen = new Pen(Brushes.Yellow, 3);
                for (int i = 1; i < _freehandPoints.Count; i++)
                {
                    context.DrawLine(pen, _freehandPoints[i - 1], _freehandPoints[i]);
                }
            }
        }

        private static void DrawOutline(DrawingContext context, Rect rect, IBrush brush)
        {
            var pen = new Pen(brush, 2);
            context.DrawLine(pen, new Point(rect.X, rect.Y), new Point(rect.Right, rect.Y));
            context.DrawLine(pen, new Point(rect.Right, rect.Y), new Point(rect.Right, rect.Bottom));
            context.DrawLine(pen, new Point(rect.Right, rect.Bottom), new Point(rect.X, rect.Bottom));
            context.DrawLine(pen, new Point(rect.X, rect.Bottom), new Point(rect.X, rect.Y));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }
            _start = e.GetPosition(this);
            _current = _start;
            _dragging = true;
            _freehandPoints.Clear();
            _freehandPoints.Add(_start);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging)
            {
                return;
            }
            _current = e.GetPosition(this);
            if (_tool == ImageAnnotation.Tool.Freehand)
            {
                _freehandPoints.Add(_current);
            }
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging)
            {
                return;
            }
            _current = e.GetPosition(this);
            _dragging = false;
            Commit();
            e.Handled = true;
        }

        private void Commit()
        {
            var rect = MakeRect(_start, _current);
            switch (_tool)
            {
                case ImageAnnotation.Tool.Rectangle:
                    _onComplete(new RectangleAnnotation
                    {
                        Rectangle = ToSharp(rect),
                        Color = SharpColor.Red,
                        Thickness = 2
                    }, _tool);
                    break;
                case ImageAnnotation.Tool.Redaction:
                    _onComplete(new RedactionAnnotation { Rectangle = ToSharp(rect) }, _tool);
                    break;
                case ImageAnnotation.Tool.Freehand:
                    _onComplete(new FreehandAnnotation
                    {
                        Points = _freehandPoints.Select(p => new PointF((float)p.X, (float)p.Y)).ToList(),
                        Color = SharpColor.Yellow,
                        Thickness = 3
                    }, _tool);
                    break;
                case ImageAnnotation.Tool.Arrow:
                    _onComplete(new ArrowAnnotation
                    {
                        Start = new PointF((float)_start.X, (float)_start.Y),
                        End = new PointF((float)_current.X, (float)_current.Y),
                        Color = SharpColor.Green,
                        Thickness = 3
                    }, _tool);
                    break;
                case ImageAnnotation.Tool.Crop:
                    _onComplete(new CropAnnotation { Rectangle = ToSharp(rect) }, _tool);
                    break;
                case ImageAnnotation.Tool.Text:
                    string value = _textProvider();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _onComplete(new TextAnnotation
                        {
                            Position = new PointF((float)_start.X, (float)_start.Y),
                            Text = value,
                            Color = SharpColor.White
                        }, _tool);
                    }
                    break;
            }
            InvalidateVisual();
        }

        private static Rect MakeRect(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            double w = Math.Abs(a.X - b.X);
            double h = Math.Abs(a.Y - b.Y);
            return new Rect(x, y, w, h);
        }

        private static SixLabors.ImageSharp.Rectangle ToSharp(Rect rect) =>
            new((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
    }

    private Task CancelSelection()
    {
        if (_selectionCompleted)
        {
            return Task.CompletedTask;
        }

        _selectionCompleted = true;
        _isSelecting = false;
        _resultRect.TrySetResult(null);
        _resultImg.TrySetResult(null);
        Close();
        return Task.CompletedTask;
    }
    private void UpdateWindowHover(Point canvasPoint)
    {
        var screenPoint = new SixLabors.ImageSharp.Point(
            (int)(_screenBounds.X + canvasPoint.X),
            (int)(_screenBounds.Y + canvasPoint.Y));

        WindowInfo? hovered = _pickableWindows.FirstOrDefault(window => window.Rectangle.Contains(screenPoint));

        if (!_captureOptions.WindowOrRegionPickerMode &&
            ReferenceEquals(hovered, _hoveredWindow)) return;
        _hoveredWindow = hovered;

        if (hovered is null)
        {
            _selectionRect.IsVisible = false;
            _infoBox.IsVisible = false;
            return;
        }

        double x = hovered.Rectangle.X - _screenBounds.X;
        double y = hovered.Rectangle.Y - _screenBounds.Y;
        _selectionRect.Width = hovered.Rectangle.Width;
        _selectionRect.Height = hovered.Rectangle.Height;
        SetSelectionRect(x, y, hovered.Rectangle.Width, hovered.Rectangle.Height);
        _selectionRect.IsVisible = true;

        string title = string.IsNullOrWhiteSpace(hovered.Title) ? hovered.ProcessName : hovered.Title;
        _infoBox.Text = $"{title} ({hovered.Rectangle.Width:0}x{hovered.Rectangle.Height:0})";
        Canvas.SetLeft(_infoBox, x);
        Canvas.SetTop(_infoBox, Math.Max(0, y - 30));
        _infoBox.IsVisible = true;
    }

    private void SelectHoveredWindow(WindowInfo window)
    {
        double x = window.Rectangle.X - _screenBounds.X;
        double y = window.Rectangle.Y - _screenBounds.Y;
        _selectionRect.Width = window.Rectangle.Width;
        _selectionRect.Height = window.Rectangle.Height;
        SetSelectionRect(x, y, window.Rectangle.Width, window.Rectangle.Height);
        _isSelecting = true;
        OnPointerReleased(this, null);
    }

    private bool IsPressInsideHoveredWindow(WindowInfo window, PointerPressedEventArgs e)
    {
        // The pointer press lands on the selector canvas. A press that is
        // inside the hovered window rectangle picks the window. A press in
        // open space starts a region drag. This matches the native Wayland
        // picker: click for a window, drag for a region.
        Point pressPoint = e.GetPosition(_canvas);
        return pressPoint.X >= window.Rectangle.X - _screenBounds.X &&
               pressPoint.Y >= window.Rectangle.Y - _screenBounds.Y &&
               pressPoint.X <= (window.Rectangle.X - _screenBounds.X) + window.Rectangle.Width &&
               pressPoint.Y <= (window.Rectangle.Y - _screenBounds.Y) + window.Rectangle.Height;
    }

    private void RecordPressPoint(Point canvasPoint)
    {
        _pressedPoint = canvasPoint;
    }

    private static bool IsDragBeyondThreshold(Point pressPoint, Point releasePoint)
    {
        // Squared distance keeps a small movement a click instead of a drag.
        // A release more than five pixels from the press becomes a region.
        // This matches the native Wayland picker click threshold.
        double dx = pressPoint.X - releasePoint.X;
        double dy = pressPoint.Y - releasePoint.Y;
        return dx * dx + dy * dy > DragDistanceSquaredLimit;
    }

    /// <summary>
    /// Centres the plus marker on the pointer so the effective cursor reads as
    /// a draggable plus. Purely cosmetic: it never participates in hit testing
    /// and never feeds back into the selection rectangle maths.
    /// </summary>
    private void MoveCursorMarker(Point position)
    {
        if (_cursorMarker is null) return;

        Canvas.SetLeft(_cursorMarker, position.X - (_cursorMarker.Width / 2));
        Canvas.SetTop(_cursorMarker, position.Y - (_cursorMarker.Height / 2));

        if (!_cursorMarker.IsVisible) _cursorMarker.IsVisible = true;
    }

    private async void OnPointerMoved(object? Sender, PointerEventArgs E)
    {
        long now = Environment.TickCount64;
        bool throttle = now - _lastPointerMoveTicks < PointerMoveThrottleMs;
        if (!throttle)
        {
            _lastPointerMoveTicks = now;
        }

        if (_liveAnnotateSession && !_regionToolActive)
        {
            return;
        }

        if (!throttle || _isSelecting)
        {
            MoveCursorMarker(E.GetPosition(_canvas));
        }

        if (!_isSelecting)
        {
            if (_captureOptions.WindowPickerMode ||
                _captureOptions.WindowOrRegionPickerMode)
            {
                if (!throttle)
                {
                    UpdateWindowHover(E.GetPosition(_canvas));
                }
            }

            return;
        }

        // Never throttle an active region drag; the first pixels of movement
        // must paint the selection outline immediately.
        var endPoint = E.GetPosition(_canvas);
        var x = Math.Min(_startPoint.X, endPoint.X);
        var y = Math.Min(_startPoint.Y, endPoint.Y);
        var width = Math.Abs(_startPoint.X - endPoint.X);
        var height = Math.Abs(_startPoint.Y - endPoint.Y);
        if (_captureOptions.IsFixedSize && _captureOptions.FixedSize.Width > 0 && _captureOptions.FixedSize.Height > 0)
        {
            width = _captureOptions.FixedSize.Width;
            height = _captureOptions.FixedSize.Height;
            x = endPoint.X < _startPoint.X ? _startPoint.X - width : _startPoint.X;
            y = endPoint.Y < _startPoint.Y ? _startPoint.Y - height : _startPoint.Y;
        }

        x = Math.Clamp(x, 0, Math.Max(0, _imageBounds.Width - width));
        y = Math.Clamp(y, 0, Math.Max(0, _imageBounds.Height - height));
        width = Math.Min(width, _imageBounds.Width);
        height = Math.Min(height, _imageBounds.Height);
        _selectionRect.Width = width;
        _selectionRect.Height = height;
        SetSelectionRect(x, y, width, height);
        var infoText =
            $"X: {_screenBounds.X + (int)x}, Y: {_screenBounds.Y + (int)y}, Width: {(int)width}, Height: {(int)height}";
        if (_infoBox.Text != infoText)
        {
            _infoBox.Text = infoText;
        }

        Canvas.SetLeft(_infoBox, x);
        Canvas.SetTop(_infoBox, Math.Max(0, y - 30));
    }

    private void SetSelectionRect(double x, double y, double width, double height)
    {
        Canvas.SetLeft(_selectionRect, x);
        Canvas.SetTop(_selectionRect, y);
        _selectionRect.Width = width;
        _selectionRect.Height = height;
    }

    private void RestoreMainWindowIfNeeded()
    {
        if (!_mainWindowWasVisibleBeforeCapture)
        {
            return;
        }

        try
        {
            App.RestoreAndFocusMainWindow();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"Could not restore main window: {ex.Message}");
        }
    }

    private void UpdateLiveToolbarPlacement()
    {
        if (_liveToolbarHost is null)
        {
            return;
        }

        _liveToolbarHost.Margin = new Thickness(0, _toolbarTopMargin, 0, 0);
        if (_liveAnnotateSession)
        {
            _liveToolbarHost.IsVisible = true;
        }
    }

    private static SixLabors.ImageSharp.Image CreateDisplayBackground(
        SixLabors.ImageSharp.Image source,
        PixelRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            (source.Width == bounds.Width && source.Height == bounds.Height))
        {
            return source.Clone(_ => { });
        }

        return source.Clone(context => context.Resize(bounds.Width, bounds.Height));
    }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        DebugHelper.WriteLine($"{sender}.OnKeyDown: Key: {e.Key}");
        switch (e.Key)
        {
            case Key.Enter:
                if (_isSelecting)
                {
                    OnPointerReleased(this, null);
                }
                break;
            case Key.Tab:
                if (_liveAnnotateSession)
                {
                    if (_regionToolActive)
                    {
                        SetLiveAnnotationTool(_annotationTool);
                    }
                    else
                    {
                        SetRegionToolActive();
                    }

                    e.Handled = true;
                }

                break;
            case Key.Escape:
                _ = CancelSelection();
                break;
        }
    }
    private bool _mainWindowWasVisibleBeforeCapture;
    private readonly Dictionary<Window, WindowBase?> _ownershipMap = new();

    private async Task<bool> PrepareAndShowAsync(CancellationToken cancellationToken = default)
    {
        // Reject duplicate requests before they hide windows or ask grim for a
        // frame.  Preparing a rejected selector captures the active selector
        // overlay, which produces a recursively dark/outlined screenshot.
        if (!TryAcquireSelectorGate())
        {
            DebugHelper.WriteLine("A region selector is already active; ignoring the duplicate request.");
            return false;
        }

        if (cancellationToken.IsCancellationRequested || IsCancellationRequested)
        {
            ReleaseSelectorGate();
            return false;
        }

        if (!await PrepareForDisplayAsync())
        {
            RestoreHiddenWindows();
            ReleaseSelectorGate();
            return false;
        }

        // A token may be cancelled while the compositor screenshot is in
        // flight. Never map a selector after that cancellation; doing so can
        // leave a hidden-window/selector combination with no caller waiting
        // to close it.
        if (cancellationToken.IsCancellationRequested || IsCancellationRequested)
        {
            RestoreHiddenWindows();
            ReleaseSelectorGate();
            return false;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsVisible = true;
                Opacity = 1;
            });

            if (!_layoutApplied && _image is not null)
            {
                await SetupSelectorLayoutAsync();
                _layoutApplied = true;
            }

            Show();

            if (IsNativeWayland)
            {
                await EnsureHyprlandSelectorOverlayAsync(_screenBounds, cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(UpdateLiveToolbarPlacement);
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"RegionSelectorWindow: Show() failed or aborted: {ex.Message}");
            RestoreHiddenWindows();
            ReleaseSelectorGate();
            return false;
        }
    }

    private Task<bool> PrepareForDisplayAsync()
    {
        // Avalonia can raise the window setup path more than once while the
        // first await is in flight. Coalesce that work: a second grim request
        // would capture this selector's own overlay instead of the desktop.
        lock (_preparationLock)
        {
            return _preparationTask ??= PrepareForDisplayCoreAsync();
        }
    }

    private async Task<bool> PrepareForDisplayCoreAsync()
    {
        if (!_ownsSelector)
        {
            return false;
        }

        if (_preparedForDisplay)
        {
            return _captureReady;
        }

        await Dispatcher.UIThread.InvokeAsync(HideSnapXWindows, DispatcherPriority.Send);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            if (IsCancellationRequested)
            {
                RestoreHiddenWindows();
                return false;
            }

            // Hiding a window is asynchronous from the compositor's point of
            // view. Give Hyprland a frame to remove SnapX before grim captures
            // the desktop that will become the selector's background.
            await Task.Delay(IsNativeWayland ? 100 : 50);
            if (IsCancellationRequested)
            {
                RestoreHiddenWindows();
                return false;
            }
            var captureTask = Task.Factory.StartNew(
                async () => await TaskHelpers.GetScreenshot().CaptureActiveMonitor(),
                TaskCreationOptions.LongRunning).Unwrap();
            _image = await captureTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (IsCancellationRequested)
            {
                _image?.Dispose();
                _image = null;
                RestoreHiddenWindows();
                return false;
            }
            if (_image is null)
            {
                DebugHelper.WriteLine("RegionSelectorWindow: capture returned no image.");
                RestoreHiddenWindows();
                return false;
            }

            // On NVIDIA-backed Hyprland (and some other compositors) grim can
            // return an all-black frame immediately after the SnapX windows are
            // hidden. Showing that as the selector background produces a
            // full-screen black overlay that looks like the machine crashed.
            if (IsLikelyBlackFrame(_image))
            {
                _image.Dispose();
                _image = null;

                // Give the compositor one more frame, then retry the capture.
                // A second attempt is cheap and usually returns the actual
                // desktop once the hide has been committed to the output.
                await Task.Delay(250);
                if (IsCancellationRequested)
                {
                    RestoreHiddenWindows();
                    return false;
                }
                captureTask = Task.Factory.StartNew(
                    async () => await TaskHelpers.GetScreenshot().CaptureActiveMonitor(),
                    TaskCreationOptions.LongRunning).Unwrap();
                _image = await captureTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (IsCancellationRequested)
                {
                    _image?.Dispose();
                    _image = null;
                    RestoreHiddenWindows();
                    return false;
                }
                if (_image is null || IsLikelyBlackFrame(_image))
                {
                    _image?.Dispose();
                    _image = null;
                    DebugHelper.WriteLine(
                        "RegionSelectorWindow: captured background is black; aborting the selector instead of showing a black overlay.");
                    RestoreHiddenWindows();
                    return false;
                }
            }

            // The selector background is only ever shown, never re-read as a
            // file, so the PNG encode/decode round trip this used to do
            // (SaveAsPngAsync + new Bitmap(stream)) was pure overhead on top
            // of every region/window selector open.
            // Downscale once for display. Painting a 2560x1440 brush on every
            // frame while dragging a region was the main source of input lag.
            PixelRect displayBounds = await ResolveSelectorBoundsAsync();
            using SixLabors.ImageSharp.Image displayImage = CreateDisplayBackground(_image, displayBounds);
            var backgroundBitmap = App.SnapX.ConvertImageSharpImgToAvalonia(displayImage);
            DebugHelper.WriteLine(
                $"Selector background: {backgroundBitmap.PixelSize} from raw image bounds {_image.Bounds}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _backgroundImage = new global::Avalonia.Controls.Image
                {
                    Source = backgroundBitmap,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    Width = displayBounds.Width,
                    Height = displayBounds.Height,
                };
                Canvas.SetLeft(_backgroundImage, 0);
                Canvas.SetTop(_backgroundImage, 0);
                _canvas.Children.Insert(0, _backgroundImage);
                Background = Brushes.Transparent;
                ApplySelectorLayout(displayBounds);
                _layoutApplied = true;
                if (NeedsLiveAnnotateSession(_captureOptions))
                {
                    InitializeLiveAnnotateSession();
                    UpdateLiveToolbarPlacement();
                }
            });
            if (_captureOptions.WindowPickerMode)
            {
                var screenRect = new SixLabors.ImageSharp.Rectangle(
                    _screenBounds.X, _screenBounds.Y, _screenBounds.Width, _screenBounds.Height);
                _pickableWindows = Methods.GetWindowList()
                    .Where(window => window.IsVisible && !window.Rectangle.IsEmpty && window.Rectangle.IntersectsWith(screenRect))
                    .OrderBy(window => window.Rectangle.Width * (long)window.Rectangle.Height)
                    .ToList();
            }
            else if (_captureOptions.WindowOrRegionPickerMode && !IsNativeWayland)
            {
                List<WindowInfo> topLevelWindows = Methods.GetWindowList();

                // Bare X11 sessions have no EWMH client list, so augment the
                // platform list with a raw window-tree walk. This extra walk
                // is Linux only: libX11 is absent on Windows and macOS.
                IEnumerable<WindowInfo> candidates = topLevelWindows;
                if (OperatingSystem.IsLinux())
                {
                    List<WindowInfo> treeWindows = GetPickableX11TreeWindows();
                    candidates = candidates.Concat(treeWindows);
                }

                _pickableWindows = candidates
                    // SetupWindowBoundsAsync assigns _screenBounds after this
                    // preparation step. Keep all visible X11 windows here;
                    // hover lookup naturally ignores windows outside the
                    // selector's screen once those bounds are available.
                    .Where(window => window.IsVisible && !window.Rectangle.IsEmpty)
                    .OrderBy(window => window.Rectangle.Width * (long)window.Rectangle.Height)
                    .ToList();
            }

            _preparedForDisplay = true;
            _captureReady = true;
            Opacity = 1;
            return true;
        }
        catch (Exception ex)
        {
            ShowErrorDialog(ex);
            RestoreHiddenWindows();
            return false;
        }
    }

    private static List<WindowInfo> GetPickableX11TreeWindows()
    {
        var windows = new List<WindowInfo>();
        IntPtr display = PickerXOpenDisplay(null);
        if (display == IntPtr.Zero)
        {
            return windows;
        }

        try
        {
            IntPtr root = PickerXDefaultRootWindow(display);
            AddPickableX11ChildWindows(display, root, 0, 0, windows);
        }
        finally
        {
            PickerXCloseDisplay(display);
        }

        return windows;
    }

    private static void AddPickableX11ChildWindows(
        IntPtr display,
        IntPtr parent,
        int parentX,
        int parentY,
        List<WindowInfo> windows)
    {
        if (PickerXQueryTree(display, parent, out _, out _, out IntPtr children, out uint childCount) == 0 ||
            children == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (uint index = 0; index < childCount; index++)
            {
                IntPtr child = Marshal.ReadIntPtr(children, checked((int)(index * IntPtr.Size)));
                if (PickerXGetWindowAttributes(display, child, out PickerXWindowAttributes attributes) == 0 ||
                    attributes.MapState != 2 || attributes.Width <= 1 || attributes.Height <= 1)
                {
                    continue;
                }

                int childX = parentX + attributes.X;
                int childY = parentY + attributes.Y;

                // Bare X servers do not publish an EWMH client list. Include
                // every visible descendant because test tools and similar X11
                // surfaces can live below a top-level window and would
                // otherwise never become pickable.
                windows.Add(new WindowInfo
                {
                    Handle = child,
                    IsVisible = true,
                    Rectangle = new SixLabors.ImageSharp.Rectangle(
                        childX, childY, attributes.Width, attributes.Height)
                });

                AddPickableX11ChildWindows(display, child, childX, childY, windows);
            }
        }
        finally
        {
            PickerXFree(children);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PickerXWindowAttributes
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int BorderWidth;
        public int Depth;
        public IntPtr Visual;
        public IntPtr Root;
        public int Class;
        public int BitGravity;
        public int WindowGravity;
        public int BackingStore;
        public IntPtr BackingPlanes;
        public IntPtr BackingPixel;
        public int SaveUnder;
        public IntPtr Colormap;
        public int MapInstalled;
        public int MapState;
        public IntPtr AllEventMasks;
        public IntPtr YourEventMask;
        public IntPtr DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Screen;
    }

    [LibraryImport("libX11.so.6", EntryPoint = "XOpenDisplay", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr PickerXOpenDisplay(string? displayName);

    [LibraryImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static partial int PickerXCloseDisplay(IntPtr display);

    [LibraryImport("libX11.so.6", EntryPoint = "XDefaultRootWindow")]
    private static partial IntPtr PickerXDefaultRootWindow(IntPtr display);

    [LibraryImport("libX11.so.6", EntryPoint = "XQueryTree")]
    private static partial int PickerXQueryTree(
        IntPtr display,
        IntPtr window,
        out IntPtr root,
        out IntPtr parent,
        out IntPtr children,
        out uint childCount);

    [LibraryImport("libX11.so.6", EntryPoint = "XGetWindowAttributes")]
    private static partial int PickerXGetWindowAttributes(
        IntPtr display,
        IntPtr window,
        out PickerXWindowAttributes attributes);

    [LibraryImport("libX11.so.6", EntryPoint = "XFree")]
    private static partial int PickerXFree(IntPtr data);

    private void HideSnapXWindows()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideSnapXWindows, DispatcherPriority.Send);
            return;
        }

        _mainWindowWasVisibleBeforeCapture = App.MyMainWindow is { IsVisible: true };

        if (App.MyMainWindow is { IsVisible: true } mainWindow && !windowsHiddenByUs.Contains(mainWindow))
        {
            _ownershipMap[mainWindow] = mainWindow.Owner;
            mainWindow.Hide();
            windowsHiddenByUs.Add(mainWindow);
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (Window window in desktop.Windows.ToArray())
            {
                if (window == this || !window.IsVisible)
                {
                    continue;
                }

                if (windowsHiddenByUs.Contains(window))
                {
                    continue;
                }

                _ownershipMap[window] = window.Owner;
                window.Hide();
                windowsHiddenByUs.Add(window);
            }
        }

        foreach (var win in App.MyMainWindow?.OwnedWindows.Where(w => w != this && w.IsVisible) ?? [])
        {
            if (windowsHiddenByUs.Contains(win))
            {
                continue;
            }

            _ownershipMap[win] = win.Owner;
            win.Hide();
            windowsHiddenByUs.Add(win);
        }
    }

    private static bool CanRestoreWindow(Window win) => win.IsLoaded && win.PlatformImpl is not null;

    private void RestoreHiddenWindows()
    {
        var sortedWindows = TopoSortWindows(windowsHiddenByUs);
        foreach (var win in sortedWindows)
        {
            try
            {
                if (!CanRestoreWindow(win))
                {
                    continue;
                }

                if (ReferenceEquals(win, App.MyMainWindow) && !_mainWindowWasVisibleBeforeCapture)
                {
                    continue;
                }

                if (_ownershipMap.TryGetValue(win, out var owner) &&
                    owner is Window ownerWindow &&
                    CanRestoreWindow(ownerWindow) &&
                    ownerWindow.IsVisible)
                {
                    win.Show(ownerWindow);
                }
                else
                {
                    win.Show();
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteLine($"Could not restore hidden window: {ex.Message}");
            }
        }

        _ownershipMap.Clear();
        windowsHiddenByUs.Clear();
    }
    List<Window> TopoSortWindows(IEnumerable<Window> windows)
    {
        var result = new List<Window>();
        var visited = new HashSet<Window>();

        void Visit(Window w)
        {
            if (!visited.Add(w))
                return;

            foreach (var child in w.OwnedWindows)
            {
                if (windowsHiddenByUs.Contains(child))
                    Visit(child);
            }

            result.Add(w);
        }

        foreach (var w in windows)
            Visit(w);

        result.Reverse(); // owners before owned
        return result;
    }
    private void OnClosed(object? Sender, EventArgs E)
    {
        _isSelecting = false;
        _captureReady = false;
        _resultRect.TrySetResult(null);
        _resultImg.TrySetResult(null);
        _imageStream?.Dispose();
        _imageStream = null;
        RestoreHiddenWindows();

        ReleaseSelectorGate();
    }

    private bool IsCancellationRequested => Volatile.Read(ref _cancellationRequested) != 0;

    private static bool IsNativeWayland => OperatingSystem.IsLinux() && LinuxAPI.IsWayland();

    internal static Task EnsureHyprlandAnnotateOverlayAsync(PixelRect bounds) =>
        EnsureHyprlandOverlayAsync(bounds, "title:SnapX annotate");

    private static async Task EnsureHyprlandSelectorOverlayAsync(
        PixelRect bounds,
        CancellationToken cancellationToken = default) =>
        await EnsureHyprlandOverlayAsync(bounds, "title:RegionSelectorWindow", cancellationToken);

    private static async Task EnsureHyprlandOverlayAsync(
        PixelRect bounds,
        string windowSelector,
        CancellationToken cancellationToken = default)
    {
        if (!IsNativeWayland)
        {
            return;
        }

        await Task.Delay(80, cancellationToken).ConfigureAwait(false);

        await RunHyprctlDispatchAsync(
            $"hl.dsp.focus({{ window = '{windowSelector}' }})",
            cancellationToken).ConfigureAwait(false);
        await RunHyprctlDispatchAsync(
            $"hl.dsp.window.float({{ window = '{windowSelector}', action = 'enable' }})",
            cancellationToken).ConfigureAwait(false);
        await RunHyprctlDispatchAsync(
            $"hl.dsp.window.move({{ window = '{windowSelector}', x = {bounds.X}, y = {bounds.Y} }})",
            cancellationToken).ConfigureAwait(false);
        await RunHyprctlDispatchAsync(
            $"hl.dsp.window.resize({{ window = '{windowSelector}', x = {bounds.Width}, y = {bounds.Height} }})",
            cancellationToken).ConfigureAwait(false);

        DebugHelper.WriteLine(
            $"Hyprland overlay {windowSelector} positioned at {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}");
    }

    private static async Task RunHyprctlDispatchAsync(string dispatch, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "hyprctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("dispatch");
        startInfo.ArgumentList.Add(dispatch);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            DebugHelper.WriteLine($"Region selector hyprctl dispatch failed to start: {dispatch}");
            return;
        }

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            DebugHelper.WriteLine(
                $"Region selector hyprctl dispatch failed ({process.ExitCode}): {dispatch} {error}".Trim());
        }
        else if (!string.IsNullOrWhiteSpace(output))
        {
            DebugHelper.WriteLine($"Region selector hyprctl: {output.Trim()}");
        }
    }

    private void RequestCancellation()
    {
        Interlocked.Exchange(ref _cancellationRequested, 1);
        _isSelecting = false;
        _selectionCompleted = true;
        _resultRect.TrySetResult(null);
        _resultImg.TrySetResult(null);

        if (IsVisible)
        {
            Close();
        }
    }

    private void ReleaseSelectorGate()
    {
        if (_ownsSelector && Interlocked.Exchange(ref _selectorGateReleased, 1) == 0)
        {
            Interlocked.Exchange(ref selectorOpen, 0);
        }
    }

    private bool TryAcquireSelectorGate()
    {
        if (_ownsSelector)
        {
            return true;
        }

        if (Interlocked.CompareExchange(ref selectorOpen, 1, 0) != 0)
        {
            return false;
        }

        _ownsSelector = true;
        return true;
    }

    /// <summary>
    /// Detects an all-black (or nearly all-black) selector background. A black
    /// frame means grim captured the output before the hidden SnapX windows
    /// left the compositor, so showing the selector would make the whole
    /// screen appear black until the user happens to close the window.
    /// </summary>
    private static bool IsLikelyBlackFrame(Image image)
    {
        if (image is null)
        {
            return true;
        }

        try
        {
            // Sample a grid of 9 points. This is deliberately cheap and does
            // not need to be pixel-perfect; any bright pixel means the frame is
            // a real desktop/application frame rather than an empty black one.
            // Only the local clone may be disposed here. When the caller's
            // image is already RGBA, the alias stays alive.
            int[] samples = { 1, 4, 7 };
            Image<Rgba32>? ownedClone = null;
            try
            {
                Image<Rgba32> rgba = image as Image<Rgba32>
                    ?? (ownedClone = image.CloneAs<Rgba32>());
                int bright = 0;
                foreach (int xStep in samples)
                {
                    foreach (int yStep in samples)
                    {
                        int x = Math.Clamp(rgba.Width * xStep / 8, 0, rgba.Width - 1);
                        int y = Math.Clamp(rgba.Height * yStep / 8, 0, rgba.Height - 1);
                        Rgba32 pixel = rgba[x, y];
                        if (pixel.R > 24 || pixel.G > 24 || pixel.B > 24)
                        {
                            bright++;
                        }
                    }
                }

                return bright == 0;
            }
            finally
            {
                ownedClone?.Dispose();
            }
        }
        catch
        {
            // If sampling fails, don't take the risk of showing a presumed
            // black overlay, but we cannot prove black either. Prefer aborting
            // the selector to a blank screen.
            return true;
        }
    }
}
