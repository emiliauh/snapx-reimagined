using System.Diagnostics;
using System.Reflection;
using SixLabors.ImageSharp;
using SnapX.Core.Media;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.ScreenCapture;
using SnapX.Core.ScreenCapture.ScreenRecording;
using SnapX.Core.Utils.Native;

internal static class MacOSRecordingChecks
{
    public static void Fuzz(Random random, ref int checks)
    {
        var method = typeof(ScreenRecordingOptions).GetMethod("ResolveMacOSCaptureTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (int i = 0; i < 2000; i++)
        {
            int x = random.Next(-5000, 5000), y = random.Next(-3000, 3000);
            var screen = new Screen { Bounds = new Rectangle(x, y, 1920, 1080), Index = 2, ScaleFactor = 2 };
            int w = random.Next(2, 1000), h = random.Next(2, 500);
            int localX = random.Next(0, 1920 - w), localY = random.Next(0, 1080 - h);
            var requested = new Rectangle(x + localX, y + localY, w, h);
            var result = ((Screen Screen, Rectangle Crop))method.Invoke(null, [requested, new[] { screen }, true])!;
            if (!ReferenceEquals(result.Screen, screen) || result.Crop != new Rectangle(localX, localY, w & ~1, h & ~1))
                throw new InvalidOperationException("macOS recording crop changed logical position or encoder alignment.");
            checks++;
        }
        foreach (Rectangle invalid in new[] { new Rectangle(0, 0, 0, 10), new Rectangle(0, 0, 1, 1), new Rectangle(-1, 0, 200, 100), new Rectangle(900, 0, 200, 100), new Rectangle(500, 0, int.MaxValue, 100), new Rectangle(0, 500, 100, int.MaxValue) })
        {
            try
            {
                method.Invoke(null, [invalid, new[] { new Screen { Bounds = new Rectangle(0, 0, 1000, 1000) } }, true]);
                throw new InvalidOperationException("Invalid recording crop was accepted.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException or InvalidOperationException) { checks++; }
        }
        if (OperatingSystem.IsMacOS())
        {
            string executableFixture = Path.GetTempFileName();
            try
            {
                var settings = new TaskSettings();
                settings.CaptureSettings.FFmpegOptions = new FFmpegOptions
                {
                    OverrideCLIPath = true, CLIPath = executableFixture, UseCustomCommands = false
                };
                typeof(ScreenRecordManager).GetMethod("ValidateStart", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [ScreenRecordOutput.FFmpeg, ScreenRecordStartMethod.CustomRegion, settings]);
                checks++;
            }
            finally { File.Delete(executableFixture); }
        }
    }

    public static async Task<int> Probe()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Requires macOS.");
        string executable = Environment.GetEnvironmentVariable("SNAPX_TEST_FFMPEG")
            ?? throw new InvalidOperationException("Set SNAPX_TEST_FFMPEG to an FFmpeg executable.");
        string outputDir = Path.Combine(Path.GetTempPath(), "snapx-recording-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        foreach (Screen screen in MacOSAPI.GetScreens())
        {
            Rectangle area = new(screen.Bounds.X + 10, screen.Bounds.Y + 10, 320, 240);
            var options = new ScreenRecordingOptions
            {
                IsRecording = true, FPS = 10, Duration = 2, CaptureArea = area, DrawCursor = true,
                OutputPath = Path.Combine(outputDir, $"screen-{screen.Index}.mp4"),
                FFmpeg = new FFmpegOptions { OverrideCLIPath = true, CLIPath = executable }
            };
            Console.WriteLine($"COMMAND: {options.GetFFmpegCommands()}");
            using var recorder = new ScreenRecorder(ScreenRecordOutput.FFmpeg, options, new Screenshot(), area);
            Task recording = Task.Run(recorder.StartRecording);
            try { await recording.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch { recorder.StopRecording(); throw; }
            if (!recorder.LastRunSucceeded || !File.Exists(options.OutputPath) || new FileInfo(options.OutputPath).Length < 100)
                throw new InvalidOperationException($"Recording failed for display {screen.Index}.");
            using var decoder = Process.Start(new ProcessStartInfo(executable)
            {
                ArgumentList = { "-v", "error", "-i", options.OutputPath, "-f", "null", "-" },
                UseShellExecute = false
            })!;
            await decoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (decoder.ExitCode != 0) throw new InvalidOperationException("Recorded MP4 failed decoding.");
            Console.WriteLine($"PASS: display {screen.Index} recorded and decoded: {options.OutputPath}");
        }
        Screen firstScreen = MacOSAPI.GetScreens()[0];
        Rectangle stopArea = new(firstScreen.Bounds.X + 20, firstScreen.Bounds.Y + 20, 320, 240);
        var stopOptions = new ScreenRecordingOptions
        {
            IsRecording = true, FPS = 10, CaptureArea = stopArea,
            OutputPath = Path.Combine(outputDir, "manual-stop.mp4"),
            FFmpeg = new FFmpegOptions { OverrideCLIPath = true, CLIPath = executable }
        };
        using var stopRecorder = new ScreenRecorder(ScreenRecordOutput.FFmpeg, stopOptions, new Screenshot(), stopArea);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stopRecorder.RecordingStarted += () => started.TrySetResult();
        Task stopTask = Task.Run(stopRecorder.StartRecording);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(1000);
            stopRecorder.StopRecording();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { stopRecorder.StopRecording(); }
        if (!stopRecorder.LastRunSucceeded || new FileInfo(stopOptions.OutputPath).Length < 100)
            throw new InvalidOperationException("Manual stop failed to finalize its recording.");
        Console.WriteLine($"PASS: manual stop finalized recording: {stopOptions.OutputPath}");
        await ProbeManager(executable, outputDir, stopArea);
        return 0;
    }

    private static async Task ProbeManager(string executable, string outputDir, Rectangle area)
    {
        var previousSettings = SnapXL.Settings;
        string previousPersonal = SnapXL.CustomPersonalPath;
        try
        {
            var settings = new TaskSettings
            {
                UseDefaultAfterCaptureJob = false, AfterCaptureJob = AfterCaptureTasks.None,
                UseDefaultAfterUploadJob = false, AfterUploadJob = AfterUploadTasks.None,
                UseDefaultGeneralSettings = false,
                OverrideScreenshotsFolder = true, ScreenshotsFolder = outputDir
            };
            settings.GeneralSettings.PlaySoundAfterCapture = false;
            settings.GeneralSettings.PlaySoundAfterUpload = false;
            settings.GeneralSettings.PlaySoundAfterAction = false;
            settings.GeneralSettings.ShowToastNotificationAfterTaskCompleted = false;
            settings.CaptureSettings.ScreenRecordStartDelay = 0;
            settings.CaptureSettings.ScreenRecordFPS = 10;
            settings.CaptureSettings.ScreenRecordFixedDuration = false;
            settings.CaptureSettings.FFmpegOptions = new FFmpegOptions { OverrideCLIPath = true, CLIPath = executable };
            SnapXL.CustomPersonalPath = outputDir;
            SnapXL.Settings = new ApplicationConfig { DefaultTaskSettings = settings, HistorySaveTasks = false };
            RegionCaptureTasks.SetRegionSelector((_, _) => Task.FromResult<RegionCaptureSelection?>(new() { Rectangle = area }));
            ScreenRecordManager.StartStopRecording(ScreenRecordOutput.FFmpeg, ScreenRecordStartMethod.Region, settings);
            await WaitForState(ScreenRecordManager.RecordingManagerState.Recording);
            await Task.Delay(1000);
            ScreenRecordManager.PauseScreenRecording();
            await WaitForState(ScreenRecordManager.RecordingManagerState.Paused);
            ScreenRecordManager.PauseScreenRecording();
            await WaitForState(ScreenRecordManager.RecordingManagerState.Recording);
            await Task.Delay(1000);
            ScreenRecordManager.StartStopRecording(ScreenRecordOutput.FFmpeg, ScreenRecordStartMethod.Region, settings);
            await WaitForState(ScreenRecordManager.RecordingManagerState.Idle);
            if (ScreenRecordManager.LastError is not null) throw ScreenRecordManager.LastError;
            if (ScreenRecordManager.LastOutputPath is not { } output || !File.Exists(output))
                throw new InvalidOperationException("Manager produced no finalized recording.");
            Console.WriteLine($"PASS: manager region start/pause/resume/toggle-stop finalized recording: {output}");
        }
        finally
        {
            ScreenRecordManager.StopRecording();
            RegionCaptureTasks.SetRegionSelector(null);
            SnapXL.Settings = previousSettings;
            SnapXL.CustomPersonalPath = previousPersonal;
        }

        static async Task WaitForState(ScreenRecordManager.RecordingManagerState expected)
        {
            var timer = Stopwatch.StartNew();
            while (ScreenRecordManager.CurrentState != expected)
            {
                if (ScreenRecordManager.LastError is not null) throw ScreenRecordManager.LastError;
                if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException($"Manager did not reach {expected}; was {ScreenRecordManager.CurrentState}.");
                await Task.Delay(50);
            }
        }
    }
}
