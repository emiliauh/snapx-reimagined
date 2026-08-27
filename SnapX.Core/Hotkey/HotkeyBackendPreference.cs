using System.ComponentModel;

namespace SnapX.Core.Hotkey;

/// <summary>
/// Selects the global hotkey backend.
/// </summary>
public enum HotkeyBackendPreference
{
    [Description("Automatic")]
    Automatic,
    [Description("Wayland portal")]
    WaylandPortal,
    [Description("X11")]
    X11,
    [Description("Disabled")]
    Disabled
}
