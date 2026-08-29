// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;

namespace SnapX.Core.ScreenCapture;

/// <summary>
/// Options controlling ShareX-style scrolling capture. These mirror the
/// upstream ShareX <c>ScrollingCaptureOptions</c> so behavior can be matched
/// closely across platforms.
/// </summary>
public class ScrollingCaptureOptions
{
    [DefaultValue(300)]
    public int StartDelay { get; set; } = 300;

    [DefaultValue(false)]
    public bool AutoScrollTop { get; set; }

    [DefaultValue(300)]
    public int ScrollDelay { get; set; } = 300;

    [DefaultValue(ScrollMethod.MouseWheel)]
    public ScrollMethod ScrollMethod { get; set; } = ScrollMethod.MouseWheel;

    [DefaultValue(2)]
    public int ScrollAmount { get; set; } = 2;

    [DefaultValue(true)]
    public bool AutoIgnoreBottomEdge { get; set; } = true;

    [DefaultValue(false)]
    public bool AutoUpload { get; set; }

    [DefaultValue(true)]
    public bool ShowRegion { get; set; } = true;
}
