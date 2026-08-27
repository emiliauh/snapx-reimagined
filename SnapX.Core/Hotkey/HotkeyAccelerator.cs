// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.Hotkey;

internal static class HotkeyAccelerator
{
    public static string ToPortalAccelerator(HotkeyInfo hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);

        var parts = new List<string>(4);
        if (hotkey.Control) parts.Add("CTRL");
        if (hotkey.Shift) parts.Add("SHIFT");
        if (hotkey.Alt) parts.Add("ALT");
        if (hotkey.Win) parts.Add("META");
        parts.Add(ToKeyName(hotkey.KeyCode));
        return string.Join('+', parts);
    }

    private static string ToKeyName(Keys key) => key switch
    {
        Keys.Back => "BACKSPACE",
        Keys.Tab => "TAB",
        Keys.Return => "ENTER",
        Keys.NumPadEnter => "ENTER",
        Keys.Escape => "ESCAPE",
        Keys.Space => "SPACE",
        Keys.PageUp => "PAGEUP",
        Keys.PageDown => "PAGEDOWN",
        Keys.Home => "HOME",
        Keys.End => "END",
        Keys.Left => "LEFT",
        Keys.Right => "RIGHT",
        Keys.Up => "UP",
        Keys.Down => "DOWN",
        Keys.Insert => "INSERT",
        Keys.Delete => "DELETE",
        Keys.PrintScreen => "PRINT",
        Keys.CapsLock => "CAPSLOCK",
        Keys.NumLock => "NUMLOCK",
        Keys.Scroll => "SCROLLLOCK",
        Keys.NumPadMultiply => "KP_MULTIPLY",
        Keys.NumPadAdd => "KP_ADD",
        Keys.NumPadSubtract => "KP_SUBTRACT",
        Keys.NumPadDecimal => "KP_DECIMAL",
        Keys.NumPadDivide => "KP_DIVIDE",
        Keys.NumPadEquals => "KP_EQUAL",
        Keys.D0 => "0",
        Keys.D1 => "1",
        Keys.D2 => "2",
        Keys.D3 => "3",
        Keys.D4 => "4",
        Keys.D5 => "5",
        Keys.D6 => "6",
        Keys.D7 => "7",
        Keys.D8 => "8",
        Keys.D9 => "9",
        >= Keys.A and <= Keys.Z => key.ToString(),
        >= Keys.F1 and <= Keys.F24 => key.ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => $"KP_{(int)key - (int)Keys.NumPad0}",
        _ => key.ToString().ToUpperInvariant()
    };
}
