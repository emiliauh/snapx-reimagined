using System.Runtime.InteropServices;

namespace SnapX.Core.Utils.Native;

/// <summary>
/// Synthetic input used by ShareX-style scrolling capture. On X11 the events
/// are delivered through the XTest extension; on Wayland we fall back to
/// <c>ydotool</c> (uinput) because the compositor does not permit cross-client
/// XTest injection. Platforms without a safe driver are reported honestly.
/// </summary>
public static partial class InputHelpers
{
    private static int? _xtestAvailable;

    /// <summary>
    /// Reports whether a synthetic-input backend is available for the current
    /// session. Only backends that actually drive <see cref="SendMouseWheel"/>
    /// or <see cref="SendKeyPress"/> are reported as available, so callers do
    /// not claim scrolling capture works when it would silently no-op. On
    /// Linux the availability is true when either the X11 XTest extension can
    /// be reached (DISPLAY is set) or a Wayland key injector (<c>wtype</c> or
    /// <c>ydotool</c>) is present. The Windows SendInput path is intentionally
    /// not implemented here, so Windows reports false rather than pretending.
    /// </summary>
    public static bool HasInputBackend()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            if (IsXTestAvailable())
            {
                return true;
            }

            return TryFindTool("wtype") is not null || TryFindTool("ydotool") is not null;
        }

        return false;
    }

    /// <summary>
    /// Scrolls the mouse wheel. A negative <paramref name="amount"/> scrolls
    /// down (the direction ShareX's scrolling capture uses), a positive amount
    /// scrolls up. Each notch is one 120-unit X11 wheel tick.
    /// </summary>
    public static bool SendMouseWheel(int amount)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return false;
        }

        // On a hybrid Wayland/X11 session the common scrolling-capture target
        // is an XWayland window (Chromium, Electron, Firefox under XWayland).
        // Such windows scroll from X11 XTest wheel events delivered to the
        // window under the pointer, without needing keyboard focus. A focused-
        // window virtual keyboard (wtype) cannot reach the target after the
        // region selector exits and focus returns to SnapX. So try the X11
        // wheel first; it is the reliable path for XWayland windows and the
        // only one that keeps consecutive frames overlapping (real wheel
        // notches scroll a small distance, so the stitcher can join frames).
        if (TrySendMouseWheelX11(amount))
        {
            return true;
        }

        // Native Wayland: ydotool's "scroll" command, then the virtual-keyboard
        // fallback for windows that accept a key-driven scroll.
        if (TrySendMouseWheelWayland(amount))
        {
            return true;
        }

        return TrySendKeyPressForScroll(amount);
    }

    /// <summary>Presses a single key (Down, End, Next/PageDown, Home, ...).</summary>
    public static bool SendKeyPress(KeyCode key)
    {
        if (OperatingSystem.IsWindows())
        {
            return SendKeyPressWindows(key);
        }

        // On native Wayland, prefer the compositor virtual-keyboard backend
        // (wtype/ydotool) so the key reaches Wayland-only windows. Use X11
        // XTest only as the fallback for X11/XWayland-only sessions.
        bool nativeWayland = OperatingSystem.IsLinux() && LinuxAPI.IsWayland();
        if (nativeWayland ? TrySendKeyWayland(key) : (OperatingSystem.IsLinux() && TrySendKeyX11(key)))
        {
            return true;
        }

        if (OperatingSystem.IsLinux() && TrySendKeyWayland(key))
        {
            return true;
        }

        return OperatingSystem.IsLinux() && TrySendKeyX11(key);
    }

    private static bool TrySendMouseWheelX11(int amount)
    {
        if (!OperatingSystem.IsLinux() || IsXTestUnavailable())
        {
            return false;
        }

        try
        {
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                // Negative amount means "scroll down" (matches the caller's
                // SendMouseWheel(-120 * ScrollAmount) convention). Button 5 is
                // the X11 wheel-down event; button 4 is wheel-up.
                int button = amount < 0 ? 5 : 4;
                int clicks = Math.Max(1, Math.Abs(amount) / 120);
                for (int i = 0; i < clicks; i++)
                {
                    XTestFakeButtonEvent(display, (uint)button, true, CurrentTime);
                    XTestFakeButtonEvent(display, (uint)button, false, CurrentTime);
                }
                XFlush(display);
                return true;
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySendMouseWheelWayland(int amount)
    {
        string? tool = FindTool("ydotool");
        if (tool is null)
        {
            return false;
        }

        int clicks = Math.Max(1, Math.Abs(amount) / 120);
        string dir = amount > 0 ? "4" : "5";
        return RunExternal(tool, ["scroll", "--button", dir, "--repeat", clicks.ToString()]);
    }

    private static bool TrySendKeyX11(KeyCode key)
    {
        if (!OperatingSystem.IsLinux() || IsXTestUnavailable())
        {
            return false;
        }

        int keysym = ToX11Keysym(key);
        if (keysym == 0)
        {
            return false;
        }

        try
        {
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                ulong keycode = XKeysymToKeycode(display, (IntPtr)keysym);
                if (keycode == 0)
                {
                    return false;
                }

                XTestFakeKeyEvent(display, keycode, true, CurrentTime);
                XTestFakeKeyEvent(display, keycode, false, CurrentTime);
                XFlush(display);
                return true;
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySendKeyWayland(KeyCode key)
    {
        // wtype drives the compositor's virtual-keyboard protocol natively, so
        // it reaches Wayland-only windows that an X11 XTest injection cannot.
        // It is the preferred Wayland backend for key-based scrolling.
        string? wtype = FindTool("wtype");
        string? wtypeName = ToWtypeKeyName(key);
        if (wtype is not null
            && wtypeName is not null
            && RunExternal(wtype, ["-k", wtypeName]))
        {
            return true;
        }

        // Fall back to ydotool (uinput) when the wtype daemon/tool is absent.
        string? ydotool = FindTool("ydotool");
        string? name = ToYdotoolKeyName(key);
        if (ydotool is not null)
        {
            return name is not null && RunExternal(ydotool, ["key", name]);
        }

        return false;
    }

    /// <summary>
    /// Emulates a mouse-wheel scroll using the compositor's virtual keyboard
    /// (PageDown/PageUp). Used only as a last resort on Wayland where literal
    /// wheel injection is unavailable, so the scrolling-capture pipeline still
    /// advances the page instead of capturing identical frames forever.
    /// </summary>
    private static bool TrySendKeyPressForScroll(int amount)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        // A PageDown advances a full viewport, so two consecutive capture frames
        // share almost no overlap and the stitcher cannot join them - the capture
        // then bails after a couple of frames with a top-only slice. Use the Down
        // key (a small, line-level scroll) so frames keep a large overlap; the
        // stitcher then follows the page down to the bottom and stops when the
        // stitched height stops growing.
        KeyCode key = amount < 0 ? KeyCode.Down : KeyCode.Up;
        int repeats = Math.Max(1, Math.Abs(amount) / 120);
        bool any = false;
        for (int i = 0; i < repeats; i++)
        {
            if (SendKeyPress(key))
            {
                any = true;
            }
        }
        return any;
    }

    private static bool SendKeyPressWindows(KeyCode key)
    {
        // Windows input injection requires a P/Invoke to user32 keybd_event or
        // SendInput. It is not exercised on this Linux host and is intentionally
        // left as a no-op to avoid claiming unsupported behavior.
        _ = key;
        return false;
    }

    private static int ToX11Keysym(KeyCode key) =>
        key switch
        {
            KeyCode.Down => 0xff54,
            KeyCode.Up => 0xff52,
            KeyCode.PageDown => 0xff56,
            KeyCode.PageUp => 0xff55,
            KeyCode.Home => 0xff50,
            KeyCode.End => 0xff57,
            _ => 0
        };

    private static string? ToYdotoolKeyName(KeyCode key) =>
        key switch
        {
            KeyCode.Down => "down",
            KeyCode.Up => "up",
            KeyCode.PageDown => "pagedown",
            KeyCode.PageUp => "pageup",
            KeyCode.Home => "home",
            KeyCode.End => "end",
            _ => null
        };

    /// <summary>
    /// Maps a scroll key to the name <c>wtype</c> understands. wtype resolves
    /// keys through libxkbcommon, whose names differ from ydotool's (for
    /// example PageDown is "Page_Down" / "Next", not "pagedown").
    /// </summary>
    private static string? ToWtypeKeyName(KeyCode key) =>
        key switch
        {
            KeyCode.Down => "Down",
            KeyCode.Up => "Up",
            KeyCode.PageDown => "Page_Down",
            KeyCode.PageUp => "Page_Up",
            KeyCode.Home => "Home",
            KeyCode.End => "End",
            _ => null
        };

    private static bool IsXTestUnavailable()
    {
        if (_xtestAvailable is int v)
        {
            return v == 0;
        }

        bool available = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        _xtestAvailable = available ? 1 : 0;
        return !available;
    }

    private static bool IsXTestAvailable()
    {
        if (_xtestAvailable is int v)
        {
            return v == 1;
        }

        bool available = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        _xtestAvailable = available ? 1 : 0;
        return available;
    }

    private static string? FindTool(string name)
    {
        return TryFindTool(name);
    }

    private static string? TryFindTool(string name)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool RunExternal(string tool, string[] args)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tool,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
            using var process = System.Diagnostics.Process.Start(startInfo);
            return process is not null && process.WaitForExit(2000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private const uint CurrentTime = 0;

    [System.Runtime.InteropServices.LibraryImport("libX11.so.6")]
    private static partial IntPtr XOpenDisplay(IntPtr display);

    [System.Runtime.InteropServices.LibraryImport("libX11.so.6")]
    private static partial void XCloseDisplay(IntPtr display);

    [System.Runtime.InteropServices.LibraryImport("libX11.so.6")]
    private static partial int XFlush(IntPtr display);

    [System.Runtime.InteropServices.LibraryImport("libX11.so.6")]
    private static partial ulong XKeysymToKeycode(IntPtr display, IntPtr keysym);

    [System.Runtime.InteropServices.LibraryImport("libXtst.so.6")]
    private static partial int XTestFakeButtonEvent(IntPtr display, uint button, [MarshalAs(UnmanagedType.I1)] bool is_press, uint delay);

    [System.Runtime.InteropServices.LibraryImport("libXtst.so.6")]
    private static partial int XTestFakeKeyEvent(IntPtr display, ulong keycode, [MarshalAs(UnmanagedType.I1)] bool is_press, uint delay);
}

public enum KeyCode
{
    Up,
    Down,
    PageUp,
    PageDown,
    Home,
    End
}
