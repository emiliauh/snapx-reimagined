// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.Job;
using SnapX.Core.ScreenCapture;

namespace SnapX.Core.Capture;

public class CaptureLastRegion : CaptureRegion
{
    protected override TaskMetadata? Execute(TaskSettings taskSettings)
    {
        if (!RegionCaptureTasks.TryGetLastRegion(out var rectangle, out var captureType))
        {
            return base.Execute(taskSettings);
        }

        var image = TaskHelpers.GetScreenshot(taskSettings).CaptureRectangle(rectangle);
        if (image == null)
        {
            throw new InvalidOperationException($"Capturing the last region {rectangle} returned no image.");
        }

        lastRegionCaptureType = captureType;
        var metadata = CreateMetadata(rectangle);
        metadata.Image = image;
        return metadata;
    }
}
