// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SnapX.Core.Job;
using SnapX.Core.ScreenCapture;
using SnapX.Core.ScreenCapture.ScreenRecording;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Extensions;
using SnapX.Core.Utils.Native;

namespace SnapX.Core.Media;

public static class ScreenRecordManager
{
    public enum RecordingManagerState
    {
        Idle,
        Selecting,
        Starting,
        Recording,
        Pausing,
        Paused,
        Stopping,
        Aborting,
        Encoding
    }

    private static readonly object StateLock = new();
    private static readonly ManualResetEventSlim ResumeGate = new(initialState: true);
    private static ScreenRecorder? screenRecorder;
    private static FFmpegCLIManager? activeFfmpeg;
    private static RecordingManagerState state;
    private static ScreenRecordOutput activeOutputType;
    private static bool stopRequested;
    private static bool abortRequested;
    private static bool pauseRequested;
    private static long sessionId;
    private static CancellationTokenSource? sessionCancellation;
    private static Rectangle currentCaptureRectangle;

    public static bool IsRecording
    {
        get
        {
            lock (StateLock)
            {
                return state != RecordingManagerState.Idle;
            }
        }
    }

    public static bool IsPaused
    {
        get
        {
            lock (StateLock)
            {
                return state is RecordingManagerState.Paused or RecordingManagerState.Pausing;
            }
        }
    }

    public static Exception? LastError { get; private set; }
    public static string? LastOutputPath { get; private set; }
    public static Rectangle CurrentCaptureRectangle
    {
        get
        {
            lock (StateLock)
            {
                return currentCaptureRectangle;
            }
        }
    }

    public static event Action<Exception>? RecordingFailed;
    public static event Action<string>? RecordingCompleted;
    public static event Action<RecordingManagerState>? StateChanged;

    public static RecordingManagerState CurrentState
    {
        get
        {
            lock (StateLock)
            {
                return state;
            }
        }
    }

    private static void SetState(RecordingManagerState newState)
    {
        RecordingManagerState oldState;
        lock (StateLock)
        {
            oldState = state;
            if (oldState == newState)
            {
                return;
            }
            state = newState;
        }
        StateChanged?.Invoke(newState);
    }

    public static void PauseScreenRecording()
    {
        ScreenRecorder? recorder;
        bool resumeGif = false;

        lock (StateLock)
        {
            recorder = screenRecorder;
            if (state == RecordingManagerState.Recording)
            {
                if (activeOutputType == ScreenRecordOutput.GIF)
                {
                    SetState(RecordingManagerState.Paused);
                }
                else
                {
                    pauseRequested = true;
                    ResumeGate.Reset();
                    SetState(RecordingManagerState.Pausing);
                }
            }
            else if (state == RecordingManagerState.Paused)
            {
                if (activeOutputType == ScreenRecordOutput.GIF)
                {
                    SetState(RecordingManagerState.Recording);
                    resumeGif = true;
                }
                else
                {
                    pauseRequested = false;
                    SetState(RecordingManagerState.Starting);
                    ResumeGate.Set();
                    return;
                }
            }
            else
            {
                return;
            }
        }

        if (activeOutputType == ScreenRecordOutput.GIF)
        {
            if (recorder?.PauseRecording() != true)
            {
                lock (StateLock)
                {
                    SetState(resumeGif ? RecordingManagerState.Paused : RecordingManagerState.Recording);
                }
            }
        }
        else
        {
            recorder?.StopRecording();
        }
    }

    public static void AbortRecording()
    {
        ScreenRecorder? recorder;
        FFmpegCLIManager? encoder;
        lock (StateLock)
        {
            if (state == RecordingManagerState.Idle)
            {
                return;
            }

            abortRequested = true;
            stopRequested = true;
            pauseRequested = false;
            SetState(RecordingManagerState.Aborting);
            recorder = screenRecorder;
            encoder = activeFfmpeg;
            ResumeGate.Set();
            sessionCancellation?.Cancel();
        }

        recorder?.AbortRecording();
        encoder?.Close();
    }

    public static void StartStopRecording(
        ScreenRecordOutput outputType,
        ScreenRecordStartMethod startMethod,
        TaskSettings taskSettings)
    {
        ArgumentNullException.ThrowIfNull(taskSettings);

        lock (StateLock)
        {
            if (state != RecordingManagerState.Idle)
            {
                // Preserve the established toggle behavior.
                _ = Task.Run(StopRecording);
                return;
            }

            try
            {
                ValidateStart(outputType, startMethod, taskSettings);
            }
            catch (Exception ex)
            {
                // A validation failure here (for example wf-recorder missing,
                // or no region selector registered) throws synchronously.
                // RunRecordingSessionAsync does not run for this path, because
                // the failure happens before that task starts. Its try/catch
                // and ErrorMessageEvent therefore do not handle the exception.
                // Report it here so the user sees the error immediately.
                LastError = ex;
                DebugHelper.WriteException(ex);
                SnapXL.EventAggregator.Publish(new ErrorMessageEvent(ex, "Screen recording failed", true));
                return;
            }

            LastError = null;
            LastOutputPath = null;
            stopRequested = false;
            abortRequested = false;
            pauseRequested = false;
            activeOutputType = outputType;
            SetState(startMethod == ScreenRecordStartMethod.Region
                ? RecordingManagerState.Selecting
                : RecordingManagerState.Starting);
            ResumeGate.Set();
            sessionCancellation = new CancellationTokenSource();
            sessionId++;
            long currentSession = sessionId;
            _ = Task.Run(() => RunRecordingSessionAsync(
                currentSession,
                outputType,
                startMethod,
                taskSettings));
        }
    }

    public static void StopRecording()
    {
        ScreenRecorder? recorder;
        lock (StateLock)
        {
            if (state == RecordingManagerState.Idle)
            {
                return;
            }

            stopRequested = true;
            pauseRequested = false;
            if (!abortRequested)
            {
                SetState(RecordingManagerState.Stopping);
            }

            recorder = screenRecorder;
            ResumeGate.Set();
            sessionCancellation?.Cancel();
        }

        recorder?.StopRecording();
    }

    private static async Task RunRecordingSessionAsync(
        long currentSession,
        ScreenRecordOutput outputType,
        ScreenRecordStartMethod startMethod,
        TaskSettings taskSettings)
    {
        string? finalPath = null;
        string? gifCachePath = null;
        var temporaryPaths = new List<string>();

        try
        {
            (Rectangle captureRectangle, TaskMetadata metadata) = await ResolveCaptureAreaAsync(
                startMethod,
                taskSettings,
                GetSessionCancellationToken(currentSession)).ConfigureAwait(false);

            // A stop can arrive while an interactive selector or compositor
            // geometry query is still completing. Do not continue into output
            // allocation and recorder startup after that request: there is no
            // segment to finalize, so this is a normal cancellation rather
            // than a failed recording.
            ThrowIfStopRequested(currentSession);

            if (captureRectangle.IsEmpty)
            {
                // An empty region from the interactive selector means cancellation.
                return;
            }

            if (taskSettings.CaptureSettings.FFmpegOptions.IsEvenSizeRequired
                && outputType == ScreenRecordOutput.FFmpeg)
            {
                captureRectangle = CaptureHelpers.EvenRectangleSize(captureRectangle);
            }

            if (captureRectangle.Width <= 0 || captureRectangle.Height <= 0)
            {
                throw new InvalidOperationException("The recording region is empty after applying encoder constraints.");
            }

            SnapXL.Settings.ScreenRecordRegion = captureRectangle;
            lock (StateLock)
            {
                currentCaptureRectangle = captureRectangle;
            }

            int fps = outputType == ScreenRecordOutput.GIF
                ? taskSettings.CaptureSettings.GIFFPS
                : taskSettings.CaptureSettings.ScreenRecordFPS;
            fps = Math.Clamp(fps, 1, 240);

            string extension = outputType == ScreenRecordOutput.GIF
                ? "gif"
                : taskSettings.CaptureSettings.FFmpegOptions.Extension;
            string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(taskSettings, metadata);
            string fileName = TaskHelpers.GetFileName(taskSettings, extension, metadata);
            finalPath = TaskHelpers.HandleExistsFile(screenshotsFolder, fileName, taskSettings);
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                throw new OperationCanceledException("The recording output path was not accepted.");
            }

            if (!WaitForStartDelay(currentSession, taskSettings.CaptureSettings.ScreenRecordStartDelay))
            {
                return;
            }

            float configuredDuration = taskSettings.CaptureSettings.ScreenRecordFixedDuration
                ? Math.Max(0, taskSettings.CaptureSettings.ScreenRecordDuration)
                : 0;

            if (outputType == ScreenRecordOutput.GIF && !IsWaylandSession())
            {
                gifCachePath = finalPath + ".frames";
                FileHelpers.DeleteFile(gifCachePath);
                temporaryPaths.Add(gifCachePath);

                var options = CreateRecordingOptions(
                    taskSettings,
                    captureRectangle,
                    fps,
                    configuredDuration,
                    gifCachePath,
                    isLossless: false);
                Screenshot screenshot = CreateRecordingScreenshot(taskSettings);
                using var recorder = new ScreenRecorder(
                    ScreenRecordOutput.GIF,
                    options,
                    screenshot,
                    captureRectangle);
                if (!SetRecorder(currentSession, recorder, RecordingManagerState.Recording))
                {
                    ThrowIfStopRequested(currentSession);
                    return;
                }
                recorder.StartRecording();

                if (IsAbortRequested(currentSession))
                {
                    return;
                }

                recorder.SaveAsGIF(finalPath, taskSettings.ImageSettings.ImageGIFQuality);
                DetachRecorder(currentSession, recorder);
            }
            else
            {
                finalPath = await RecordFFmpegAsync(
                    currentSession,
                    captureRectangle,
                    metadata,
                    taskSettings,
                    fps,
                    configuredDuration,
                    finalPath,
                    temporaryPaths,
                    encodeAsGif: outputType == ScreenRecordOutput.GIF).ConfigureAwait(false);
            }

            if (IsAbortRequested(currentSession))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
            {
                throw new IOException("Recording finished without producing an output file.");
            }

            LastOutputPath = finalPath;
            TaskHelpers.PlayNotificationSoundAsync(NotificationSound.ActionCompleted, taskSettings);
            WorkerTask task = WorkerTask.CreateFileJobTask(finalPath, metadata, taskSettings);
            TaskManager.Start(task);
            RecordingCompleted?.Invoke(finalPath);

            foreach (string temporaryPath in temporaryPaths)
            {
                if (!temporaryPath.Equals(finalPath, StringComparison.OrdinalIgnoreCase))
                {
                    FileHelpers.DeleteFile(temporaryPath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Output-path or region-selection cancellation is an expected outcome.
        }
        catch (Exception ex)
        {
            LastError = ex;
            DebugHelper.WriteException(ex);
            SnapXL.EventAggregator.Publish(new ErrorMessageEvent(ex, "Screen recording failed", true));
            RecordingFailed?.Invoke(ex);
        }
        finally
        {
            bool aborted = IsAbortRequested(currentSession);
            ScreenRecorder? recorder = ClearRecorder(currentSession);
            recorder?.Dispose();
            ClearActiveFfmpeg(currentSession)?.Dispose();

            if (aborted)
            {
                if (!string.IsNullOrWhiteSpace(finalPath))
                {
                    FileHelpers.DeleteFile(finalPath);
                }

                foreach (string temporaryPath in temporaryPaths)
                {
                    FileHelpers.DeleteFile(temporaryPath);
                }
            }

            lock (StateLock)
            {
                if (sessionId == currentSession)
                {
                    SetState(RecordingManagerState.Idle);
                    stopRequested = false;
                    abortRequested = false;
                    pauseRequested = false;
                    currentCaptureRectangle = Rectangle.Empty;
                    ResumeGate.Set();
                    sessionCancellation?.Dispose();
                    sessionCancellation = null;
                }
            }
        }
    }

    private static async Task<string> RecordFFmpegAsync(
        long currentSession,
        Rectangle captureRectangle,
        TaskMetadata metadata,
        TaskSettings taskSettings,
        int fps,
        float configuredDuration,
        string finalPath,
        List<string> temporaryPaths,
        bool encodeAsGif = false)
    {
        bool hasCustomCommands = taskSettings.CaptureSettings.FFmpegOptions.UseCustomCommands
            && !string.IsNullOrWhiteSpace(taskSettings.CaptureSettings.FFmpegOptions.CustomCommands);
        // The generated Wayland path records a lossless intermediate before
        // palette conversion. A custom command owns its own capture output, so
        // leave it intact and convert that completed segment to GIF instead.
        bool lossless = !hasCustomCommands && (encodeAsGif
            || taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding
            || taskSettings.CaptureSettings.FFmpegOptions.IsAnimatedImage);
        string recordingBasePath = (lossless || encodeAsGif)
            ? FileHelpers.AppendTextToFileName(Path.ChangeExtension(finalPath, "mp4"), "-lossless")
            : finalPath;
        var segments = new List<string>();
        double remainingDuration = configuredDuration;
        int segmentIndex = 0;

        while (!IsStopRequested(currentSession))
        {
            string requestedSegmentPath = FileHelpers.AppendTextToFileName(
                recordingBasePath,
                $"-part{segmentIndex++:000}");
            string segmentPath = Path.ChangeExtension(
                requestedSegmentPath,
                lossless ? "mp4" : taskSettings.CaptureSettings.FFmpegOptions.Extension);
            FileHelpers.DeleteFile(segmentPath);
            temporaryPaths.Add(segmentPath);

            var options = CreateRecordingOptions(
                taskSettings,
                captureRectangle,
                fps,
                remainingDuration > 0 ? (float)remainingDuration : 0,
                segmentPath,
                lossless);
            Screenshot screenshot = CreateRecordingScreenshot(taskSettings);
            using var recorder = new ScreenRecorder(
                ScreenRecordOutput.FFmpeg,
                options,
                screenshot,
                captureRectangle);
            if (!SetRecorder(currentSession, recorder, RecordingManagerState.Recording))
            {
                ThrowIfStopRequested(currentSession);
                return finalPath;
            }

            Stopwatch activeTimer = Stopwatch.StartNew();
            recorder.StartRecording();
            activeTimer.Stop();
            DetachRecorder(currentSession, recorder);

            if (File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 0)
            {
                segments.Add(segmentPath);
            }
            else if (!IsAbortRequested(currentSession))
            {
                throw new IOException("FFmpeg exited without producing a recording segment.");
            }

            if (remainingDuration > 0)
            {
                remainingDuration = Math.Max(0, remainingDuration - activeTimer.Elapsed.TotalSeconds);
                if (remainingDuration <= 0.01)
                {
                    break;
                }
            }

            bool segmentWasPaused = WasPauseRequested(currentSession);
            if (segmentWasPaused && !WaitWhilePaused(currentSession))
            {
                break;
            }

            if (!segmentWasPaused)
            {
                break;
            }
        }

        if (IsAbortRequested(currentSession))
        {
            return finalPath;
        }

        if (segments.Count == 0)
        {
            throw new IOException("No FFmpeg recording segments were produced.");
        }

        SetManagerState(currentSession, RecordingManagerState.Encoding);
        string combinedPath = segments.Count == 1
            ? segments[0]
            : FileHelpers.AppendTextToFileName(recordingBasePath, "-combined");

        if (segments.Count > 1)
        {
            FileHelpers.DeleteFile(combinedPath);
            temporaryPaths.Add(combinedPath);
            using var ffmpeg = new FFmpegCLIManager(taskSettings.CaptureSettings.FFmpegOptions.FFmpegPath)
            {
                ShowError = true
            };
            if (!SetActiveFfmpeg(currentSession, ffmpeg))
            {
                return finalPath;
            }
            try
            {
                ffmpeg.ConcatenateVideos(segments.ToArray(), combinedPath);
            }
            finally
            {
                DetachActiveFfmpeg(currentSession, ffmpeg);
            }
            if (IsAbortRequested(currentSession))
            {
                return finalPath;
            }
            if (!File.Exists(combinedPath) || new FileInfo(combinedPath).Length == 0)
            {
                throw new IOException("FFmpeg failed to concatenate paused recording segments.");
            }
        }

        if (lossless || encodeAsGif)
        {
            var conversion = CreateRecordingOptions(
                taskSettings,
                captureRectangle,
                fps,
                0,
                finalPath,
                isLossless: false);
            conversion.IsRecording = false;
            conversion.InputPath = combinedPath;

            using var ffmpeg = new FFmpegCLIManager(taskSettings.CaptureSettings.FFmpegOptions.FFmpegPath)
            {
                ShowError = true,
                TrackEncodeProgress = true
            };
            if (!SetActiveFfmpeg(currentSession, ffmpeg))
            {
                return finalPath;
            }

            bool encodingSucceeded;
            try
            {
                encodingSucceeded = encodeAsGif
                    ? ffmpeg.Run(GetGifEncodingArguments(combinedPath, finalPath, taskSettings.CaptureSettings.FFmpegOptions))
                    : ffmpeg.Run(conversion.GetFFmpegCommands());
            }
            finally
            {
                DetachActiveFfmpeg(currentSession, ffmpeg);
            }

            if (!encodingSucceeded && !IsAbortRequested(currentSession))
            {
                throw new IOException("FFmpeg failed during the final encoding pass.");
            }
        }
        else if (!combinedPath.Equals(finalPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(combinedPath, finalPath, overwrite: false);
        }

        return finalPath;
    }

    private static string GetGifEncodingArguments(string inputPath, string outputPath, FFmpegOptions options)
    {
        StringBuilder arguments = new();
        arguments.Append($"-i \"{inputPath}\" ");
        arguments.Append($"-filter_complex \"[0:v]palettegen=stats_mode={options.GIFStatsMode}[palette];");
        arguments.Append($"[0:v][palette]paletteuse=dither={options.GIFDither}");

        if (options.GIFDither == FFmpegPaletteUseDither.bayer)
        {
            arguments.Append($":bayer_scale={options.GIFBayerScale}");
        }

        if (options.GIFStatsMode == FFmpegPaletteGenStatsMode.single)
        {
            arguments.Append(":new=1");
        }

        arguments.Append("\" -loop 0 -y ");
        arguments.Append($"\"{outputPath}\"");
        return arguments.ToString();
    }

    private static ScreenRecordingOptions CreateRecordingOptions(
        TaskSettings taskSettings,
        Rectangle captureRectangle,
        int fps,
        float duration,
        string outputPath,
        bool isLossless)
    {
        return new ScreenRecordingOptions
        {
            IsRecording = true,
            IsLossless = isLossless,
            FFmpeg = taskSettings.CaptureSettings.FFmpegOptions,
            FPS = fps,
            Duration = duration,
            OutputPath = outputPath,
            CaptureArea = captureRectangle,
            DrawCursor = taskSettings.CaptureSettings.ScreenRecordShowCursor
        };
    }

    private static Screenshot CreateRecordingScreenshot(TaskSettings taskSettings)
    {
        Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
        screenshot.CaptureCursor = taskSettings.CaptureSettings.ScreenRecordShowCursor;
        if (IsWaylandSession())
        {
            // X11 desktop bounds are unavailable in a native Wayland session.
            screenshot.RemoveOutsideScreenArea = false;
        }

        return screenshot;
    }

    private static async Task<(Rectangle Rectangle, TaskMetadata Metadata)> ResolveCaptureAreaAsync(
        ScreenRecordStartMethod startMethod,
        TaskSettings taskSettings,
        CancellationToken cancellationToken)
    {
        var metadata = new TaskMetadata();
        Rectangle rectangle;

        switch (startMethod)
        {
            case ScreenRecordStartMethod.Region:
                RegionCaptureOptions regionOptions = RegionCaptureTasks.GetRegionCaptureOptions(
                    taskSettings.CaptureSettings.SurfaceOptions);
                regionOptions.UpdateRegionHistory = false;
                RegionCaptureSelection? selection = await RegionCaptureTasks.SelectRegionAsync(
                    regionOptions,
                    taskSettings.CaptureSettings.ScreenRecordTransparentRegion
                        ? RegionCaptureType.Transparent
                        : RegionCaptureType.Default,
                    captureImage: false,
                    cancellationToken).ConfigureAwait(false);
                if (selection is null)
                {
                    return (Rectangle.Empty, metadata);
                }

                rectangle = selection.Rectangle;
                if (selection.WindowInfo is not null)
                {
                    metadata.UpdateInfo(selection.WindowInfo);
                }
                break;

            case ScreenRecordStartMethod.ActiveWindow:
                if (IsWaylandSession())
                {
                    rectangle = await ResolveActiveWindowOnWaylandAsync().ConfigureAwait(false);
                    break;
                }
                if (taskSettings.CaptureSettings.CaptureClientArea)
                {
                    throw new PlatformNotSupportedException(
                        "Client-area-only recording is not supported by the current native window API.");
                }

                WindowInfo activeWindow = Methods.GetForegroundWindow()
                    ?? throw new InvalidOperationException("No active window could be identified for recording.");
                rectangle = activeWindow.Rectangle;
                metadata.UpdateInfo(activeWindow);
                break;

            case ScreenRecordStartMethod.ActiveMonitor:
                rectangle = await ResolveMonitorAsync(taskSettings).ConfigureAwait(false);
                break;

            case ScreenRecordStartMethod.Fullscreen:
                rectangle = await ResolveFullscreenAsync(taskSettings).ConfigureAwait(false);
                break;

            case ScreenRecordStartMethod.CustomRegion:
                rectangle = taskSettings.CaptureSettings.CaptureCustomRegion;
                break;

            case ScreenRecordStartMethod.LastRegion:
                rectangle = SnapXL.Settings.ScreenRecordRegion;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(startMethod), startMethod, null);
        }

        if (!IsWaylandSession())
        {
            rectangle = Rectangle.Intersect(rectangle, CaptureHelpers.GetScreenBounds());
        }

        return (rectangle, metadata);
    }

    /// <summary>
    /// Resolves the on-screen geometry to record. In a Wayland session this
    /// reads the compositor's logical output layout (Hyprland) so rotated and
    /// fractionally-scaled monitors are addressed correctly. On X11 and other
    /// platforms the existing native screen helpers are used.
    /// </summary>
    private static async Task<Rectangle> ResolveFullscreenAsync(TaskSettings taskSettings)
    {
        if (!IsWaylandSession())
        {
            return CaptureHelpers.GetScreenBounds();
        }

        List<WaylandMonitorGeometry> monitors = await ReadHyprlandMonitorsAsync().ConfigureAwait(false);
        if (monitors.Count == 1)
        {
            WaylandMonitorGeometry only = monitors[0];
            return new Rectangle(only.X, only.Y, only.Width, only.Height);
        }

        if (monitors.Count > 1)
        {
            // wf-recorder (the Wayland recording backend) captures exactly one
            // wlroots output per invocation: its "-g" geometry must resolve to
            // a single compositor output, and a rectangle spanning multiple
            // outputs is rejected outright ("Failed to detect output based on
            // geometry"). Unlike X11, Wayland compositors do not expose a
            // single stitched virtual-desktop buffer that a client can read
            // in one screencopy/PipeWire stream, so a multi-monitor union
            // rectangle cannot succeed here. Record the focused output
            // instead of failing. This matches how most Wayland recording
            // tools record one screen when more than one monitor is
            // connected.
            WaylandMonitorGeometry target = monitors.FirstOrDefault(m => m.Focused) ?? monitors[0];
            DebugHelper.WriteLine(
                $"Full-screen recording spans {monitors.Count} Wayland outputs; wf-recorder can only " +
                $"record one output per invocation, so only the focused output ({target.X},{target.Y} " +
                $"{target.Width}x{target.Height}) will be recorded.");
            return new Rectangle(target.X, target.Y, target.Width, target.Height);
        }

        // Fall back to the interactive region selector if the compositor layout
        // cannot be read, so the user still has a way to record the screen.
        return (await ResolveRegionAsync(taskSettings).ConfigureAwait(false)).Rectangle;
    }

    private static async Task<Rectangle> ResolveMonitorAsync(TaskSettings taskSettings)
    {
        if (IsWaylandSession())
        {
            List<WaylandMonitorGeometry> monitors = await ReadHyprlandMonitorsAsync().ConfigureAwait(false);
            if (monitors.Count > 0)
            {
                Point cursor = TryGetHyprlandCursorPosition() ?? GetCursorPosition();
                WaylandMonitorGeometry? underCursor = monitors
                    .FirstOrDefault(m => cursor.X >= m.X && cursor.X < m.X + m.Width
                        && cursor.Y >= m.Y && cursor.Y < m.Y + m.Height)
                    ?? monitors.FirstOrDefault(m => m.Focused)
                    ?? monitors.FirstOrDefault();
                if (underCursor is not null)
                {
                    return new Rectangle(underCursor.X, underCursor.Y, underCursor.Width, underCursor.Height);
                }
            }
        }

        Rectangle bounds = CaptureHelpers.GetActiveScreenBounds();
        if (bounds.IsEmpty)
        {
            bounds = CaptureHelpers.GetPrimaryScreenBounds();
        }
        return bounds;
    }

    /// <summary>
    /// Resolves the currently focused window's geometry on a Wayland session
    /// using Hyprland's compositor layout, so active-window recording works
    /// without an X11 overlay.
    /// </summary>
    private static async Task<Rectangle> ResolveActiveWindowOnWaylandAsync()
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "hyprctl",
                    ArgumentList = { "-j", "activewindow" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string json = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Hyprland could not report the active window.");
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("at", out JsonElement at) ||
                !root.TryGetProperty("size", out JsonElement size) ||
                at.GetArrayLength() < 2 || size.GetArrayLength() < 2)
            {
                throw new InvalidOperationException("The active window geometry was not reported by Hyprland.");
            }

            int x = at[0].GetInt32();
            int y = at[1].GetInt32();
            int width = size[0].GetInt32();
            int height = size[1].GetInt32();
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The active window has an empty recording area.");
            }
            return new Rectangle(x, y, width, height);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to resolve active window geometry on Wayland.");
            throw new PlatformNotSupportedException(
                "Active-window recording is unavailable because the compositor did not report the window geometry.",
                ex);
        }
    }

    private static async Task<(Rectangle Rectangle, TaskMetadata Metadata)> ResolveRegionAsync(TaskSettings taskSettings)
    {
        RegionCaptureOptions regionOptions = RegionCaptureTasks.GetRegionCaptureOptions(
            taskSettings.CaptureSettings.SurfaceOptions);
        regionOptions.UpdateRegionHistory = false;
        RegionCaptureSelection? selection = await RegionCaptureTasks.SelectRegionAsync(
            regionOptions,
            taskSettings.CaptureSettings.ScreenRecordTransparentRegion
                ? RegionCaptureType.Transparent
                : RegionCaptureType.Default,
            captureImage: false).ConfigureAwait(false);
        if (selection is null)
        {
            return (Rectangle.Empty, new TaskMetadata());
        }

        Rectangle rectangle = selection.Rectangle;
        var metadata = new TaskMetadata();
        if (selection.WindowInfo is not null)
        {
            metadata.UpdateInfo(selection.WindowInfo);
        }
        return (rectangle, metadata);
    }

    private static Point GetCursorPosition()
    {
        try
        {
            return CaptureHelpers.GetCursorPosition();
        }
        catch
        {
            return Point.Empty;
        }
    }

    private static Point? TryGetHyprlandCursorPosition()
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "hyprctl",
                    ArgumentList = { "cursorpos", "-j" },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string json = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return null;

            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("x", out JsonElement x) ||
                !document.RootElement.TryGetProperty("y", out JsonElement y))
            {
                return null;
            }
            return new Point((int)Math.Round(x.GetDouble()), (int)Math.Round(y.GetDouble()));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<WaylandMonitorGeometry>> ReadHyprlandMonitorsAsync()
    {
        static bool IsQuarterTurn(int transform) => transform is 1 or 3 or 5 or 7;

        var monitors = new List<WaylandMonitorGeometry>();
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "hyprctl",
                    ArgumentList = { "monitors", "-j" },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string json = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0) return monitors;

            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonElement monitor in document.RootElement.EnumerateArray())
            {
                double x = monitor.GetProperty("x").GetDouble();
                double y = monitor.GetProperty("y").GetDouble();
                double scale = monitor.GetProperty("scale").GetDouble();
                int transform = monitor.GetProperty("transform").GetInt32();
                double physicalWidth = monitor.GetProperty("width").GetDouble();
                double physicalHeight = monitor.GetProperty("height").GetDouble();
                bool rotated = IsQuarterTurn(transform);
                double logicalWidth = (rotated ? physicalHeight : physicalWidth) / scale;
                double logicalHeight = (rotated ? physicalWidth : physicalHeight) / scale;
                bool focused = monitor.TryGetProperty("focused", out JsonElement focusedEl)
                    && focusedEl.GetBoolean();
                monitors.Add(new WaylandMonitorGeometry(
                    (int)Math.Round(x),
                    (int)Math.Round(y),
                    (int)Math.Round(logicalWidth),
                    (int)Math.Round(logicalHeight),
                    focused));
            }
        }
        catch
        {
            // Let the caller fall back to another geometry source.
        }
        return monitors;
    }

    private sealed record WaylandMonitorGeometry(int X, int Y, int Width, int Height, bool Focused);

    private static void ValidateStart(
        ScreenRecordOutput outputType,
        ScreenRecordStartMethod startMethod,
        TaskSettings taskSettings)
    {
        if (startMethod == ScreenRecordStartMethod.Region && !RegionCaptureTasks.IsRegionSelectorAvailable)
        {
            throw new InvalidOperationException(
                "Interactive region recording is unavailable because no region selector is registered.");
        }

        int fps = outputType == ScreenRecordOutput.GIF
            ? taskSettings.CaptureSettings.GIFFPS
            : taskSettings.CaptureSettings.ScreenRecordFPS;
        if (fps is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(taskSettings), fps, "Recording FPS must be between 1 and 240.");
        }

        float startDelay = taskSettings.CaptureSettings.ScreenRecordStartDelay;
        if (!float.IsFinite(startDelay) || startDelay < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(taskSettings), startDelay, "Recording start delay must be finite and non-negative.");
        }

        if (taskSettings.CaptureSettings.ScreenRecordFixedDuration)
        {
            float duration = taskSettings.CaptureSettings.ScreenRecordDuration;
            if (!float.IsFinite(duration) || duration <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(taskSettings), duration, "Fixed recording duration must be finite and positive.");
            }
        }

        FFmpegOptions ffmpeg = taskSettings.CaptureSettings.FFmpegOptions;
        bool hasCustomCommands = ffmpeg.UseCustomCommands && !string.IsNullOrWhiteSpace(ffmpeg.CustomCommands);
        bool hasFfmpegExecutableOverride = ffmpeg.OverrideCLIPath && !string.IsNullOrWhiteSpace(ffmpeg.CLIPath);

        if (OperatingSystem.IsMacOS() && outputType != ScreenRecordOutput.GIF && !hasCustomCommands)
        {
            throw new PlatformNotSupportedException(
                "Screen recording on macOS requires custom FFmpeg commands that use avfoundation. Generated desktop-capture commands are not supported.");
        }

        if (outputType == ScreenRecordOutput.GIF)
        {
            if (IsWaylandSession() && !hasCustomCommands && !ScreenRecorder.IsWfRecorderAvailable())
            {
                throw new PlatformNotSupportedException(
                    "GIF recording is unavailable on Wayland: install wf-recorder, or use a KDE/X11 session.");
            }
            return;
        }

        if (OperatingSystem.IsLinux() && !IsWaylandSession() && !hasCustomCommands)
        {
            if (ffmpeg.IsAudioSourceSelected)
            {
                throw new PlatformNotSupportedException(
                    "Generated Linux recording commands currently support X11 video only. Use custom FFmpeg commands for audio capture.");
            }

            string source = ffmpeg.VideoSource;
            if (string.IsNullOrWhiteSpace(source)
                || source.Equals(FFmpegCaptureDevice.GDIGrab.Value, StringComparison.OrdinalIgnoreCase)
                || source.Equals(FFmpegCaptureDevice.DDAGrab.Value, StringComparison.OrdinalIgnoreCase)
                || source.Equals(FFmpegCaptureDevice.ScreenCaptureRecorder.Value, StringComparison.OrdinalIgnoreCase))
            {
                ffmpeg.VideoSource = FFmpegCaptureDevice.X11Grab.Value;
            }
            else if (!source.Equals(FFmpegCaptureDevice.X11Grab.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new PlatformNotSupportedException(
                    $"FFmpeg source '{source}' is not a generated Linux desktop source. Use x11grab or custom commands.");
            }
        }

        if (!hasCustomCommands && !ffmpeg.IsSourceSelected)
        {
            throw new InvalidOperationException("FFmpeg video and audio sources cannot both be empty.");
        }

        if (IsWaylandSession() && !hasCustomCommands && !hasFfmpegExecutableOverride && !ScreenRecorder.IsWfRecorderAvailable())
        {
            throw new PlatformNotSupportedException(
                "FFmpeg desktop recording is unavailable on Wayland: install wf-recorder, " +
                "or supply a custom ffmpeg command that captures via PipeWire/the portal.");
        }

        if (IsWaylandSession() && hasCustomCommands
            && (taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding || ffmpeg.IsAnimatedImage))
        {
            throw new PlatformNotSupportedException(
                "Wayland custom-command recording currently supports direct single-pass output only.");
        }

        string? ffmpegPath = ffmpeg.FFmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new FileNotFoundException("No FFmpeg executable was configured.");
        }

        if (Path.IsPathRooted(ffmpegPath) && !File.Exists(ffmpegPath))
        {
            string? pathFfmpeg = !ffmpeg.OverrideCLIPath ? FindExecutableOnPath("ffmpeg") : null;
            if (pathFfmpeg == null)
            {
                throw new FileNotFoundException("The configured FFmpeg executable was not found.", ffmpegPath);
            }

            ffmpeg.OverrideCLIPath = true;
            ffmpeg.CLIPath = pathFfmpeg;
        }
        else if (!Path.IsPathRooted(ffmpegPath))
        {
            string? pathFfmpeg = FindExecutableOnPath(ffmpegPath);
            if (pathFfmpeg is null)
            {
                throw new FileNotFoundException(
                    $"The FFmpeg executable '{ffmpegPath}' was not found on PATH.",
                    ffmpegPath);
            }

            // ExternalCLIManager intentionally accepts concrete file paths only.
            ffmpeg.OverrideCLIPath = true;
            ffmpeg.CLIPath = pathFfmpeg;
        }

    }

    private static string? FindExecutableOnPath(string executableName)
    {
        if (OperatingSystem.IsWindows() && !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            executableName += ".exe";
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed PATH entries and continue searching.
            }
        }

        return null;
    }

    private static bool WaitForStartDelay(long currentSession, float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0)
        {
            return !IsStopRequested(currentSession);
        }

        Stopwatch timer = Stopwatch.StartNew();
        TimeSpan delay = TimeSpan.FromSeconds(seconds);
        while (timer.Elapsed < delay)
        {
            if (IsStopRequested(currentSession))
            {
                return false;
            }
            Thread.Sleep(Math.Min(50, Math.Max(1, (int)(delay - timer.Elapsed).TotalMilliseconds)));
        }
        return true;
    }

    private static bool WaitWhilePaused(long currentSession)
    {
        lock (StateLock)
        {
            if (sessionId != currentSession || stopRequested || abortRequested)
            {
                return false;
            }
            if (!pauseRequested)
            {
                return true;
            }
            SetState(RecordingManagerState.Paused);
        }

        ResumeGate.Wait();
        return !IsStopRequested(currentSession);
    }

    private static bool SetRecorder(long currentSession, ScreenRecorder recorder, RecordingManagerState newState)
    {
        lock (StateLock)
        {
            if (sessionId != currentSession || stopRequested)
            {
                return false;
            }
            screenRecorder = recorder;
            SetState(newState);
            return true;
        }
    }

    private static void DetachRecorder(long currentSession, ScreenRecorder recorder)
    {
        lock (StateLock)
        {
            if (sessionId == currentSession && ReferenceEquals(screenRecorder, recorder))
            {
                screenRecorder = null;
            }
        }
    }

    private static ScreenRecorder? ClearRecorder(long currentSession)
    {
        lock (StateLock)
        {
            if (sessionId != currentSession)
            {
                return null;
            }
            ScreenRecorder? recorder = screenRecorder;
            screenRecorder = null;
            return recorder;
        }
    }

    private static bool SetActiveFfmpeg(long currentSession, FFmpegCLIManager ffmpeg)
    {
        lock (StateLock)
        {
            if (sessionId != currentSession || abortRequested)
            {
                return false;
            }

            activeFfmpeg = ffmpeg;
            return true;
        }
    }

    private static void DetachActiveFfmpeg(long currentSession, FFmpegCLIManager ffmpeg)
    {
        lock (StateLock)
        {
            if (sessionId == currentSession && ReferenceEquals(activeFfmpeg, ffmpeg))
            {
                activeFfmpeg = null;
            }
        }
    }

    private static FFmpegCLIManager? ClearActiveFfmpeg(long currentSession)
    {
        lock (StateLock)
        {
            if (sessionId != currentSession)
            {
                return null;
            }

            FFmpegCLIManager? ffmpeg = activeFfmpeg;
            activeFfmpeg = null;
            return ffmpeg;
        }
    }

    private static void SetManagerState(long currentSession, RecordingManagerState newState)
    {
        lock (StateLock)
        {
            if (sessionId == currentSession)
            {
                SetState(newState);
            }
        }
    }

    private static bool IsStopRequested(long currentSession)
    {
        lock (StateLock)
        {
            return sessionId != currentSession || stopRequested || abortRequested;
        }
    }

    private static void ThrowIfStopRequested(long currentSession)
    {
        if (IsStopRequested(currentSession))
        {
            throw new OperationCanceledException("Screen recording was stopped before capture began.");
        }
    }

    private static bool IsAbortRequested(long currentSession)
    {
        lock (StateLock)
        {
            return sessionId == currentSession && abortRequested;
        }
    }

    private static bool WasPauseRequested(long currentSession)
    {
        lock (StateLock)
        {
            return sessionId == currentSession && pauseRequested;
        }
    }

    private static CancellationToken GetSessionCancellationToken(long currentSession)
    {
        lock (StateLock)
        {
            return sessionId == currentSession && sessionCancellation is not null
                ? sessionCancellation.Token
                : new CancellationToken(canceled: true);
        }
    }

    private static bool IsWaylandSession() => OperatingSystem.IsLinux()
        && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase));

    private static bool IsKdeWaylandSession() => IsWaylandSession()
        && (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.Contains("KDE", StringComparison.OrdinalIgnoreCase) == true
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KDE_SESSION_VERSION")));
}
