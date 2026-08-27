namespace SnapX.Core.Hotkey;

/// <summary>
/// Parses the shortcut text used by the settings editor.
/// </summary>
public static class HotkeyParser
{
    public static bool TryParse(string? text, out Keys key, out bool win, out string error)
    {
        key = Keys.None;
        win = false;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a key combination.";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "Enter a key combination.";
            return false;
        }

        foreach (var rawPart in parts)
        {
            var part = rawPart.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (part.Equals("CTRL", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("CONTROL", StringComparison.OrdinalIgnoreCase))
            {
                key |= Keys.Control;
                continue;
            }
            if (part.Equals("SHIFT", StringComparison.OrdinalIgnoreCase))
            {
                key |= Keys.Shift;
                continue;
            }
            if (part.Equals("ALT", StringComparison.OrdinalIgnoreCase))
            {
                key |= Keys.Alt;
                continue;
            }
            if (part.Equals("WIN", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("META", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("SUPER", StringComparison.OrdinalIgnoreCase))
            {
                win = true;
                continue;
            }

            if (!TryParseKey(part, out var parsedKey))
            {
                error = $"Unknown key: {rawPart}.";
                key = Keys.None;
                win = false;
                return false;
            }

            if ((key & Keys.KeyCode) != Keys.None)
            {
                error = "Enter one non-modifier key.";
                key = Keys.None;
                win = false;
                return false;
            }

            key |= parsedKey;
        }

        var keyCode = key & Keys.KeyCode;
        if (keyCode == Keys.None || !Enum.IsDefined(keyCode))
        {
            error = "Enter one valid non-modifier key.";
            key = Keys.None;
            win = false;
            return false;
        }

        var info = new HotkeyInfo(key) { Win = win };
        if (!info.IsValidHotkey)
        {
            error = "This key combination is not valid.";
            key = Keys.None;
            win = false;
            return false;
        }

        return true;
    }

    private static bool TryParseKey(string text, out Keys key)
    {
        if (text.Length == 1 && char.IsDigit(text[0]))
        {
            key = (Keys)((int)Keys.D0 + (text[0] - '0'));
            return true;
        }

        if (text.Length == 1 && char.IsLetter(text[0]) &&
            Enum.TryParse(text.ToUpperInvariant(), out key))
        {
            return true;
        }

        var normalized = text.ToUpperInvariant() switch
        {
            "BACKSPACE" => nameof(Keys.Back),
            "ENTER" => nameof(Keys.Return),
            "CAPSLOCK" => nameof(Keys.CapsLock),
            "PAGEDOWN" => nameof(Keys.PageDown),
            "PAGEUP" => nameof(Keys.PageUp),
            "PRINTSCREEN" => nameof(Keys.PrintScreen),
            "ESC" => nameof(Keys.Escape),
            "SPACEBAR" => nameof(Keys.Space),
            "NUMPAD0" => nameof(Keys.NumPad0),
            "NUMPAD1" => nameof(Keys.NumPad1),
            "NUMPAD2" => nameof(Keys.NumPad2),
            "NUMPAD3" => nameof(Keys.NumPad3),
            "NUMPAD4" => nameof(Keys.NumPad4),
            "NUMPAD5" => nameof(Keys.NumPad5),
            "NUMPAD6" => nameof(Keys.NumPad6),
            "NUMPAD7" => nameof(Keys.NumPad7),
            "NUMPAD8" => nameof(Keys.NumPad8),
            "NUMPAD9" => nameof(Keys.NumPad9),
            _ => text
        };

        return Enum.TryParse(normalized, true, out key) &&
            (key & Keys.Modifiers) == Keys.None;
    }
}
