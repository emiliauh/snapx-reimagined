// SPDX-License-Identifier: GPL-3.0-or-later

using SixLabors.ImageSharp;
using SnapX.Core.Job;

namespace SnapX.Core.Capture;

/// <summary>
/// Runs a repeating timed screen capture, matching ShareX's AutoCapture
/// behavior. A capture starts after the configured interval, and each new
/// capture waits for the previous <see cref="CaptureBase"/> invocation to
/// finish so captures never overlap.
/// </summary>
public static class AutoCaptureManager
{
    public enum AutoCaptureState
    {
        Stopped,
        Running
    }

    private static readonly object StateLock = new();
    private static CancellationTokenSource? sessionCancellation;
    private static AutoCaptureState state;

    /// <summary>Gets the live auto-capture state under lock.</summary>
    public static AutoCaptureState State
    {
        get
        {
            lock (StateLock)
            {
                return state;
            }
        }
    }

    /// <summary>True while the auto-capture loop is running.</summary>
    public static bool IsRunning => State == AutoCaptureState.Running;

    /// <summary>
    /// The effective repeat interval in seconds. Values are clamped to the
    /// range [1 second, 24 hours] so the loop can neither spin nor stall.
    /// </summary>
    public static TimeSpan GetEffectiveInterval(decimal repeatTimeSeconds)
    {
        double seconds = (double)repeatTimeSeconds;
        if (double.IsNaN(seconds))
        {
            return TimeSpan.FromSeconds(1);
        }

        double clamped = Math.Clamp(seconds, 1, 24 * 60 * 60);
        return TimeSpan.FromSeconds(clamped);
    }

    /// <summary>Starts (or restarts) the auto-capture loop.</summary>
    public static void Start(TaskSettings? taskSettings = null)
    {
        taskSettings ??= TaskSettings.GetDefaultTaskSettings();
        TaskSettings safe = TaskSettings.GetSafeTaskSettings(taskSettings);
        decimal configuredInterval = SnapXL.Settings?.AutoCaptureRepeatTime ?? 60;
        TimeSpan interval = GetEffectiveInterval(configuredInterval);

        CancellationTokenSource cancellation;
        lock (StateLock)
        {
            if (state == AutoCaptureState.Running)
            {
                DebugHelper.WriteLine("AutoCapture is already running; restarting the interval.");
                sessionCancellation?.Cancel();
                sessionCancellation?.Dispose();
            }

            sessionCancellation = new CancellationTokenSource();
            cancellation = sessionCancellation;
            state = AutoCaptureState.Running;
        }

        // The cancellation token is observed inside RunLoopAsync's own delay.
        // Do not pass the (possibly later disposed) token to Task.Run, which
        // would throw ObjectDisposedException if Stop() disposes it first.
        _ = Task.Run(() => RunLoopAsync(safe, interval, cancellation));
    }

    /// <summary>Stops the auto-capture loop if it is running.</summary>
    public static void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (StateLock)
        {
            if (state != AutoCaptureState.Running)
            {
                return;
            }

            state = AutoCaptureState.Stopped;
            cancellation = sessionCancellation;
            sessionCancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        DebugHelper.WriteLine("AutoCapture stopped.");
    }

    /// <summary>Toggles between running and stopped.</summary>
    public static void Toggle(TaskSettings? taskSettings = null)
    {
        if (IsRunning)
        {
            Stop();
        }
        else
        {
            Start(taskSettings);
        }
    }

    private static async Task RunLoopAsync(
        TaskSettings taskSettings,
        TimeSpan interval,
        CancellationTokenSource session)
    {
        CancellationToken cancellation = session.Token;
        DebugHelper.WriteLine($"AutoCapture started. Interval: {interval.TotalSeconds:0.##}s.");

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellation).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested)
                {
                    break;
                }

                await RunSingleCaptureAsync(taskSettings, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when Stop() cancels the session.
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            SnapXL.EventAggregator.Publish(new ErrorMessageEvent(ex, "AutoCapture failed", true));
        }
        finally
        {
            lock (StateLock)
            {
                // Only transition to Stopped when this exact session is still the
                // active one. A concurrent Stop() or Start() may replace it.
                if (sessionCancellation == session && state == AutoCaptureState.Running)
                {
                    state = AutoCaptureState.Stopped;
                    sessionCancellation = null;
                    session.Dispose();
                }
            }
        }
    }

    private static async Task RunSingleCaptureAsync(
        TaskSettings taskSettings,
        CancellationToken cancellation)
    {
        Rectangle region = SnapXL.Settings?.AutoCaptureRegion ?? Rectangle.Empty;

        CaptureBase capture;
        if (!region.IsEmpty)
        {
            // CaptureCustomRegion reads CaptureSettings.CaptureCustomRegion, so
            // feed the configured auto-capture region into that slot.
            taskSettings.CaptureSettings.CaptureCustomRegion = region;
            capture = new CaptureCustomRegion();
        }
        else
        {
            capture = new CaptureFullscreen();
        }

        // Run the synchronous Capture on a background thread, then wait for the
        // active capture before the next interval fires so captures never overlap.
        await Task.Run(() => capture.Capture(taskSettings), cancellation).ConfigureAwait(false);
        await CaptureBase.WaitForActiveCaptureAsync().ConfigureAwait(false);
    }
}
