// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.Job;
using SnapX.Core.ScreenCapture;

namespace SnapX.Core.Capture;

/// <summary>
/// Lets the user hover over windows to highlight them and click to capture
/// the one under the cursor, instead of always grabbing whichever window
/// currently has focus.
/// </summary>
public class CaptureWindowPicker : CaptureBase
{
    protected override bool ExecuteOnBackgroundThread => true;

    protected override TaskMetadata? Execute(TaskSettings taskSettings)
    {
        // Clone rather than mutate the shared SurfaceOptions instance -
        // WindowPickerMode must not leak into unrelated region captures.
        RegionCaptureOptions options = RegionCaptureTasks.GetRegionCaptureOptions(taskSettings.CaptureSettings.SurfaceOptions);
        options.WindowPickerMode = true;

        RegionCaptureSelection? selection = RegionCaptureTasks.SelectRegionAsync(
                options,
                RegionCaptureType.Default,
                captureImage: true)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        if (selection?.Image is null)
        {
            return null;
        }

        var metadata = new TaskMetadata(selection.Image);
        metadata.UpdateInfo(selection.WindowInfo);
        return metadata;
    }
}
