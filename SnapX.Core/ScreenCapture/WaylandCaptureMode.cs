using System.ComponentModel;

namespace SnapX.Core.ScreenCapture;

/// <summary>
/// Selects the capture path that SnapX uses in a Wayland session.
/// </summary>
public enum WaylandCaptureMode
{
    [Description("Automatic")]
    Automatic,
    [Description("Wayland portal")]
    Portal,
    [Description("KDE KWin")]
    KWin,
    [Description("X11 fallback")]
    X11Fallback
}
