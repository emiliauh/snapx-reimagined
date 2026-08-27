// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.Job;
using SnapX.Core.ScreenCapture;

namespace SnapX.Core.Capture;

/// <summary>
/// Interactive rectangle capture shared by the hotkey, CLI, and UI paths.
/// The actual selector is supplied by the desktop frontend through
/// <see cref="RegionCaptureTasks.SetRegionSelector"/>.
/// </summary>
public class CaptureRegion : CaptureBase
{
    protected static RegionCaptureType lastRegionCaptureType = RegionCaptureType.Default;

    public RegionCaptureType RegionCaptureType { get; protected set; }

    public CaptureRegion()
    {
    }

    public CaptureRegion(RegionCaptureType regionCaptureType)
    {
        RegionCaptureType = regionCaptureType;
    }

    // A selector is UI-owned. Running the synchronous legacy Capture API on a
    // worker keeps the Avalonia dispatcher free to show and drive the selector.
    protected override bool ExecuteOnBackgroundThread => true;

    protected override TaskMetadata? Execute(TaskSettings taskSettings)
    {
        return RegionCaptureType switch
        {
            RegionCaptureType.Light => ExecuteRegionCapture(taskSettings, RegionCaptureType.Light),
            RegionCaptureType.Transparent => ExecuteRegionCapture(taskSettings, RegionCaptureType.Transparent),
            _ => ExecuteRegionCapture(taskSettings, RegionCaptureType.Default)
        };
    }

    protected TaskMetadata? ExecuteRegionCapture(TaskSettings taskSettings) =>
        ExecuteRegionCapture(taskSettings, RegionCaptureType.Default);

    protected TaskMetadata? ExecuteRegionCaptureLight(TaskSettings taskSettings)
    {
        DebugHelper.Logger?.Information(
            "Light region capture is using the cross-platform rectangle selector fallback.");
        return ExecuteRegionCapture(taskSettings, RegionCaptureType.Light);
    }

    protected TaskMetadata? ExecuteRegionCaptureTransparent(TaskSettings taskSettings)
    {
        DebugHelper.Logger?.Information(
            "Transparent region capture is using the cross-platform rectangle selector fallback.");
        return ExecuteRegionCapture(taskSettings, RegionCaptureType.Transparent);
    }

    private static TaskMetadata? ExecuteRegionCapture(TaskSettings taskSettings, RegionCaptureType captureType)
    {
        if (captureType != RegionCaptureType.Default)
        {
            DebugHelper.Logger?.Information(
                "{CaptureType} region capture is using the cross-platform rectangle selector fallback.",
                captureType);
        }

        RegionCaptureSelection? selection = RegionCaptureTasks.SelectRegionAsync(
                taskSettings.CaptureSettings.SurfaceOptions,
                captureType,
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
        lastRegionCaptureType = captureType;
        return metadata;
    }
}
