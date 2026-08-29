
// SPDX-License-Identifier: GPL-3.0-or-later


using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Processing;
using SnapX.Core.Job;
using SnapX.Core.Media;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Native;

namespace SnapX.Core.ScreenCapture.ScreenRecording;

public class ScreenRecorder : IDisposable
{
    public bool IsRecording => Volatile.Read(ref recordingState) != 0;
    public bool IsPaused => Volatile.Read(ref paused) != 0;
    public bool IsAborted => Volatile.Read(ref aborted) != 0;
    public bool LastRunSucceeded { get; private set; }

    public int FPS
    {
        get
        {
            return fps;
        }
        set
        {
            if (!IsRecording)
            {
                if (value is < 1 or > 1000)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Recording FPS must be between 1 and 1000.");
                }

                fps = value;
                UpdateInfo();
            }
        }
    }

    public float DurationSeconds
    {
        get
        {
            return durationSeconds;
        }
        set
        {
            if (!IsRecording)
            {
                if (!float.IsFinite(value) || value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Recording duration must be finite and non-negative.");
                }

                durationSeconds = value;
                UpdateInfo();
            }
        }
    }

    public Rectangle CaptureRectangle
    {
        get
        {
            return captureRectangle;
        }
        private set
        {
            if (!IsRecording)
            {
                captureRectangle = value;
            }
        }
    }

    public string? CachePath { get; private set; }

    public ScreenRecordOutput OutputType { get; private set; }

    public ScreenRecordingOptions Options { get; set; }

    public event Action? RecordingStarted;

    public delegate void ProgressEventHandler(int progress);
    public event ProgressEventHandler? EncodingProgressChanged;

    private int fps, delay, frameCount, previousProgress;
    private float durationSeconds;
    private Screenshot screenshot;
    private Rectangle captureRectangle;
    private ImageCache imgCache;
    private FFmpegCLIManager ffmpeg;
    private int stopRequested;
    private int recordingState;
    private int disposed;
    private int paused;
    private int aborted;
    private readonly ManualResetEventSlim pauseGate = new(initialState: true);

    // wf-recorder captures Wayland desktops directly through the compositor's
    // screencopy/PipeWire path. ffmpeg cannot do this on its own without a
    // hand-written portal/PipeWire client, so on Wayland (without a custom
    // ffmpeg command the user supplied themselves) this replaces ffmpeg as
    // the process actually invoked, while everything else about this class
    // (start/stop signaling, output path, disposal) stays the same.
    private bool useWfRecorder;
    private Process? wfRecorderProcess;
    private int wfRecorderStopSignaled;
    private const int WfRecorderPollIntervalMilliseconds = 100;
    private static readonly TimeSpan WfRecorderGracefulStopTimeout = TimeSpan.FromSeconds(5);

    public ScreenRecorder(ScreenRecordOutput outputType, ScreenRecordingOptions options, Screenshot screenshot, Rectangle captureRectangle)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(screenshot);

        if (string.IsNullOrEmpty(options.OutputPath))
        {
            throw new ArgumentException("Screen recorder cache path is empty.", nameof(options));
        }

        if (captureRectangle.Width <= 0 || captureRectangle.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureRectangle), captureRectangle, "The recording area must be non-empty.");
        }

        FPS = options.FPS;
        DurationSeconds = options.Duration;
        CaptureRectangle = captureRectangle;
        CachePath = options.OutputPath;
        OutputType = outputType;

        Options = options;

        switch (OutputType)
        {
            default:
            case ScreenRecordOutput.FFmpeg:
                FileHelpers.CreateDirectoryFromFilePath(Options.OutputPath);
                bool hasCustomCommands = Options.FFmpeg.UseCustomCommands && !string.IsNullOrWhiteSpace(Options.FFmpeg.CustomCommands);
                bool hasFfmpegExecutableOverride = Options.FFmpeg.OverrideCLIPath &&
                    !string.IsNullOrWhiteSpace(Options.FFmpeg.CLIPath);
                useWfRecorder = !hasCustomCommands && !hasFfmpegExecutableOverride &&
                    OperatingSystem.IsLinux() && LinuxAPI.IsWayland() && IsWfRecorderAvailable();
                if (!useWfRecorder)
                {
                    DebugHelper.WriteLine(
                        hasCustomCommands
                            ? "Screen-recording backend selected: custom FFmpeg commands."
                            : hasFfmpegExecutableOverride
                                ? $"Screen-recording backend selected: configured FFmpeg executable ({Options.FFmpeg.FFmpegPath})."
                                : "Screen-recording backend selected: FFmpeg.");
                    ffmpeg = new FFmpegCLIManager(Options.FFmpeg.FFmpegPath);
                    ffmpeg.ShowError = true;
                    ffmpeg.EncodeStarted += OnRecordingStarted;
                    ffmpeg.EncodeProgressChanged += OnEncodingProgressChanged;
                }
                else
                {
                    DebugHelper.WriteLine("Screen-recording backend selected: wf-recorder.");
                }
                break;
            case ScreenRecordOutput.GIF:
                imgCache = new HardDiskCache(Options);
                break;
        }

        this.screenshot = screenshot;
    }

    private static bool? wfRecorderAvailable;

    internal static bool IsWfRecorderAvailable()
    {
        if (wfRecorderAvailable.HasValue) return wfRecorderAvailable.Value;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "wf-recorder",
                ArgumentList = { "--help" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using Process? process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(2000))
            {
                if (process is not null)
                {
                    ForceStopWfRecorder(process);
                }
                wfRecorderAvailable = false;
            }
            else
            {
                wfRecorderAvailable = process.ExitCode == 0;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            wfRecorderAvailable = false;
        }

        return wfRecorderAvailable.Value;
    }

    private void UpdateInfo()
    {
        if (fps <= 0)
        {
            return;
        }

        delay = Math.Max(1, 1000 / fps);
        double requestedFrameCount = fps * (double)durationSeconds;
        frameCount = requestedFrameCount <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(requestedFrameCount));
    }

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (Interlocked.CompareExchange(ref recordingState, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref paused, 0);
        Interlocked.Exchange(ref aborted, 0);
        pauseGate.Set();
        LastRunSucceeded = false;

        try
        {
            // Stop can race with construction/start from the manager. Preserve a
            // pre-start stop request instead of clearing it and launching FFmpeg.
            if (Volatile.Read(ref stopRequested) != 0)
            {
                return;
            }

            if (OutputType == ScreenRecordOutput.FFmpeg)
            {
                LastRunSucceeded = useWfRecorder
                    ? RunWfRecorder()
                    : ffmpeg.Run(Options.GetFFmpegCommands());
            }
            else
            {
                OnRecordingStarted();
                RecordUsingCache();
                LastRunSucceeded = !IsAborted;
            }
        }
        finally
        {
            Interlocked.Exchange(ref recordingState, 0);
        }
    }

    private bool RunWfRecorder()
    {
        WfRecorderCaptureTarget? target = TryResolveWfRecorderTarget(CaptureRectangle);
        bool unresolvedHyprlandOutput = target is null && IsHyprlandSession();
        var startInfo = new ProcessStartInfo
        {
            FileName = "wf-recorder",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (target is not null)
        {
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(target.OutputName);
            if (!target.CoversWholeOutput)
            {
                startInfo.ArgumentList.Add("-g");
                // wf-recorder expects the region in global compositor logical
                // coordinates and subtracts the output origin itself. SnapX's
                // CaptureRectangle is already in that space (slurp/native
                // picker emit global logical coords), so the global rectangle
                // is passed through unchanged. Converting it to output-local
                // would be wrong on a multi-monitor layout where an output does
                // not sit at (0,0), because wf-recorder's "contained_in" check
                // tests the region against the global output bounds.
                startInfo.ArgumentList.Add(FormatGeometry(target.GlobalRectangle));
            }

            DebugHelper.WriteLine(
                $"Using wf-recorder output={target.OutputName} local={FormatGeometry(target.LocalRectangle)} " +
                $"global={FormatGeometry(CaptureRectangle)}{(target.WasClipped ? " (clipped)" : "")}" +
                (target.CoversWholeOutput ? " whole-output (geometry omitted)" : string.Empty));
        }
        else
        {
            // Non-Hyprland wlroots compositors retain the previous geometry
            // behavior. The failure diagnostics below now expose the real
            // compositor/wf-recorder reason if this fallback cannot resolve
            // one output.
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add(FormatGeometry(CaptureRectangle));
            if (!unresolvedHyprlandOutput)
            {
                DebugHelper.WriteLine(
                    $"Using wf-recorder without an explicit output; compositor output resolution was unavailable, " +
                    $"global={FormatGeometry(CaptureRectangle)}.");
            }
        }
        if (fps > 0)
        {
            startInfo.ArgumentList.Add("-r");
            startInfo.ArgumentList.Add(fps.ToString());
        }
        // When SnapX is generating the capture, wf-recorder is only used to
        // produce a high-quality intermediate. The user's FFmpeg codec and
        // quality settings are applied by the final pass in ScreenRecordManager,
        // so this capture backend must not silently override them with its own
        // defaults. Keep the intermediate mathematically lossless.
        if (Options.IsLossless)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("libx264");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("crf=0");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("preset=ultrafast");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("tune=zerolatency");
            if (Options.FFmpeg.IsAudioSourceSelected)
            {
                startInfo.ArgumentList.Add("-C");
                startInfo.ArgumentList.Add("aac");
                startInfo.ArgumentList.Add("-P");
                startInfo.ArgumentList.Add("bit_rate=192000");
            }
        }
        // wf-recorder does not capture audio unless it is explicitly told to.
        // Pass through the user-selected audio source (resolved to a PulseAudio
        // / PipeWire device name on Linux, matching the FFmpeg path) so a
        // Wayland recording carries the selected audio track instead of being
        // silently silent. When no audio source is configured we leave the
        // default silent behavior intact.
        string audioSource = Options.FFmpeg.IsAudioSourceSelected
            ? FFmpegCaptureDevice.ResolveAudioSource(Options.FFmpeg.AudioSource)
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(audioSource))
        {
            // wf-recorder's --audio takes an OPTIONAL argument and only
            // consumes it in attached form (-aname / --audio=name). Passing
            // "-a name" as two argv entries would treat "name" as a stray
            // positional, so the device must be embedded in a single argument.
            startInfo.ArgumentList.Add("--audio=" + audioSource);
            // PipeWire's PulseAudio compatibility layer is the backend enabled
            // on Omarchy and most Arch/Wayland systems. wf-recorder's default
            // can also resolve through libpulse when pipewire-pulse is absent;
            // naming it explicitly avoids an unexpected default-selection change
            // when multiple audio backends are installed.
            startInfo.ArgumentList.Add("--audio-backend=pulse");
        }
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(Options.OutputPath);
        string commandLine = FormatCommandLine(startInfo);
        if (unresolvedHyprlandOutput)
        {
            DebugHelper.WriteLine(
                $"wf-recorder output resolution failed: Hyprland output name for -o could not be resolved; " +
                $"skipped command: {commandLine}");
            return false;
        }
        DebugHelper.WriteLine($"wf-recorder command: {commandLine}");

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("SnapX could not start wf-recorder for Wayland screen recording.");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            DebugHelper.WriteException(ex);
            return false;
        }

        Interlocked.Exchange(ref wfRecorderStopSignaled, 0);
        wfRecorderProcess = process;
        var standardError = new StringBuilder();
        var standardOutput = new StringBuilder();
        try
        {
            // wf-recorder does not write anything meaningful to stdout, but its
            // pipes must still be drained so the process cannot block on a full
            // buffer during a long recording.
            process.OutputDataReceived += (_, args) => AppendProcessLine(standardOutput, args.Data);
            process.ErrorDataReceived += (_, args) => AppendProcessLine(standardError, args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            OnRecordingStarted();
            bool exitedSuccessfully = WaitForWfRecorderExit(process);
            // Ensure asynchronous output handlers have consumed the final
            // lines after the process exit notification.
            process.WaitForExit();

            bool hasOutput = File.Exists(Options.OutputPath);
            long outputLength = hasOutput ? new FileInfo(Options.OutputPath).Length : 0;
            if (!exitedSuccessfully || !hasOutput || outputLength <= 0)
            {
                string error = ReadProcessText(standardError);
                string output = ReadProcessText(standardOutput);
                DebugHelper.WriteLine(
                    $"wf-recorder failed: exitCode={process.ExitCode}, command={commandLine}, " +
                    $"outputExists={hasOutput}, outputBytes={outputLength}, " +
                    $"stderr={(string.IsNullOrWhiteSpace(error) ? "<empty>" : error.Trim())}" +
                    (string.IsNullOrWhiteSpace(output) ? string.Empty : $", stdout={output.Trim()}"));
                return false;
            }

            return true;
        }
        finally
        {
            wfRecorderProcess = null;
            process.Dispose();
        }
    }

    private sealed record WfRecorderCaptureTarget(
        string OutputName,
        Rectangle GlobalRectangle,
        Rectangle LocalRectangle,
        bool CoversWholeOutput,
        bool WasClipped);

    private sealed record HyprlandOutput(
        string Name,
        Rectangle LogicalBounds,
        bool Focused);

    private static WfRecorderCaptureTarget? TryResolveWfRecorderTarget(Rectangle requested)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "hyprctl",
                ArgumentList = { "monitors", "-j" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            string json = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(3000) || process.ExitCode != 0)
            {
                DebugHelper.WriteLine(
                    $"wf-recorder output resolution failed: hyprctl exitCode=" +
                    $"{(process.HasExited ? process.ExitCode : -1)}, stderr={error.Trim()}");
                return null;
            }

            List<HyprlandOutput> outputs = ParseHyprlandOutputs(json);
            if (outputs.Count == 0)
            {
                DebugHelper.WriteLine("wf-recorder output resolution found no usable Hyprland outputs.");
                return null;
            }

            int centerX = requested.X + requested.Width / 2;
            int centerY = requested.Y + requested.Height / 2;
            var candidates = outputs
                .Select(output => new
                {
                    Output = output,
                    Intersection = Rectangle.Intersect(requested, output.LogicalBounds)
                })
                .Select(candidate => new
                {
                    candidate.Output,
                    candidate.Intersection,
                    Area = (long)Math.Max(0, candidate.Intersection.Width) * Math.Max(0, candidate.Intersection.Height),
                    ContainsCenter = candidate.Output.LogicalBounds.Contains(centerX, centerY)
                })
                .Where(candidate => candidate.Area > 0)
                .OrderByDescending(candidate => candidate.Area)
                .ThenByDescending(candidate => candidate.ContainsCenter)
                .ThenByDescending(candidate => candidate.Output.Focused)
                .ToList();

            HyprlandOutput chosen;
            Rectangle clipped;
            if (candidates.Count > 0)
            {
                chosen = candidates[0].Output;
                clipped = candidates[0].Intersection;
            }
            else
            {
                chosen = outputs.FirstOrDefault(output => output.Focused) ?? outputs[0];
                clipped = chosen.LogicalBounds;
                DebugHelper.WriteLine(
                    $"wf-recorder capture geometry {FormatGeometry(requested)} did not intersect a reported output; " +
                    $"using focused/fallback output {chosen.Name}.");
            }

            Rectangle bounds = chosen.LogicalBounds;
            bool coversWholeOutput = clipped == bounds ||
                (requested.X <= bounds.X && requested.Y <= bounds.Y &&
                 (long)requested.Right >= bounds.Right && (long)requested.Bottom >= bounds.Bottom);
            Rectangle local = coversWholeOutput
                ? new Rectangle(0, 0, bounds.Width, bounds.Height)
                : new Rectangle(clipped.X - bounds.X, clipped.Y - bounds.Y, clipped.Width, clipped.Height);
            bool wasClipped = clipped != requested;

            DebugHelper.WriteLine(
                $"wf-recorder output resolution: requested={FormatGeometry(requested)}, output={chosen.Name}, " +
                $"bounds={FormatGeometry(bounds)}, overlap={FormatGeometry(clipped)}, " +
                $"local={FormatGeometry(local)}, clipped={wasClipped}, wholeOutput={coversWholeOutput}.");
            return new WfRecorderCaptureTarget(
                chosen.Name,
                clipped,
                local,
                coversWholeOutput,
                wasClipped);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException
                                   or JsonException or IOException)
        {
            DebugHelper.WriteLine($"wf-recorder output resolution failed: {ex.Message}");
            return null;
        }
    }

    private static List<HyprlandOutput> ParseHyprlandOutputs(string json)
    {
        static bool IsQuarterTurn(int transform) => transform is 1 or 3 or 5 or 7;

        var outputs = new List<HyprlandOutput>();
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement monitor in document.RootElement.EnumerateArray())
        {
            if (!monitor.TryGetProperty("name", out JsonElement nameElement) ||
                !monitor.TryGetProperty("x", out JsonElement xElement) ||
                !monitor.TryGetProperty("y", out JsonElement yElement) ||
                !monitor.TryGetProperty("width", out JsonElement widthElement) ||
                !monitor.TryGetProperty("height", out JsonElement heightElement) ||
                !monitor.TryGetProperty("scale", out JsonElement scaleElement))
            {
                continue;
            }

            string? name = nameElement.GetString();
            double scale = scaleElement.GetDouble();
            if (string.IsNullOrWhiteSpace(name) || scale <= 0)
            {
                continue;
            }

            int transform = monitor.TryGetProperty("transform", out JsonElement transformElement)
                ? transformElement.GetInt32()
                : 0;
            bool rotated = IsQuarterTurn(transform);
            int logicalWidth = (int)Math.Round(
                (rotated ? heightElement.GetDouble() : widthElement.GetDouble()) / scale);
            int logicalHeight = (int)Math.Round(
                (rotated ? widthElement.GetDouble() : heightElement.GetDouble()) / scale);
            if (logicalWidth <= 0 || logicalHeight <= 0)
            {
                continue;
            }

            outputs.Add(new HyprlandOutput(
                name,
                new Rectangle(
                    (int)Math.Round(xElement.GetDouble()),
                    (int)Math.Round(yElement.GetDouble()),
                    logicalWidth,
                    logicalHeight),
                monitor.TryGetProperty("focused", out JsonElement focusedElement) && focusedElement.GetBoolean()));
        }

        return outputs;
    }

    private static bool IsHyprlandSession()
    {
        return Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
                   ?.Contains("Hyprland", StringComparison.OrdinalIgnoreCase) == true ||
               Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP")
                   ?.Contains("Hyprland", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string FormatGeometry(Rectangle rectangle) =>
        $"{rectangle.X},{rectangle.Y} {rectangle.Width}x{rectangle.Height}";

    private static string FormatCommandLine(ProcessStartInfo startInfo) =>
        startInfo.FileName + " " + string.Join(" ", startInfo.ArgumentList.Select(QuoteCommandArgument));

    private static string QuoteCommandArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) || argument.Contains('\'')
            ? "'" + argument.Replace("'", "'\\''") + "'"
            : argument;

    private static void AppendProcessLine(StringBuilder builder, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            builder.AppendLine(line);
        }
    }

    private static string ReadProcessText(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.ToString();
        }
    }

    /// <summary>
    /// Waits for wf-recorder without allowing a compositor or encoder hang to
    /// strand the recording manager forever. SIGINT is retained as the normal
    /// stop path because it finalizes the media file; process-tree termination
    /// is used only after that graceful path has had a bounded chance to exit.
    /// </summary>
    private bool WaitForWfRecorderExit(Process process)
    {
        TimeSpan? duration = durationSeconds > 0
            ? TimeSpan.FromSeconds(durationSeconds)
            : null;
        return WaitForWfRecorderExit(
            process,
            () => Volatile.Read(ref stopRequested) != 0,
            duration,
            WfRecorderGracefulStopTimeout,
            SignalWfRecorderGracefully);
    }

    private static bool WaitForWfRecorderExit(
        Process process,
        Func<bool> isStopRequested,
        TimeSpan? duration,
        TimeSpan gracefulStopTimeout,
        Action<Process> signalGracefulStop)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(isStopRequested);
        ArgumentNullException.ThrowIfNull(signalGracefulStop);
        if (duration is { } requestedDuration && requestedDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        if (gracefulStopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracefulStopTimeout));
        }

        Stopwatch recordingTimer = Stopwatch.StartNew();
        Stopwatch? gracefulStopTimer = null;

        while (!process.WaitForExit(WfRecorderPollIntervalMilliseconds))
        {
            bool reachedDuration = duration is { } activeDuration && recordingTimer.Elapsed >= activeDuration;
            bool shouldStop = isStopRequested() || reachedDuration;
            if (!shouldStop)
            {
                continue;
            }

            if (gracefulStopTimer is null)
            {
                signalGracefulStop(process);
                gracefulStopTimer = Stopwatch.StartNew();
                continue;
            }

            if (gracefulStopTimer.Elapsed < gracefulStopTimeout)
            {
                continue;
            }

            DebugHelper.WriteLine(
                "wf-recorder did not exit within {0} seconds of SIGINT; force-stopping its process tree.",
                gracefulStopTimeout.TotalSeconds);
            ForceStopWfRecorder(process);
            return false;
        }

        return process.ExitCode == 0;
    }

    private void SignalWfRecorderGracefully(Process process)
    {
        if (Interlocked.Exchange(ref wfRecorderStopSignaled, 1) == 0)
        {
            StopWfRecorderGracefully(process);
        }
    }

    private static void StopWfRecorderGracefully(Process process)
    {
        try
        {
            if (process.HasExited) return;

            // wf-recorder only finalizes (muxes) its output file on SIGINT -
            // Process.Kill() sends SIGKILL on Linux, which leaves a corrupt
            // file. `kill -INT` matches wf-recorder's own documented
            // "Ctrl+C to stop" contract.
            using var killProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "kill",
                ArgumentList = { "-INT", process.Id.ToString() },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            killProcess?.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
        }
    }

    private static void ForceStopWfRecorder(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // The process can exit between HasExited and Kill. Either outcome
            // is fine; the session manager will observe the completed child.
            DebugHelper.WriteException(ex);
        }
    }

    private void RecordUsingCache()
    {
        try
        {
            for (int i = 0; Volatile.Read(ref stopRequested) == 0 && (frameCount == 0 || i < frameCount); i++)
            {
                pauseGate.Wait();
                if (Volatile.Read(ref stopRequested) != 0)
                {
                    break;
                }

                Stopwatch timer = Stopwatch.StartNew();

                Image img = screenshot.CaptureRectangle(CaptureRectangle)
                    ?? throw new InvalidOperationException("The screenshot backend returned no frame while recording a GIF.");
                //DebugHelper.WriteLine("Screen capture: " + (int)timer.ElapsedMilliseconds);

                imgCache.AddImageAsync(img);

                if (Volatile.Read(ref stopRequested) == 0 && (frameCount == 0 || i + 1 < frameCount))
                {
                    int sleepTime = delay - (int)timer.ElapsedMilliseconds;

                    if (sleepTime > 0)
                    {
                        Thread.Sleep(sleepTime);
                    }
                    else if (sleepTime < 0)
                    {
                        // Need to handle FPS drops
                    }
                }
            }
        }
        finally
        {
            imgCache.Finish();
        }
    }

    public void StopRecording()
    {
        Interlocked.Exchange(ref stopRequested, 1);
        pauseGate.Set();

        if (ffmpeg != null)
        {
            ffmpeg.Close();
        }

        Process? process = wfRecorderProcess;
        if (process != null)
        {
            SignalWfRecorderGracefully(process);
        }
    }

    /// <summary>
    /// Toggles pause for the frame-cache recorder. FFmpeg pause is implemented by
    /// ScreenRecordManager using finalized recording segments.
    /// </summary>
    public bool PauseRecording()
    {
        if (!IsRecording || OutputType != ScreenRecordOutput.GIF)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref paused, 1, 0) == 0)
        {
            pauseGate.Reset();
        }
        else
        {
            Interlocked.Exchange(ref paused, 0);
            pauseGate.Set();
        }

        return true;
    }

    public void AbortRecording()
    {
        Interlocked.Exchange(ref aborted, 1);
        LastRunSucceeded = false;
        StopRecording();
    }

    public void SaveAsGIF(string? path, GIFQuality quality)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A GIF output path is required.", nameof(path));
        }

        if (IsRecording)
        {
            throw new InvalidOperationException("A GIF cannot be saved while recording is still active.");
        }

        if (imgCache is not HardDiskCache hdCache || hdCache.Count == 0)
        {
            throw new InvalidOperationException("No captured GIF frames are available.");
        }

        FileHelpers.CreateDirectoryFromFilePath(path);
        using IEnumerator<Image> frames = hdCache.GetImageEnumerator().GetEnumerator();
        if (!frames.MoveNext())
        {
            throw new InvalidOperationException("No captured GIF frames are available.");
        }

        using Image firstFrame = frames.Current;
        using Image animation = firstFrame.Clone(_ => { });
        int frameDelay = Math.Max(1, (int)Math.Round(delay / 10d));
        animation.Metadata.GetGifMetadata().RepeatCount = 0;
        animation.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = frameDelay;

        int encodedFrames = 1;
        while (frames.MoveNext())
        {
            using Image frame = frames.Current;
            frame.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = frameDelay;
            animation.Frames.AddFrame(frame.Frames.RootFrame);
            encodedFrames++;
            OnEncodingProgressChanged(encodedFrames * 100f / hdCache.Count);
        }

        animation.SaveAsGif(path, new GifEncoder { Quantizer = TaskHelpers.GetGifQuantizer(quality) });
        OnEncodingProgressChanged(100);
    }

    public bool FFmpegEncodeVideo(string input, string? output)
    {
        FileHelpers.CreateDirectoryFromFilePath(output);

        Options.IsRecording = false;
        Options.IsLossless = false;
        Options.InputPath = input;
        Options.OutputPath = output;

        try
        {
            ffmpeg.TrackEncodeProgress = true;

            return ffmpeg.Run(Options.GetFFmpegCommands());
        }
        finally
        {
            ffmpeg.TrackEncodeProgress = false;
        }
    }

    public bool FFmpegEncodeAsGIF(string input, string? output)
    {
        FileHelpers.CreateDirectoryFromFilePath(output);

        try
        {
            ffmpeg.TrackEncodeProgress = true;

            StringBuilder args = new StringBuilder();

            args.Append($"-i \"{input}\" ");

            // https://ffmpeg.org/ffmpeg-filters.html#palettegen-1
            args.Append($"-lavfi \"palettegen=stats_mode={Options.FFmpeg.GIFStatsMode}[palette],");

            // https://ffmpeg.org/ffmpeg-filters.html#paletteuse
            args.Append($"[0:v][palette]paletteuse=dither={Options.FFmpeg.GIFDither}");

            if (Options.FFmpeg.GIFDither == FFmpegPaletteUseDither.bayer)
            {
                args.Append($":bayer_scale={Options.FFmpeg.GIFBayerScale}");
            }

            if (Options.FFmpeg.GIFStatsMode == FFmpegPaletteGenStatsMode.single)
            {
                args.Append(":new=1");
            }

            args.Append("\" ");
            args.Append("-y ");
            args.Append($"\"{output}\"");

            return ffmpeg.Run(args.ToString());
        }
        finally
        {
            ffmpeg.TrackEncodeProgress = false;
        }
    }

    protected void OnRecordingStarted()
    {
        RecordingStarted?.Invoke();
    }

    protected void OnEncodingProgressChanged(float progress)
    {
        int currentProgress = (int)progress;

        if (EncodingProgressChanged != null && currentProgress != previousProgress)
        {
            EncodingProgressChanged(currentProgress);
            previousProgress = currentProgress;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        StopRecording();

        if (ffmpeg != null)
        {
            ffmpeg.Dispose();
        }

        if (imgCache != null)
        {
            imgCache.Dispose();
        }

        pauseGate.Dispose();
    }
}
