using FluentAvalonia.UI.Controls;
using SnapX.Core;
using SnapX.Core.Job;

namespace SnapX.Avalonia.Utils;

/// <summary>
/// Toggles an AfterCaptureTasks/AfterUploadTasks flag from a menu item whose
/// Tag names the flag (an optional leading '!' is stripped). Shared by every
/// "After Capture" / "After Upload" flyout in the app so their behavior
/// can't drift between the main window and Settings.
/// </summary>
public static class TaskFlagMenuHelper
{
    public static bool Toggle(FAToggleMenuFlyoutItem toggle)
    {
        string? key = toggle.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (key.StartsWith('!')) key = key[1..];

        if (Enum.TryParse(key, out AfterCaptureTasks flagCapture) && flagCapture != AfterCaptureTasks.None)
        {
            bool currentlyHasFlag = (SnapXL.Settings?.DefaultTaskSettings.AfterCaptureJob & flagCapture) == flagCapture;
            if (currentlyHasFlag)
                SnapXL.Settings!.DefaultTaskSettings.AfterCaptureJob &= ~flagCapture;
            else
                SnapXL.Settings!.DefaultTaskSettings.AfterCaptureJob |= flagCapture;
            SnapXL.Settings.DefaultTaskSettings.UseDefaultAfterCaptureJob = false;
            return true;
        }

        if (Enum.TryParse(key, out AfterUploadTasks flagUpload) && flagUpload != AfterUploadTasks.None)
        {
            bool currentlyHasFlag = (SnapXL.Settings?.DefaultTaskSettings.AfterUploadJob & flagUpload) == flagUpload;
            if (currentlyHasFlag)
                SnapXL.Settings!.DefaultTaskSettings.AfterUploadJob &= ~flagUpload;
            else
                SnapXL.Settings!.DefaultTaskSettings.AfterUploadJob |= flagUpload;
            SnapXL.Settings.DefaultTaskSettings.UseDefaultAfterUploadJob = false;
            return true;
        }

        return false;
    }

    public static void SyncCheckState(FAToggleMenuFlyoutItem toggle)
    {
        string? key = toggle.Tag?.ToString();
        if (key?.StartsWith('!') ?? false) key = key[1..];
        if (string.IsNullOrWhiteSpace(key)) return;

        if (Enum.TryParse(key, out AfterCaptureTasks flagCapture) && flagCapture != AfterCaptureTasks.None)
        {
            toggle.IsChecked = (SnapXL.Settings?.DefaultTaskSettings.AfterCaptureJob & flagCapture) == flagCapture;
        }
        else if (Enum.TryParse(key, out AfterUploadTasks flagUpload) && flagUpload != AfterUploadTasks.None)
        {
            toggle.IsChecked = (SnapXL.Settings?.DefaultTaskSettings.AfterUploadJob & flagUpload) == flagUpload;
        }
    }
}
