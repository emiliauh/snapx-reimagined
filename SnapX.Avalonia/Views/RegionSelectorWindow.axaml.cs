using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
using Image = SixLabors.ImageSharp.Image;
using Point = Avalonia.Point;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using WindowState = Avalonia.Controls.WindowState;

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
    private readonly Lock _preparationLock = new();
    private Task<bool>? _preparationTask;
    private bool IsSilentMode { get; set; } = false;

    private bool TakeScreenshot { get; set; } = true;

    [ModuleInitializer]
    internal static void RegisterCoreRegionSelector()
    {
        RegionCaptureTasks.SetRegionSelector(SelectRegionForCoreAsync);
    }

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

        // The Avalonia selector is a full-screen Window.  On native Wayland it
        // maps an EGL WSI surface, hides the main window, and later maps that
        // window again.  That is precisely the surface lifecycle we must not
        // enter on the affected NVIDIA/Hyprland renderer.  slurp is the native
        // Wayland selector for this application; if it cannot complete, report
        // a cancelled selection rather than substituting an Avalonia window.
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

        DebugHelper.WriteLine(
            $"Native window-or-region selector phase=launch outputs={monitors.Count} windows={windows.Count}.");
        using var pickerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = monitors
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

        try { await SetupWindowBoundsAsync(); }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Region selector could not determine its display bounds");
            await CancelSelection();
            return;
        }
        IsVisible = true;
        Activate();
        Focus();
    }
    public static async Task<Image?> SelectRegionAsync()
    {
        if (IsNativeWayland)
        {
            return (await SelectRegionForCoreAsync(new RegionCaptureRequest
            {
                CaptureImage = true
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
                CaptureImage = false
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

    private async Task SetupWindowBoundsAsync()
    {

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            PixelRect bounds;


            var cursorPos = Methods.GetCursorPosition();
            var screen = Screens.ScreenFromPoint(new PixelPoint(cursorPos.X, cursorPos.Y));
            if (screen != null)
            {
                bounds = screen.Bounds;
            }
            else
            {
                bounds = await Task.Run(() =>
                        {
                            try
                            {
                                var SnapXScreen = Methods.GetScreen(cursorPos);
                                if (SnapXScreen is null) return Task.FromResult(new PixelRect());
                                var (x, y, width, height) = SnapXScreen.Bounds;
                                return Task.FromResult(new PixelRect(x, y, width, height));
                            }
                            catch (Exception Exception)
                            {
                                return Task.FromException<PixelRect>(Exception);
                            }
                        });
            }

            Position = new PixelPoint(bounds.X, bounds.Y);
            _screenBounds = bounds;
            Width = bounds.Width;
            Height = bounds.Height;

            _canvas.Width = bounds.Width;
            _canvas.Height = bounds.Height;
            _imageBounds = new Rect(0, 0, bounds.Width, bounds.Height);
            WindowState = OperatingSystem.IsMacOS() ? WindowState.Maximized : WindowState.Normal;

            if (_canvas.Parent is Viewbox viewBox)
            {
                viewBox.Width = bounds.Width;
                viewBox.Height = bounds.Height;
            }

            DebugHelper.WriteLine($"Selector Ready: {bounds.Width}x{bounds.Height} at {bounds.X},{bounds.Y}");
        });
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
                _selectionRect.Margin = new Thickness(x, y, 0, 0);
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

        _selectionRect.Width = 0;
        _selectionRect.Height = 0;
        _selectionRect.Margin = new Thickness(_startPoint.X, _startPoint.Y, 0, 0);

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

        if (App.MyMainWindow != null)
        {
            App.MyMainWindow.Show(); // in case it was hidden
            dialog.ShowAsync(App.MyMainWindow);
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
            _selectionRect.Margin.Left,
            _selectionRect.Margin.Top,
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
        try
        {
            await Task.Run(() =>
            {
                if (_image is null)
                {
                    throw new InvalidOperationException("The selector screenshot is unavailable.");
                }

                double scaleX = _image.Width / _imageBounds.Width;
                double scaleY = _image.Height / _imageBounds.Height;
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
                _resultImg.TrySetResult(_image);
                if (IsSilentMode) return;
                DebugHelper.WriteLine("Running image task");
                UploadManager.RunImageTask(_image, TaskSettings.GetDefaultTaskSettings());
            });
        }
        catch (Exception ex)
        {
            _resultImg.TrySetException(ex);
            ShowErrorDialog(ex);
        }
        App.MyMainWindow?.Show();
        Close();
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
        App.MyMainWindow?.Show();
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
        _selectionRect.Margin = new Thickness(x, y, 0, 0);
        _selectionRect.IsVisible = true;

        string title = string.IsNullOrWhiteSpace(hovered.Title) ? hovered.ProcessName : hovered.Title;
        _infoBox.Text = $"{title} ({hovered.Rectangle.Width:0}x{hovered.Rectangle.Height:0})";
        _infoBox.Margin = new Thickness(x, Math.Max(0, y - 30), 0, 0);
        _infoBox.IsVisible = true;
    }

    private void SelectHoveredWindow(WindowInfo window)
    {
        double x = window.Rectangle.X - _screenBounds.X;
        double y = window.Rectangle.Y - _screenBounds.Y;
        _selectionRect.Width = window.Rectangle.Width;
        _selectionRect.Height = window.Rectangle.Height;
        _selectionRect.Margin = new Thickness(x, y, 0, 0);
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

    private async void OnPointerMoved(object? Sender, PointerEventArgs E)
    {
        if (!_isSelecting)
        {
            if (_captureOptions.WindowPickerMode ||
                _captureOptions.WindowOrRegionPickerMode)
            {
                UpdateWindowHover(E.GetPosition(_canvas));
            }
            return;
        }
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
        _selectionRect.Margin = new Thickness(x, y, 0, 0);
        var infoText = $"X: {_screenBounds.X + x:0}, Y: {_screenBounds.Y + y:0}, Width: {width:0}, Height: {height:0}";
        if (_infoBox.Text != infoText)
            _infoBox.Text = infoText;
        _infoBox.Margin = new Thickness(x, y - 30, 0, 0);
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
            case Key.Escape:
                _ = CancelSelection();
                break;
        }
    }
    private readonly Dictionary<Window, WindowBase?> _ownershipMap = new();

    private async Task<bool> PrepareAndShowAsync(CancellationToken cancellationToken = default)
    {
        // Every supported native-Wayland caller is routed through slurp above.
        // Keep this guard here as well so a future direct construction cannot
        // silently reintroduce this full-screen Avalonia WSI surface.
        if (IsNativeWayland)
        {
            DebugHelper.WriteLine("RegionSelectorWindow is disabled on native Wayland; use slurp instead.");
            return false;
        }

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
            Show();
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

        HideSnapXWindows();

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
            await Task.Delay(100);
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
            var backgroundBitmap = App.SnapX.ConvertImageSharpImgToAvalonia(_image);
            DebugHelper.WriteLine(
                $"Selector background: {backgroundBitmap.PixelSize} from raw image bounds {_image.Bounds}");
            Background = new ImageBrush
            {
                Source = backgroundBitmap,
                Stretch = Stretch.UniformToFill,
            };
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
        foreach (var win in App.MyMainWindow?.OwnedWindows.Where(w => w != this && w.IsVisible) ?? [])
        {
            _ownershipMap[win] = win.Owner;
            win.Hide();
            windowsHiddenByUs.Add(win);
        }

        if (App.MyMainWindow is { IsVisible: true } mainWindow)
        {
            _ownershipMap[mainWindow] = mainWindow.Owner;
            mainWindow.Hide();
            windowsHiddenByUs.Add(mainWindow);
        }
    }

    private void RestoreHiddenWindows()
    {
        var sortedWindows = TopoSortWindows(windowsHiddenByUs);
        foreach (var win in sortedWindows)
        {
            if (_ownershipMap.TryGetValue(win, out var owner) && owner?.IsVisible == true)
                win.Show(owner as Window);
            else
                win.Show();
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
