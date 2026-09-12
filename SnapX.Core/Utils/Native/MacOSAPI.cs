using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SnapX.Core.Media;
using uniffi.snapxrust;

namespace SnapX.Core.Utils.Native;

public class MacOSAPI : NativeAPI
{
    public override void CopyImage(Image image) => CopyImage(image, null);

    public override void CopyImage(Image image, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(image);
        // Use a unique internal filename: callers' names must never become script source.
        var tempPath = Path.Combine(Path.GetTempPath(), $"snapx-clipboard-{Guid.NewGuid():N}.png");
        try
        {
            image.Save(tempPath, new PngEncoder());
            var startInfo = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("on run argv\nset the clipboard to (read (POSIX file (item 1 of argv)) as «class PNGf»)\nend run");
            startInfo.ArgumentList.Add(tempPath);
            using var process = Process.Start(startInfo)
                ?? throw new IOException("Unable to start osascript for the clipboard.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new IOException($"osascript failed: {error}");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    public override void CopyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var startInfo = new ProcessStartInfo("/usr/bin/pbcopy")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false
        };
        // pbcopy selects its input encoding from the locale.
        startInfo.Environment["LC_CTYPE"] = "UTF-8";
        using var process = Process.Start(startInfo)
            ?? throw new IOException("Unable to start pbcopy.");
        process.StandardInput.Write(text);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"pbcopy failed: {error}");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CGPoint
    {
        public double X;
        public double Y;
    }

    private const string CoreGraphicsLib =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphicsLib)]
    static extern CGPoint CGEventGetLocation(IntPtr eventRef);

    [DllImport(CoreGraphicsLib)]
    static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(CoreFoundationLib)]
    static extern void CFRelease(IntPtr eventRef);

    private const string CoreFoundationLib =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreGraphicsLib)]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] displays, out uint displayCount);
    [DllImport(CoreGraphicsLib)]
    private static extern uint CGMainDisplayID();
    [DllImport(CoreGraphicsLib)]
    private static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreGraphicsLib)]
    private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
    [DllImport(CoreGraphicsLib)]
    private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);
    [DllImport(CoreGraphicsLib)]
    private static extern void CGDisplayModeRelease(IntPtr mode);

    public static List<Screen> GetScreens()
    {
        var ids = new uint[32];
        if (CGGetActiveDisplayList((uint)ids.Length, ids, out var count) != 0)
            throw new IOException("Unable to enumerate macOS displays.");
        var primary = CGMainDisplayID();
        var screens = new List<Screen>();
        for (int i = 0; i < count; i++)
        {
            var bounds = CGDisplayBounds(ids[i]);
            double scale = 1;
            var mode = CGDisplayCopyDisplayMode(ids[i]);
            try
            {
                // CGDisplayPixelsWide reports logical width on scaled Retina modes.
                // The display mode exposes the backing pixel width instead.
                if (mode != IntPtr.Zero && bounds.Width > 0)
                    scale = Math.Max(1, (double)CGDisplayModeGetPixelWidth(mode) / bounds.Width);
            }
            finally { if (mode != IntPtr.Zero) CGDisplayModeRelease(mode); }
            string name = ids[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Preserve the names used by the existing native monitor API where its unsigned
            // point interface can represent the display center.
            var centerX = bounds.X + bounds.Width / 2;
            var centerY = bounds.Y + bounds.Height / 2;
            if (centerX >= 0 && centerY >= 0)
            {
                try { name = SnapxrustMethods.GetMonitor((uint)centerX, (uint)centerY).Name; }
                catch (Exception ex) { DebugHelper.WriteException(ex, "Unable to read the display name"); }
            }
            screens.Add(new Screen
            {
                Id = ids[i].ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = name,
                Bounds = new Rectangle((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height),
                IsPrimary = ids[i] == primary,
                Index = i,
                ScaleFactor = scale,
                SessionType = SessionType.macOS
            });
        }
        return screens;
    }

    public static Rectangle GetDesktopBounds()
    {
        var screens = GetScreens();
        if (screens.Count == 0) throw new InvalidOperationException("No active macOS displays.");
        return screens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
    }

    public override Screen? GetScreen(Point pos) => GetScreens().FirstOrDefault(s => s.Bounds.Contains(pos));

    // Options for window list
    private const uint kCGWindowListOptionIncludingWindow = 1 << 3;

    [DllImport(CoreGraphicsLib)]
    private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(CoreFoundationLib)]
    private static extern nint CFArrayGetCount(IntPtr theArray);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr theArray, nint idx);
    [DllImport(CoreFoundationLib)]
    internal static extern IntPtr CFStringCreateWithCString(
        IntPtr alloc,
        string str,
        uint encoding
    );

    [DllImport(CoreFoundationLib)]
    internal static extern IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);

    [DllImport(CoreGraphicsLib)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CGRectMakeWithDictionaryRepresentation(
        IntPtr dict,
        out CGRect rect
    );
    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    // Standard UTF8 encoding ID for CFString
    internal const uint kCFStringEncodingUTF8 = 0x08000100;

    public override Point GetCursorPosition()
    {
        var ev = CGEventCreate(IntPtr.Zero);
        if (ev == IntPtr.Zero)
            throw new InvalidOperationException("Unable to read the macOS cursor position.");
        try
        {
            var point = CGEventGetLocation(ev);
            return new Point((int)point.X, (int)point.Y);
        }
        finally
        {
            CFRelease(ev);
        }
    }

    public override void ShowWindow(WindowInfo window)
    {
        if (window.ProcessId == 0)
            return;

        var script =
            $"tell application \"System Events\" to set frontmost of every process whose unix id is {window.ProcessId} to true";

        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi)
            ?? throw new IOException("Unable to start osascript to activate the window.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"Unable to activate the window: {error}");
    }
    public override Rectangle GetWindowRectangle(WindowInfo window)
    {
        return GetWindowRectangle(window.Handle);
    }
    public override Rectangle GetWindowRectangle(IntPtr windowHandle)
    {
        uint windowId = (uint)windowHandle.ToInt32();

        IntPtr arrayRef = CGWindowListCopyWindowInfo(kCGWindowListOptionIncludingWindow, windowId);
        if (arrayRef == IntPtr.Zero)
            return Rectangle.Empty;

        try
        {
            nint count = CFArrayGetCount(arrayRef);
            if (count == 0)
                return Rectangle.Empty;

            IntPtr dictRef = CFArrayGetValueAtIndex(arrayRef, 0);

            return ExtractCGRectFromDict(dictRef);
        }
        finally
        {
            CFRelease(arrayRef);
        }
    }
    private Rectangle ExtractCGRectFromDict(IntPtr dictRef)
    {
        IntPtr key = CFStringCreateWithCString(
            IntPtr.Zero,
            "kCGWindowBounds",
            kCFStringEncodingUTF8
        );

        try
        {
            IntPtr boundsDict = CFDictionaryGetValue(dictRef, key);

            if (
                boundsDict != IntPtr.Zero
                && CGRectMakeWithDictionaryRepresentation(boundsDict, out CGRect cgRect)
            )
            {
                return new Rectangle(
                    (int)cgRect.X,
                    (int)cgRect.Y,
                    (int)cgRect.Width,
                    (int)cgRect.Height
                );
            }
        }
        finally
        {
            if (key != IntPtr.Zero)
                CFRelease(key);
        }

        return Rectangle.Empty;
    }

    [DllImport(CoreFoundationLib)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr number, int type, out int value);

    private static bool IsCaptureWindow(ulong handle)
    {
        var windows = CGWindowListCopyWindowInfo(kCGWindowListOptionIncludingWindow, unchecked((uint)handle));
        if (windows == IntPtr.Zero) return false;
        var key = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowLayer", kCFStringEncodingUTF8);
        try
        {
            if (CFArrayGetCount(windows) == 0 || key == IntPtr.Zero) return false;
            var dictionary = CFArrayGetValueAtIndex(windows, 0);
            var number = CFDictionaryGetValue(dictionary, key);
            // Normal application windows occupy layer zero. Cursor, menu-bar and
            // other system overlays must not become window-capture candidates.
            return number != IntPtr.Zero && CFNumberGetValue(number, 3, out var layer) && layer == 0;
        }
        finally
        {
            if (key != IntPtr.Zero) CFRelease(key);
            CFRelease(windows);
        }
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // A window's owner may exit between native enumeration and this lookup.
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    public override List<WindowInfo> GetWindowList()
    {
        DebugHelper.WriteLine($"GetWindowList called");

        var rawWindows = SnapxrustMethods.GetWindowList();

        var windows = rawWindows
            .Where(raw => IsCaptureWindow(raw.Hwnd))
            .Select(raw => new WindowInfo
            {
                ProcessId = (int)raw.ProcessId,
                ProcessName = GetProcessName((int)raw.ProcessId),

                Title = raw.Title,
                Rectangle = new Rectangle(raw.X, raw.Y, (int)raw.Width, (int)raw.Height),
                IsMinimized = raw.IsMinimized,
                IsVisible = !raw.IsMinimized,
                IsActive = raw.IsFocused,
                Handle = (nint)raw.Hwnd,
            })
            .ToList();

        return windows;
    }
}
