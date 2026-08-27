// SPDX-License-Identifier: GPL-3.0-or-later


using SixLabors.ImageSharp;
using SnapX.Core.Job;
using SnapX.Core.Upload;
using SnapX.Core.Utils.Extensions;
using SnapX.Core.Utils.Native;

namespace SnapX.Core.Capture;

public abstract class CaptureBase
{
    private static int captureInProgress;
    private static Task activeCaptureTask = Task.CompletedTask;

    /// <summary>
    /// Awaits the background capture launched by the most recent <see cref="Capture"/>
    /// call. A short-lived host (the CLI) must await this before exiting, otherwise
    /// the process can terminate before the capture has finished decoding the frame
    /// and handing it off to the upload/save pipeline.
    /// </summary>
    public static Task WaitForActiveCaptureAsync() => activeCaptureTask;

    /// <summary>
    /// Releases the interactive-capture gate immediately. A cancelled or
    /// failed region selector must not leave the app reporting
    /// "already in progress" until the background task happens to finish.
    /// </summary>
    public static void CancelActiveCapture()
    {
        int value = Interlocked.Exchange(ref captureInProgress, 0);
        if (value != 0)
        {
            DebugHelper.WriteLine("Interactive screen capture was cancelled.");
        }
    }

    public bool AllowAutoHideForm { get; set; } = true;
    public bool AllowAnnotation { get; set; } = true;

    /// <summary>
    /// Wayland capture is process and image-decoding work.  Keep it off the
    /// frontend dispatcher so pressing a capture action never freezes the UI
    /// while grim or the portal returns the frame. Interactive implementations
    /// can opt in on other platforms as well.
    /// </summary>
    protected virtual bool ExecuteOnBackgroundThread =>
        OperatingSystem.IsLinux() && LinuxAPI.IsWayland();

    public void Capture(bool autoHideForm)
    {
        Capture(null, autoHideForm);
    }

    public void Capture(TaskSettings taskSettings = null, bool autoHideForm = false)
    {
        if (taskSettings == null)
            taskSettings = TaskSettings.GetDefaultTaskSettings();

        // A Wayland capture can take several seconds. Starting a second job
        // while the first job owns the selector or image backend makes their
        // results interleave and produces misleading history cards.
        if (Interlocked.CompareExchange(ref captureInProgress, 1, 0) != 0)
        {
            // A dropped request that only writes to the debug log looks
            // indistinguishable from a hotkey/menu item that silently does
            // nothing, so also tell the user why through the same channel
            // CaptureInternal uses for capture failures.
            DebugHelper.WriteLine("A screen capture is already in progress; ignoring the new request.");
            SnapXL.EventAggregator.Publish(new ErrorMessageEvent(
                new InvalidOperationException("A screen capture is already in progress. Wait for it to finish before starting another."),
                "Screen capture",
                false));
            return;
        }

        // TODO: Reimplement taskSettings.GeneralSettings.ToastWindowAutoHide
        // if (taskSettings.GeneralSettings.ToastWindowAutoHide)
        // {
        //     NotificationForm.CloseActiveForm();
        // }

        if (ExecuteOnBackgroundThread)
        {
            activeCaptureTask = Task.Run(async () =>
            {
                if (taskSettings.CaptureSettings.ScreenshotDelay > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds((double)taskSettings.CaptureSettings.ScreenshotDelay))
                        .ConfigureAwait(false);
                }

                CaptureInternal(taskSettings, autoHideForm);
            });
        }
        else if (taskSettings.CaptureSettings.ScreenshotDelay > 0)
        {
            int delay = (int)(taskSettings.CaptureSettings.ScreenshotDelay * 1000);

            Task.Delay(delay)
                .ContinueInCurrentContext(() =>
                {
                    CaptureInternal(taskSettings, autoHideForm);
                });
        }
        else
        {
            CaptureInternal(taskSettings, autoHideForm);
        }
    }

    protected abstract TaskMetadata? Execute(TaskSettings taskSettings);

    private void CaptureInternal(TaskSettings taskSettings, bool autoHideForm)
    {
        if (autoHideForm && AllowAutoHideForm)
        {
            // SnapX.MainWindow.Hide();
            // Thread.Sleep(250);
        }

        TaskMetadata? metadata = null;

        try
        {
            AllowAnnotation = true;
            metadata = Execute(taskSettings);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            SnapXL.EventAggregator.Publish(new ErrorMessageEvent(ex, "Screen capture failed", true));
        }
        finally
        {
            try
            {
                if (autoHideForm && AllowAutoHideForm)
                {
                    // SnapX.MainWindow.ForceActivate();
                }

                AfterCapture(metadata, taskSettings);
            }
            finally
            {
                Interlocked.Exchange(ref captureInProgress, 0);
            }
        }
    }

    private void AfterCapture(TaskMetadata? metadata, TaskSettings taskSettings)
    {
        if (metadata != null && metadata.Image != null)
        {
            TaskHelpers.PlayNotificationSoundAsync(NotificationSound.Capture, taskSettings);

            if (
                taskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.AnnotateImage)
                && !AllowAnnotation
            )
            {
                taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(
                    AfterCaptureTasks.AnnotateImage
                );
            }

            if (
                taskSettings.ImageSettings.ImageEffectOnlyRegionCapture
                && GetType() != typeof(CaptureRegion)
                && GetType() != typeof(CaptureLastRegion)
            )
            {
                taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(
                    AfterCaptureTasks.AddImageEffects
                );
            }

            UploadManager.RunImageTask(metadata, taskSettings);
        }
    }

    protected TaskMetadata CreateMetadata()
    {
        return CreateMetadata(Rectangle.Empty, null);
    }

    protected TaskMetadata CreateMetadata(Rectangle insideRect)
    {
        return CreateMetadata(insideRect, "explorer");
    }

    protected TaskMetadata CreateMetadata(Rectangle insideRect, string ignoreProcess)
    {
        var metadata = new TaskMetadata();

        // Querying the X11 foreground window can block under Wayland/XWayland.
        // Capture metadata is optional for a full-screen image, so do not let
        // that legacy lookup hold the screenshot pipeline indefinitely.
        var windowInfo = OperatingSystem.IsLinux() && LinuxAPI.IsWayland()
            ? null
            : Methods.GetForegroundWindow();
        if (windowInfo != null)
        {
            if (
                (
                    ignoreProcess == null
                    || !windowInfo.ProcessName.Equals(
                        ignoreProcess,
                        StringComparison.OrdinalIgnoreCase
                    )
                ) && (insideRect.IsEmpty || windowInfo.Rectangle.Contains(insideRect))
            )
            {
                metadata.UpdateInfo(windowInfo);
            }
        }

        return metadata;
    }
}
