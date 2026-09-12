using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.Fonts;
using SnapX.Core.Utils.Native;
using SnapX.Core.SharpCapture.macOS;

if (!OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("This smoke test requires macOS.");
    return 1;
}

if (args.Contains("--tray-click-policy"))
{
    const ulong leftMouseDown = 1;
    const ulong leftMouseUp = 2;
    const ulong rightMouseUp = 4;
    const ulong controlModifier = 1UL << 18;
    if (Native.SnapXTrayClassifyEvent(leftMouseUp, 0) != 1 ||
        Native.SnapXTrayClassifyEvent(leftMouseUp, controlModifier) != 2 ||
        Native.SnapXTrayClassifyEvent(rightMouseUp, 0) != 2 ||
        Native.SnapXTrayClassifyEvent(leftMouseDown, 0) != 3 ||
        Native.SnapXTrayClassifyEvent(leftMouseDown, controlModifier) != 0)
        throw new InvalidOperationException("The macOS tray helper does not preserve primary/menu click routing.");
    Console.WriteLine("PASS: macOS tray helper routes left-click to the primary action and right/Control-click to the native menu.");
    return 0;
}

if (args.Contains("--ocr"))
{
    try
    {
        SnapX.Core.DebugHelper.Init(Path.Combine(Path.GetTempPath(), "snapx-ocr-smoke.log"));
        var fonts = new FontCollection();
        var font = fonts.Add("/System/Library/Fonts/Supplemental/Arial.ttf").CreateFont(48);
        using var synthetic = new Image<Rgba32>(1000, 240, Color.White);
        synthetic.Mutate(ctx => ctx.DrawText("SNAPX MACOS TEST", font, Color.Black, new PointF(30, 30))
            .DrawText("Invoice 12345", font, Color.Black, new PointF(30, 120)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Console.WriteLine("Running local OCR on generated text; model files may be downloaded, image pixels are not uploaded.");
        using var result = await SnapX.Core.Job.TaskHelpers.OCRImageDetailed(
            image: synthetic, languageCode: "eng", cts: timeout.Token,
            progress: new Progress<SnapX.Core.Job.TaskHelpers.OCRProgress>(p => Console.WriteLine($"OCR {p.Percent:0}%: {p.Status}")));
        Console.WriteLine($"Recognized text: {result.FullText}");
        if (!result.FullText.Contains("SNAPX MACOS TEST", StringComparison.OrdinalIgnoreCase) ||
            !result.FullText.Contains("12345", StringComparison.Ordinal))
            throw new InvalidOperationException("OCR did not recognize the expected synthetic text.");
        Console.WriteLine($"PASS: Local OCR recognized both synthetic lines ({result.Lines.Count} detected lines).");
        using var blank = new Image<Rgba32>(320, 160, Color.White);
        using var blankResult = await SnapX.Core.Job.TaskHelpers.OCRImageDetailed(image: blank, languageCode: "eng", cts: timeout.Token);
        if (!string.IsNullOrWhiteSpace(blankResult.FullText) || blankResult.Lines.Count != 0)
            throw new InvalidOperationException("OCR hallucinated text on a blank image.");
        Console.WriteLine("PASS: Blank image produced no OCR text.");
        if (synthetic[0, 0] != new Rgba32(255, 255, 255, 255))
            throw new InvalidOperationException("Caller-owned OCR source image changed.");
        var sourcePath = Path.Combine(Path.GetTempPath(), $"snapx-ocr-source-{Guid.NewGuid():N}.png");
        try
        {
            await synthetic.SaveAsPngAsync(sourcePath);
            using var fileResult = await SnapX.Core.Job.TaskHelpers.OCRImageDetailed(filePath: sourcePath, languageCode: "eng", cts: timeout.Token);
            if (!fileResult.FullText.Contains("12345", StringComparison.Ordinal))
                throw new InvalidOperationException("File-based OCR did not recognize the generated text.");
        }
        finally { File.Delete(sourcePath); }
        Console.WriteLine("PASS: File-path OCR recognizes text and caller-owned images remain usable.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL: local OCR: {ex}");
        return 1;
    }
}

if (args.Contains("--no-overlays"))
{
    var remaining = uniffi.snapxrust.SnapxrustMethods.GetWindowList().Count(w => w.Title == "RegionSelectorWindow");
    if (remaining != 0) throw new InvalidOperationException($"{remaining} live selector overlays remain after completion/cancellation.");
    Console.WriteLine("PASS: No live selector overlays remain.");
    return 0;
}

if (args.Contains("--multi-monitor-capture"))
{
    var displays = MacOSAPI.GetScreens();
    if (displays.Count < 2) throw new InvalidOperationException("This probe requires at least two displays.");
    foreach (var display in displays)
        Console.WriteLine($"Display {display.Index}: {display.Bounds}; scale={display.ScaleFactor}");
    int seams = 0;
    for (int i = 0; i < displays.Count; i++)
    for (int j = i + 1; j < displays.Count; j++)
    {
        var a = displays[i].Bounds;
        var b = displays[j].Bounds;
        Rectangle region;
        int left = Math.Max(a.Left, b.Left), right = Math.Min(a.Right, b.Right);
        int top = Math.Max(a.Top, b.Top), bottom = Math.Min(a.Bottom, b.Bottom);
        if (left < right && (a.Bottom == b.Top || b.Bottom == a.Top))
            region = new Rectangle((left + right) / 2 - 32, Math.Max(a.Top, b.Top) - 32, 64, 64);
        else if (top < bottom && (a.Right == b.Left || b.Right == a.Left))
            region = new Rectangle(Math.Max(a.Left, b.Left) - 32, (top + bottom) / 2 - 32, 64, 64);
        else continue;
        using var image = await new macOSCapture().CaptureRectangle(region)
            ?? throw new InvalidOperationException("Cross-display capture returned no image.");
        if (image.Width < region.Width || image.Height < region.Height)
            throw new InvalidOperationException("Cross-display capture truncated the requested bounds.");
        Console.WriteLine($"PASS: display {i}/{j} seam {region} captured {image.Width}x{image.Height}; pixels were not saved.");
        seams++;
    }
    if (seams == 0) throw new InvalidOperationException("No touching display edges found for the seam test.");
    return 0;
}

if (args.Contains("--overlay-alpha") || args.Contains("--overlay-opacity"))
{
    // The picker is intentionally above the normal application-window layer,
    // so query the unfiltered native list rather than capture candidates.
    var overlays = uniffi.snapxrust.SnapxrustMethods.GetWindowList().Where(w => w.Title == "RegionSelectorWindow").ToList();
    if (overlays.Count == 0) throw new InvalidOperationException("Open the region picker before running --overlay-alpha.");
    if (args.Contains("--all-displays"))
    {
        foreach (var display in MacOSAPI.GetScreens())
        {
            var center = new Point(display.Bounds.X + display.Bounds.Width / 2, display.Bounds.Y + display.Bounds.Height / 2);
            var displayOverlays = overlays
                .Where(w => new Rectangle((int)w.X, (int)w.Y, (int)w.Width, (int)w.Height).Contains(center))
                .ToList();
            if (displayOverlays.Count != 1)
                throw new InvalidOperationException($"Expected one selector overlay on display {display.Index}.");
            var nativeBounds = new Rectangle(
                (int)displayOverlays[0].X,
                (int)displayOverlays[0].Y,
                (int)displayOverlays[0].Width,
                (int)displayOverlays[0].Height);
            if (Math.Abs(nativeBounds.X - display.Bounds.X) > 1 ||
                Math.Abs(nativeBounds.Y - display.Bounds.Y) > 1 ||
                Math.Abs(nativeBounds.Width - display.Bounds.Width) > 1 ||
                Math.Abs(nativeBounds.Height - display.Bounds.Height) > 1)
                throw new InvalidOperationException(
                    $"Display {display.Index} overlay {nativeBounds} does not include the full display {display.Bounds}.");
        }
        Console.WriteLine($"PASS: {overlays.Count} selector overlays cover every full display, including menu-bar bounds.");
    }
    foreach (var overlay in overlays)
    {
    Console.WriteLine($"Picker native bounds: {overlay.X},{overlay.Y},{overlay.Width},{overlay.Height}");
    foreach (var screen in MacOSAPI.GetScreens())
        Console.WriteLine($"Display bounds: {screen.Bounds}; scale={screen.ScaleFactor}");
    using var captured = await new macOSCapture().CaptureWindow(new SnapX.Core.Media.WindowInfo { Handle = (nint)overlay.Hwnd })
        ?? throw new InvalidOperationException("The picker window could not be captured.");
    using var rgba = captured.CloneAs<Rgba32>();
    long transparent = 0, translucent = 0, opaque = 0, visibleContent = 0;
    rgba.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            foreach (var pixel in accessor.GetRowSpan(y))
            {
                if (pixel.A == 0) transparent++;
                else if (pixel.A == 255) opaque++;
                else translucent++;
                if (pixel.A != 0 && (pixel.R > 8 || pixel.G > 8 || pixel.B > 8)) visibleContent++;
            }
        }
    });
    double transparentFraction = (double)transparent / ((long)rgba.Width * rgba.Height);
    double opaqueFraction = (double)opaque / ((long)rgba.Width * rgba.Height);
    double contentFraction = (double)visibleContent / ((long)rgba.Width * rgba.Height);
    Console.WriteLine($"Overlay {rgba.Width}x{rgba.Height}: transparent={transparent:N0}, translucent={translucent:N0}, opaque={opaque:N0}; transparent={transparentFraction:P2}; opaque={opaqueFraction:P2}.");
    bool expectTransparent = args.Contains("--expect-transparent");
    // Opt in only with a visibly nonblack desktop fixture. A legitimate
    // black desktop is valid product input, but alpha alone cannot detect a
    // broken frozen preview that only displays the native black clear color.
    if (args.Contains("--expect-content") && contentFraction < 0.01)
    {
        Console.Error.WriteLine($"FAIL: The screenshot selector contains only {contentFraction:P2} nonblack pixels; expected the visible desktop fixture.");
        return 1;
    }
    if (args.Contains("--expect-content"))
        Console.WriteLine($"PASS: Frozen selector renders desktop content ({contentFraction:P2} nonblack pixels).");
    if (expectTransparent ? transparentFraction < 0.90 : opaqueFraction < 0.90)
    {
        Console.Error.WriteLine(expectTransparent
            ? "FAIL: The recording-region geometry picker is not predominantly transparent."
            : "FAIL: The screenshot region picker is not predominantly opaque.");
        return 1;
    }
    Console.WriteLine(expectTransparent
        ? "PASS: Recording-region geometry picker retains a transparent desktop interior."
        : "PASS: Screenshot region picker presents a frozen opaque display frame.");
    }
    return 0;
}

int failures = 0;
void Check(string name, Action action)
{
    try { action(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL: {name}: {ex}"); }
}
var api = new MacOSAPI();
Check("Linux notification service is unavailable without throwing on macOS", () =>
{
    var notifications = new SnapX.Core.DesktopNotificationService();
    try
    {
        if (notifications.IsAvailable || notifications.NotifyAsync("Smoke test", "No notification should be sent").Result != 0)
            throw new InvalidOperationException("Unexpected Linux notification availability on macOS.");
        notifications.CloseNotificationAsync(1).GetAwaiter().GetResult();
    }
    finally { notifications.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
});

Check("CoreGraphics cursor position and native lifetime", () =>
{
    for (int i = 0; i < 100; i++) api.GetCursorPosition();
});
if (!args.Contains("--clipboard-only"))
    Check("window enumeration and bounds overload dispatch", () =>
{
    var windows = api.GetWindowList();
    if (windows.Count == 0) throw new InvalidOperationException("No native windows returned.");
    foreach (var window in windows.Take(20))
    {
        var direct = api.GetWindowRectangle(window.Handle);
        var indirect = api.GetWindowRectangle(window);
        if (direct.Width <= 0 || direct.Height <= 0) throw new InvalidOperationException("Window bounds are empty.");
        if (direct != indirect) throw new InvalidOperationException("Window bounds overloads disagree.");
    }
    Console.WriteLine($"Enumerated {windows.Count} windows.");
});
else
    Console.WriteLine("SKIP: WindowServer probes in clipboard-only mode.");

if (args.Contains("--clipboard"))
{
    var folder = Path.Combine(Path.GetTempPath(), $"snapx-clipboard-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(folder);
    var backup = Path.Combine(folder, "clipboard.plist");
    string helper = Path.Combine(folder, "pasteboard");
    bool saved = false;
    try
    {
        Run("/usr/bin/clang", "-framework", "AppKit", Path.Combine(AppContext.BaseDirectory, "Pasteboard.m"), "-o", helper);
        Run(helper, "save", backup);
        saved = true;
        Check("clipboard text escaping and Unicode roundtrip (128 seeded cases)", () =>
        {
            var random = new Random(0x5A17);
            string[] fragments = ["\"", "'", "\\", "\n", "\r\n", "\t", "$()", "`", "é", "漢字", "😀", " "];
            for (int i = 0; i < 128; i++)
            {
                var text = string.Concat(Enumerable.Range(0, random.Next(0, 128)).Select(_ => fragments[random.Next(fragments.Length)]));
                api.CopyText(text);
                var actual = Run("/usr/bin/pbpaste");
                if (actual != text) throw new InvalidOperationException($"Roundtrip mismatch at case {i}.");
            }
        });
        Check("clipboard PNG retains pixels and transparency", () =>
        {
            using var source = new Image<Rgba32>(2, 2);
            source[0, 0] = new Rgba32(37, 83, 149, 127);
            api.CopyImage(source, "quotes\"\\and arbitrary names.png");
            var png = Path.Combine(folder, "clipboard.png");
            Run(helper, "png", png);
            using var actual = Image.Load<Rgba32>(png);
            if (actual[0, 0] != source[0, 0]) throw new InvalidOperationException("PNG pixels changed.");
        });
    }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL: clipboard setup: {ex}"); }
    finally
    {
        if (saved)
        {
            Run(helper, "restore", backup);
            Console.WriteLine("Restored original clipboard representations.");
        }
        Directory.Delete(folder, true);
    }
}
else Console.WriteLine("SKIP: clipboard probes (enable with --clipboard; original clipboard is restored).");

if (args.Contains("--clipboard-only")) return failures == 0 ? 0 : 1;

Check("monitor enumeration and lookup overloads", () =>
{
    var capture = new macOSCapture();
    foreach (var screen in MacOSAPI.GetScreens())
    {
        var point = screen.Bounds.Location;
        if (api.GetScreen(point)?.Bounds != screen.Bounds ||
            capture.GetScreen(point).Result != screen.Bounds ||
            capture.GetScreen(screen.Name).Result != screen.Bounds)
            throw new InvalidOperationException("Screen lookup overloads disagree.");
    }
});

if (Native.CGPreflightScreenCaptureAccess())
{
    Check("native fullscreen screen capture and image decode", () =>
    {
        using var image = new macOSCapture().CaptureFullscreen().GetAwaiter().GetResult();
        if (image is null || image.Width < 1 || image.Height < 1)
            throw new InvalidOperationException("Capture produced no image.");
        Console.WriteLine($"Captured {image.Width}x{image.Height} pixels; image was not saved.");
        var screen = MacOSAPI.GetScreens().First(s => s.IsPrimary);
        var capture = new macOSCapture();
        using var monitor = capture.CaptureScreen(screen).GetAwaiter().GetResult();
        if (monitor is null) throw new InvalidOperationException("Monitor capture returned null.");
        using var region = capture.CaptureRectangle(new Rectangle(screen.Bounds.X, screen.Bounds.Y, 32, 24)).GetAwaiter().GetResult();
        if (region is null || region.Width < 32 || region.Height < 24)
            throw new InvalidOperationException("Region capture returned incorrect dimensions.");
        Console.WriteLine($"Monitor {monitor.Width}x{monitor.Height}; 32x24-point region {region.Width}x{region.Height} pixels.");
        using var byName = capture.CaptureScreen(screen.Name).GetAwaiter().GetResult();
        if (byName is null || byName.Size != monitor.Size)
            throw new InvalidOperationException("Named screen capture disagrees with Screen overload.");
        var window = api.GetWindowList().First(w => w.IsVisible && w.ProcessName.Contains("SnapX", StringComparison.OrdinalIgnoreCase) && w.Rectangle.Width > 100 && w.Rectangle.Height > 100);
        using var windowImage = capture.CaptureWindow(window).GetAwaiter().GetResult();
        if (windowImage is null || windowImage.Width < 1 || windowImage.Height < 1)
            throw new InvalidOperationException("Window capture returned no pixels.");
        Console.WriteLine($"Window capture {windowImage.Width}x{windowImage.Height} pixels.");
    });
}
else
{
    Console.WriteLine("BLOCKED: screen recording permission has not been granted to this host; capture was not attempted.");
    if (args.Contains("--require-capture")) failures++;
}
return failures == 0 ? 0 : 1;

static string Run(string command, params string[] arguments)
{
    var info = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    info.Environment["LC_CTYPE"] = "UTF-8";
    info.Environment["CLANG_MODULE_CACHE_PATH"] = Path.Combine(Path.GetTempPath(), "snapx-swift-module-cache");
    using var process = Process.Start(info) ?? throw new IOException($"Unable to run {command}");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException(command); }
    Task.WaitAll(output, error);
    if (process.ExitCode != 0) throw new IOException($"{command} exited {process.ExitCode}: {error.Result}");
    return output.Result;
}
static class Native
{
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CGPreflightScreenCaptureAccess();

    [DllImport("snapx-tray", EntryPoint = "snapx_tray_classify_event")]
    internal static extern int SnapXTrayClassifyEvent(ulong eventType, ulong modifierFlags);
}
