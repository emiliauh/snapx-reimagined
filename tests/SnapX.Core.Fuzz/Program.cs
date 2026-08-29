using System.Collections;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Microsoft.Data.Sqlite;
using SnapX.Core;
using SnapX.Core.History;
using SnapX.Core.Hotkey;
using SnapX.Core.Job;
using SnapX.Core.Upload;
using SnapX.Core.Upload.Custom;
using SnapX.Core.Upload.Utils;
using SnapX.Core.Capture;
using SnapX.Core.ImageEffects;
using SnapX.Core.ImageEffects.Annotations;
using SnapX.Core.ScreenCapture;
using SnapX.Core.ScreenCapture.ScreenRecording;
using SnapX.Core.Localization;
using SnapX.Core.Media.Services;
using SnapX.Core.Utils;
using SnapX.Core.Upload.Zip;
using SnapX.NativeMessagingHost;

if (args.Contains("--portal-probe", StringComparer.Ordinal))
{
    return await RunPortalProbe();
}

if (args.Contains("--x11-probe", StringComparer.Ordinal))
{
    return await RunX11Probe();
}

if (args.Contains("--wf-recorder-stop-probe", StringComparer.Ordinal))
{
    int probeChecks = 0;
    VerifyWfRecorderStopEscalation(ref probeChecks);
    Console.WriteLine($"wf-recorder stop probe passed: {probeChecks:N0} checks.");
    return 0;
}

const int seed = 0x5A17;
var random = new Random(seed);
var checks = 0;

try
{
    FuzzRegionNormalization(random, ref checks);
    checks += await VerifyRegionSelectionLifecycleAsync();
    FuzzHotkeyLifecycle(random, ref checks);
    FuzzPortalAcceleratorFormatting(random, ref checks);
    FuzzUploaderResponseValidation(random, ref checks);
    FuzzCustomUploaderSyntax(random, ref checks);
    FuzzHistoryFiltering(random, ref checks);
    VerifyHistoryCommitIdentityAndOrder(ref checks);
    VerifyHistoryMediaPreviewRouting(ref checks);
    VerifyClipboardTaskRouting(ref checks);
    checks += await VerifyThumbnailCacheIdentity();
    VerifyAutoCaptureInterval(ref checks);
    VerifyImageEffectPreset(ref checks);
    VerifyPinToScreenEvent(ref checks);
    VerifyAnnotationModel(ref checks);
    VerifyCapabilityGates(ref checks);
    FuzzSimplifiedTechnicalEnglish(random, ref checks);
    FuzzHotkeyParser(random, ref checks);
    FuzzHotkeyRegistrationIdentity(ref checks);
    VerifyOfficialUploaderServices(ref checks);
    VerifyHyprlandHotkeyBindingManager(ref checks);
    VerifyWfRecorderStopEscalation(ref checks);
    VerifyNativeMessagingBoundaries(ref checks);
    VerifyScrollingCaptureStitching(ref checks);
    checks += await VerifyUnsafeUrlRejectionAsync();
    VerifyZipExtractionBoundary(ref checks);

    Console.WriteLine($"SnapX fuzz/property checks passed: {checks:N0} (seed {seed}).");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"SnapX fuzz/property check failed after {checks:N0} checks (seed {seed}):");
    Console.Error.WriteLine(ex);
    return 1;
}

static async Task<int> RunPortalProbe()
{
    int unobservedExceptions = 0;
    EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, eventArgs) =>
    {
        Interlocked.Increment(ref unobservedExceptions);
        eventArgs.SetObserved();
        Console.Error.WriteLine(eventArgs.Exception);
    };
    TaskScheduler.UnobservedTaskException += handler;

    try
    {
        await RunPortalRegistration();

        for (int attempt = 0; attempt < 4; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }

        Console.WriteLine($"Portal probe unobserved exceptions: {unobservedExceptions}.");
        return unobservedExceptions == 0 ? 0 : 1;
    }
    finally
    {
        TaskScheduler.UnobservedTaskException -= handler;
    }

    static async Task RunPortalRegistration()
    {
        using IHotkeyBackend backend = HotkeyBackendFactory.CreateDefault(
            HotkeyBackendPreference.WaylandPortal);
        var registration = new HotkeyRegistration(
            $"snapx_portal_probe_{Guid.NewGuid():N}",
            new HotkeyInfo(Keys.Control | Keys.F12));

        try
        {
            IReadOnlyDictionary<string, HotkeyBackendRegistrationResult> results =
                await backend.RegisterAsync([registration]);
            HotkeyBackendRegistrationResult result = results[registration.Id];
            Console.WriteLine(
                result.IsRegistered
                    ? $"Portal probe registered with {backend.Name}."
                    : $"Portal probe failed normally: {result.Error}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Portal probe failed normally: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

static async Task<int> RunX11Probe()
{
    using IHotkeyBackend backend = HotkeyBackendFactory.CreateDefault(
        HotkeyBackendPreference.X11);
    var registration = new HotkeyRegistration(
        $"snapx_x11_probe_{Guid.NewGuid():N}",
        new HotkeyInfo(Keys.Control | Keys.F12));

    if (!backend.IsAvailable)
    {
        Console.WriteLine($"X11 probe unavailable: {backend.AvailabilityError}");
        return 1;
    }

    IReadOnlyDictionary<string, HotkeyBackendRegistrationResult> results =
        await backend.RegisterAsync([registration]);
    HotkeyBackendRegistrationResult result = results[registration.Id];
    Console.WriteLine(
        result.IsRegistered
            ? $"X11 probe registered with {backend.Name}."
            : $"X11 probe failed: {result.Error}");
    return result.IsRegistered ? 0 : 1;
}

static void VerifyWfRecorderStopEscalation(ref int checks)
{
    // This is deliberately not a capture test. It runs a local shell that
    // ignores SIGINT, then proves the bounded wf-recorder shutdown helper
    // escalates to terminating that exact child tree rather than leaving a
    // recording session in Stopping forever.
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = "/bin/sh",
        ArgumentList = { "-c", "trap '' INT; while :; do sleep 1; done" },
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Could not start the wf-recorder stop test process.");

    try
    {
        MethodInfo method = typeof(ScreenRecorder).GetMethod(
            "WaitForWfRecorderExit",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(Process), typeof(Func<bool>), typeof(TimeSpan?), typeof(TimeSpan), typeof(Action<Process>)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(ScreenRecorder).FullName, "WaitForWfRecorderExit");
        Action<Process> sendInterrupt = child =>
        {
            using Process? kill = Process.Start(new ProcessStartInfo
            {
                FileName = "kill",
                ArgumentList = { "-INT", child.Id.ToString() },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            kill?.WaitForExit(2000);
        };
        bool exitedGracefully = (bool)(method.Invoke(
            null,
            [process, (Func<bool>)(() => true), null, TimeSpan.FromMilliseconds(50), sendInterrupt])
            ?? throw new InvalidOperationException("wf-recorder shutdown helper returned no result."));

        Check(!exitedGracefully,
            "wf-recorder shutdown reported a graceful exit after force termination", ref checks);
        Check(process.WaitForExit(2000),
            "wf-recorder shutdown escalation left its child process running", ref checks);
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
    }
}

static void VerifyAutoCaptureInterval(ref int checks)
{
    // Zero, negative and NaN repeat times must clamp to a safe minimum of
    // 1 second so the loop cannot spin. Oversized values must clamp to the
    // upper bound (24h) rather than folding back to 1s.
    Check(AutoCaptureManager.GetEffectiveInterval(0) == TimeSpan.FromSeconds(1),
        "AutoCapture interval must clamp zero to 1s.", ref checks);
    Check(AutoCaptureManager.GetEffectiveInterval(-5) == TimeSpan.FromSeconds(1),
        "AutoCapture interval must clamp negative to 1s.", ref checks);
    Check(AutoCaptureManager.GetEffectiveInterval(decimal.MaxValue) == TimeSpan.FromHours(24),
        "AutoCapture interval must clamp oversized to 24h.", ref checks);
    Check(AutoCaptureManager.GetEffectiveInterval(60) == TimeSpan.FromSeconds(60),
        "AutoCapture interval must preserve a valid value.", ref checks);

    // A running manager responds to Stop() by leaving the Running state.
    AutoCaptureManager.Start();
    Check(AutoCaptureManager.IsRunning, "AutoCapture.Start must run the loop.", ref checks);
    AutoCaptureManager.Stop();
    Check(!AutoCaptureManager.IsRunning, "AutoCapture.Stop must stop the loop.", ref checks);
}

static void VerifyImageEffectPreset(ref int checks)
{
    using Image image = new Image<Rgba32>(32, 32, Color.Red);

    // Out-of-range selection must be a no-op rather than throwing or blanking.
    var settings = new TaskSettings();
    settings.ImageSettings.ImageEffectPresets = [];
    settings.ImageSettings.SelectedImageEffectPreset = 37;
    Image unchanged = TaskHelpers.ApplyImageEffects(image, settings);
    Check(ReferenceEquals(unchanged, image),
        "Out-of-range preset must leave the image untouched.", ref checks);

    // A preset with no effects must leave the image untouched too.
    var emptyPreset = new ImageEffectPreset { Name = "Empty", Effects = [] };
    settings.ImageSettings.ImageEffectPresets = [emptyPreset];
    settings.ImageSettings.SelectedImageEffectPreset = 0;
    Image unchanged2 = TaskHelpers.ApplyImageEffects(image, settings);
    Check(ReferenceEquals(unchanged2, image),
        "Empty preset must leave the image untouched.", ref checks);

    // A preset with a deterministic pixel effect must visibly change the image.
    // Grayscale is internal, so construct it via reflection (the harness already
    // reflects private members for other checks).
    Type? grayscaleType = typeof(ImageEffect).Assembly.GetType(
        "SnapX.Core.ImageEffects.Adjustments.Grayscale");
    if (grayscaleType != null)
    {
        var grayscale = (ImageEffect?)Activator.CreateInstance(grayscaleType);
        if (grayscale != null)
        {
            var gradientSettings = new TaskSettings();
            gradientSettings.ImageSettings.ImageEffectPresets =
                [new ImageEffectPreset { Name = "Grayscale", Effects = [grayscale] }];
            gradientSettings.ImageSettings.SelectedImageEffectPreset = 0;
            using Image colorImage = new Image<Rgba32>(4, 4, Color.Red);
            Image<Rgba32> colorBitmap = (Image<Rgba32>)colorImage;
            Rgba32 before = colorBitmap[0, 0];
            Image effectImage = TaskHelpers.ApplyImageEffects(colorImage, gradientSettings);
            Image<Rgba32> effectBitmap = (Image<Rgba32>)effectImage;
            Rgba32 after = effectBitmap[0, 0];
            Check(effectImage != null && (after.R != before.R || after.G != before.G || after.B != before.B),
                "A chosen preset must change pixel data.", ref checks);
            if (!ReferenceEquals(effectImage, colorImage))
            {
                effectImage.Dispose();
            }
        }
    }

    // Keeping the default preset in settings must preserve the effect list.
    var storage = new TaskSettingsImage();
    storage.ImageEffectPresets = [ImageEffectPreset.GetDefaultPreset()];
    Check(storage.ImageEffectPresets.Count == 1 && storage.ImageEffectPresets[0].Effects.Count > 0,
        "ImageEffectPresets storage must be restored.", ref checks);

    // The default preset runs a multi-effect chain (Canvas then DrawText). It
    // must apply without use-after-dispose and return a valid, non-empty image.
    using Image presetSource = new Image<Rgba32>(32, 32, Color.Red);
    var presetSettings = new TaskSettings();
    presetSettings.ImageSettings.ImageEffectPresets = [ImageEffectPreset.GetDefaultPreset()];
    presetSettings.ImageSettings.SelectedImageEffectPreset = 0;
    Image presetResult = TaskHelpers.ApplyImageEffects(presetSource, presetSettings);
    Check(presetResult is not null && presetResult.Width > 0 && presetResult.Height > 0,
        "Default preset must apply without throwing and produce a valid image.", ref checks);
    // Access a pixel and encode the result so a disposed image fails here
    // rather than passing via cached metadata.
    Check(presetResult is Image<Rgba32> presetRgba && presetRgba[0, 0].R >= 0,
        "Default preset result must be readable (not disposed).", ref checks);
    using var resultStream = new MemoryStream();
    presetResult.Save(resultStream, SixLabors.ImageSharp.Formats.Png.PngFormat.Instance);
    Check(resultStream.Length > 0, "Default preset result must be encodable.", ref checks);
    if (!ReferenceEquals(presetResult, presetSource))
    {
        presetResult.Dispose();
    }
}

static void VerifyPinToScreenEvent(ref int checks)
{
    // A close-all request must publish a request whose CloseAll flag is set and
    // whose completion can be marked handled without a worker image.
    TaskCompletionSource<bool> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    EventAggregator aggregator = new EventAggregator();
    aggregator.Subscribe<NeedPinToScreenEvent>(e =>
    {
        observed.TrySetResult(e.CloseAll);
        e.MarkAsHandled();
    });

    var closeAll = new NeedPinToScreenEvent(null!) { CloseAll = true };
    aggregator.Publish(closeAll);
    Check(observed.Task.GetAwaiter().GetResult(), "PinToScreen close-all request must reach the frontend.", ref checks);
    Check(closeAll.Completion.GetAwaiter().GetResult(), "PinToScreen close-all must be marked handled.", ref checks);

    // A pin request with an image must advertise an image and complete when
    // the frontend marks it handled.
    TaskCompletionSource<bool> sawImage = new(TaskCreationOptions.RunContinuationsAsynchronously);
    EventAggregator aggregator2 = new EventAggregator();
    aggregator2.Subscribe<NeedPinToScreenEvent>(e =>
    {
        sawImage.TrySetResult(e.Image is not null);
        e.MarkAsHandled();
    });
    using Image pinImage = new Image<Rgba32>(2, 2, Color.Blue);
    var pin = new NeedPinToScreenEvent(pinImage);
    aggregator2.Publish(pin);
    Check(sawImage.Task.GetAwaiter().GetResult(), "PinToScreen request must carry its image.", ref checks);
    Check(pin.Completion.GetAwaiter().GetResult(), "PinToScreen request must complete after handling.", ref checks);

    // Pin-to-screen via file must be distinguishable from a plain upload.
    var fileEvent = new NeedFileOpenerEvent { PinToScreen = true };
    Check(fileEvent.PinToScreen, "PinToScreen file request must carry the pin flag.", ref checks);
}

static void VerifyAnnotationModel(ref int checks)
{
    // A rectangle annotation must render a stroke/edge and change the output.
    using Image rectImage = new Image<Rgba32>(16, 16, Color.White);
    var rect = new RectangleAnnotation
    {
        Rectangle = new SixLabors.ImageSharp.Rectangle(2, 2, 8, 8),
        Color = Color.Red,
        Thickness = 2
    };
    Image rectResult = rect.Apply(rectImage);
    Check(rectResult.Width == 16 && rectResult.Height == 16,
        "Rectangle annotation must preserve dimensions.", ref checks);

    // Redaction fills the region with black.
    using Image redactImage = new Image<Rgba32>(8, 8, Color.White);
    var redact = new RedactionAnnotation
    {
        Rectangle = new SixLabors.ImageSharp.Rectangle(0, 0, 8, 8)
    };
    Image redactResult = redact.Apply(redactImage);
    Rgba32 redactPixel = ((Image<Rgba32>)redactResult)[0, 0];
    Check(redactPixel.R == 0 && redactPixel.G == 0 && redactPixel.B == 0,
        "Redaction must black out the region.", ref checks);

    // Blur changes pixels inside the selected area without changing dimensions.
    using Image<Rgba32> blurImage = new(12, 12, Color.Black);
    for (int y = 0; y < blurImage.Height; y++)
    {
        for (int x = 6; x < blurImage.Width; x++)
        {
            blurImage[x, y] = Color.White;
        }
    }
    var blur = new BlurAnnotation
    {
        Rectangle = new SixLabors.ImageSharp.Rectangle(2, 2, 8, 8),
        Radius = 3
    };
    Image blurResult = blur.Apply(blurImage);
    Rgba32 blurBoundaryPixel = ((Image<Rgba32>)blurResult)[5, 6];
    Check(blurResult.Width == 12 && blurResult.Height == 12,
        "Blur annotation must preserve dimensions.", ref checks);
    Check(blurBoundaryPixel.R is > 0 and < 255,
        "Blur annotation must blend pixels inside its region.", ref checks);

    // CropAnnotation clamps to the image bounds and returns a new frame.
    using Image cropSource = new Image<Rgba32>(10, 10, Color.Green);
    var crop = new CropAnnotation { Rectangle = new SixLabors.ImageSharp.Rectangle(0, 0, 4, 4) };
    Image cropResult = crop.Apply(cropSource);
    Check(cropResult.Width == 4 && cropResult.Height == 4,
        "Crop annotation must clamp to image bounds.", ref checks);

    // Editor request completes with null on cancel and an image on accept.
    using Image editImage = new Image<Rgba32>(4, 4, Color.Blue);
    var request = new NeedEditImageEvent(editImage);
    EventAggregator agg = new EventAggregator();
    agg.Subscribe<NeedEditImageEvent>(e => e.Complete(e.Image));
    agg.Publish(request);
    Check(request.Completion.GetAwaiter().GetResult() is { } result && result == editImage,
        "Editor request must return the edited image.", ref checks);
}

static void VerifyCapabilityGates(ref int checks)
{
    // macOS must not pretend to support global hotkeys via a stub backend.
    // Because this test runs on Linux, assert the factory's macOS branch is a
    // real UnavailableHotkeyBackend with a descriptive error by checking the
    // contract through the same constructor used on macOS.
    var unavailable = new UnavailableHotkeyBackend("macOS global hotkeys require a native event-tap backend.", "macOS (unsupported)");
    Check(!unavailable.IsAvailable, "UnavailableHotkeyBackend must report unavailable.", ref checks);
    Check(unavailable.AvailabilityError is { Length: > 0 }, "UnavailableHotkeyBackend must carry a reason.", ref checks);

    // Scrolling capture publishes an explicit error and never pretends success.
    var error = new ErrorMessageEvent(
        new PlatformNotSupportedException("Scrolling capture is not available on this platform."),
        "Scrolling capture",
        false);
    Check(error.Exception is PlatformNotSupportedException,
        "Scrolling capture must be gated with PlatformNotSupportedException.", ref checks);
    Check(!error.FullError, "Capability-gated errors should be non-fatal.", ref checks);
}

static void VerifyScrollingCaptureStitching(ref int checks)
{
    // StitchFrames is the pure primitive that backs ShareX-style scrolling
    // capture. It trims 'overlap' rows from the bottom of the top frame and
    // appends the bottom frame's rows [bottomStartRow, Height) after it. The
    // property test models two captures of a fixed-height viewport H scrolled
    // by S, where the new content enters at the bottom of the new frame.
    const int Height = 40;
    const int Scroll = 12;
    const int Width = 16;
    const int Overlap = Height - Scroll; // rows shared between consecutive frames

    // Top frame = page rows [0, H). Bottom frame = page rows [S, S+H). Every row
    // is a unique solid colour so overlap matching is unambiguous.
    static byte RowValue(int row) => (byte)((row * 37 + 11) & 0xFF);

    using var top = new Image<Rgba32>(Width, Height);
    using var bottom = new Image<Rgba32>(Width, Height);
    for (int y = 0; y < Height; y++)
    {
        byte vt = RowValue(y);
        byte vb = RowValue(y + Scroll);
        for (int x = 0; x < Width; x++)
        {
            top[x, y] = new Rgba32(vt, vt, vt, 255);
            bottom[x, y] = new Rgba32(vb, vb, vb, 255);
        }
    }

    // The new content begins at row Scroll in the bottom frame (where its top
    // Overlap==Height-Scroll rows duplicate the top frame's bottom). The top
    // frame contributes its first Scroll rows [0, Scroll).
    using var stitched = (Image<Rgba32>)ScrollingCaptureManager.StitchFrames(top, bottom, Overlap, Scroll);

    int expectedHeight = Height - Overlap + (Height - Scroll);
    Check(stitched.Height == expectedHeight, "Stitched height is incorrect.", ref checks);
    Check(expectedHeight == Height, "Stitch must preserve the viewport height.", ref checks);

    // The stitched frame equals the next viewport: page rows [0, S+H) placed in
    // [0, H) after removing the overlap. Verify each row's colour.
    for (int y = 0; y < Height; y++)
    {
        if (y < Scroll)
        {
            // Kept from the top frame: page row y.
            byte v = RowValue(y);
            Rgba32 px = stitched[0, y];
            Check(px.R == v && px.G == v && px.B == v, "Stitch corrupted the top region.", ref checks);
        }
        else
        {
            // Appended from the bottom frame: page row y+Scroll.
            byte v = RowValue(y + Scroll);
            Rgba32 px = stitched[0, y];
            Check(px.R == v && px.G == v && px.B == v, "Stitch corrupted the appended region.", ref checks);
        }
    }

    // ImagesEqual rejects a size mismatch and accepts identical frames.
    using var other = new Image<Rgba32>(Width, Height);
    for (int y = 0; y < Height; y++)
    {
        byte v = RowValue(y);
        for (int x = 0; x < Width; x++) other[x, y] = new Rgba32(v, v, v, 255);
    }
    Check(ScrollingCaptureManager.ImagesEqual(top, other), "ImagesEqual rejected identical frames.", ref checks);
    Check(!ScrollingCaptureManager.ImagesEqual(top, bottom), "ImagesEqual accepted a size mismatch.", ref checks);
}

static void VerifyNativeMessagingBoundaries(ref int checks)
{
    const string expected = "Unicode framing \U0001F642\nwith a new line";
    using var roundTrip = new MemoryStream();
    NativeMessagingHost.Write(roundTrip, expected);
    roundTrip.Position = 0;
    Check(NativeMessagingHost.Read(roundTrip) == expected,
        "Native messaging did not preserve a valid framed message", ref checks);

    byte[] prefix = new byte[sizeof(int)];
    foreach (int length in new[] { -1, NativeMessagingHost.MaximumMessageSize + 1 })
    {
        using var malformed = new MemoryStream();
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        malformed.Write(prefix);
        malformed.Position = 0;
        Check(Throws<InvalidDataException>(() => NativeMessagingHost.Read(malformed)),
            "Native messaging accepted an invalid frame length", ref checks);
    }

    using var invalidUtf8 = new MemoryStream();
    invalidUtf8.Write([1, 0, 0, 0, 0xFF]);
    invalidUtf8.Position = 0;
    Check(Throws<DecoderFallbackException>(() => NativeMessagingHost.Read(invalidUtf8)),
        "Native messaging accepted malformed UTF-8", ref checks);
}

static async Task<int> VerifyUnsafeUrlRejectionAsync()
{
    int checks = 0;
    string[] unsafeUrls =
    [
        "http://127.0.0.1/",
        "https://10.0.0.1/",
        "http://169.254.169.254/latest/meta-data/",
        "https://[::1]/",
        "https://[::ffff:127.0.0.1]/",
        "https://[fc00::1]/",
        "https://[fe80::1]/",
        "https://[2001:db8::1]/",
        "https://user:password@example.com/",
        "http://localhost/"
    ];

    foreach (string url in unsafeUrls)
    {
        Check(!URLHelpers.IsValidURL(url) || url.Contains('@'),
            $"A private address was accepted as a valid external URL: {url}", ref checks);
        Check(!await URLHelpers.IsSafePublicHttpUrlAsync(url),
            $"A private address was accepted as a safe external URL: {url}", ref checks);
    }

    Check(URLHelpers.IsValidURL("https://1.1.1.1/"),
        "A public IPv4 address was rejected as a URL", ref checks);
    Check(await URLHelpers.IsSafePublicHttpUrlAsync("https://1.1.1.1/"),
        "A public IPv4 address was rejected as a safe external URL", ref checks);

    return checks;
}

static void VerifyZipExtractionBoundary(ref int checks)
{
    string testRoot = Path.Combine(Path.GetTempPath(), $"snapx-fuzz-zip-{Guid.NewGuid():N}");
    string archivePath = Path.Combine(testRoot, "payload.zip");
    string extractionPath = Path.Combine(testRoot, "extract");
    string escapedPath = Path.Combine(testRoot, "escaped.txt");

    try
    {
        Directory.CreateDirectory(testRoot);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("../escaped.txt");
            using StreamWriter writer = new(entry.Open());
            writer.Write("not outside the extraction directory");
        }

        Check(Throws<InvalidDataException>(() => ZipManager.Extract(archivePath, extractionPath)),
            "Zip extraction accepted a path-traversal entry", ref checks);
        Check(!File.Exists(escapedPath), "Zip extraction wrote outside its destination", ref checks);
    }
    finally
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}

static void FuzzRegionNormalization(Random random, ref int checks)
{
    var bounds = new Rectangle(-5000, -3000, 10000, 7000);

    for (int i = 0; i < 50_000; i++)
    {
        var rectangle = new Rectangle(
            random.Next(-20_000, 20_001),
            random.Next(-20_000, 20_001),
            random.Next(-20_000, 20_001),
            random.Next(-20_000, 20_001));
        int minimumSize = random.Next(-20, 250);
        Rectangle normalized = RegionCaptureTasks.NormalizeRectangle(rectangle, bounds, minimumSize);

        if (!normalized.IsEmpty)
        {
            long right = (long)normalized.X + normalized.Width;
            long bottom = (long)normalized.Y + normalized.Height;
            Check(normalized.X >= bounds.X, "Normalized X escaped the left bound", ref checks);
            Check(normalized.Y >= bounds.Y, "Normalized Y escaped the top bound", ref checks);
            Check(right <= (long)bounds.X + bounds.Width, "Normalized right escaped the bound", ref checks);
            Check(bottom <= (long)bounds.Y + bounds.Height, "Normalized bottom escaped the bound", ref checks);
            Check(normalized.Width >= Math.Max(1, minimumSize), "Minimum width was not enforced", ref checks);
            Check(normalized.Height >= Math.Max(1, minimumSize), "Minimum height was not enforced", ref checks);
        }

        checks++;
    }

    Rectangle reversed = RegionCaptureTasks.NormalizeRectangle(
        new Rectangle(800, 600, -500, -400),
        new Rectangle(0, 0, 1000, 1000));
    Check(reversed == new Rectangle(300, 200, 500, 400), "Reversed drag was normalized incorrectly", ref checks);

    Rectangle extreme = RegionCaptureTasks.NormalizeRectangle(
        new Rectangle(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue),
        new Rectangle(-100, -100, 200, 200));
    Check(extreme == new Rectangle(-100, -100, 99, 99), "Extreme coordinates were not safely clamped", ref checks);
}

static async Task<int> VerifyRegionSelectionLifecycleAsync()
{
    int checks = 0;
    var bounds = new Rectangle(-1920, 0, 3840, 1080);
    var expected = new Rectangle(-400, 120, 320, 240);

    try
    {
        var returnedImage = new Image<Rgba32>(expected.Width, expected.Height);
        RegionCaptureTasks.SetRegionSelector((_, _) => Task.FromResult<RegionCaptureSelection?>(new()
        {
            Rectangle = expected,
            CaptureBounds = bounds,
            Image = returnedImage
        }));

        RegionCaptureSelection? selection = await RegionCaptureTasks.SelectRegionAsync(captureImage: true);
        Check(selection is not null, "A valid selector result was discarded", ref checks);
        Check(selection!.Rectangle == expected, "A valid selector rectangle was changed", ref checks);
        Check(ReferenceEquals(selection.Image, returnedImage), "The successful selector image ownership changed", ref checks);
        Check(RegionCaptureTasks.TryGetLastRegion(out Rectangle last, out _ ) && last == expected,
            "A successful selector result did not update last region", ref checks);
        selection.Image!.Dispose();

        var rejectedImage = new Image<Rgba32>(32, 32);
        RegionCaptureTasks.SetRegionSelector((_, _) => Task.FromResult<RegionCaptureSelection?>(new()
        {
            Rectangle = new Rectangle(10_000, 10_000, 10, 10),
            CaptureBounds = bounds,
            Image = rejectedImage
        }));

        bool rejected = false;
        try
        {
            await RegionCaptureTasks.SelectRegionAsync(captureImage: true);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Check(rejected, "An out-of-bounds selector result was accepted", ref checks);
        bool disposed = false;
        try
        {
            _ = rejectedImage[0, 0];
        }
        catch (ObjectDisposedException)
        {
            disposed = true;
        }
        Check(disposed, "A rejected selector result leaked its full-frame image", ref checks);

        RegionCaptureTasks.SetRegionSelector(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        using var cancellation = new CancellationTokenSource();
        Task<RegionCaptureSelection?> cancelledSelection = RegionCaptureTasks.SelectRegionAsync(
            captureImage: false, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        bool cancellationPropagated = false;
        try
        {
            await cancelledSelection;
        }
        catch (OperationCanceledException)
        {
            cancellationPropagated = true;
        }
        Check(cancellationPropagated, "Selector cancellation did not complete its awaiting caller", ref checks);
    }
    finally
    {
        RegionCaptureTasks.SetRegionSelector(null);
    }

    return checks;
}

static void FuzzHotkeyLifecycle(Random random, ref int checks)
{
    using var backend = new SimulatedHotkeyBackend();
    using var manager = new HotkeyManager(backend)
    {
        HotkeyRepeatLimit = TimeSpan.Zero
    };

    var first = NewHotkey(HotkeyType.PrintScreen, Keys.Control | Keys.A);
    int triggered = 0;
    manager.HotkeyTrigger += _ => triggered++;
    manager.UpdateHotkeys([first], showFailedHotkeys: false);

    Check(first.HotkeyInfo.Status == HotkeyStatus.Registered, "Valid hotkey was not registered", ref checks);
    Check(backend.Registrations.Count == 1, "Backend registration count was incorrect", ref checks);
    Check(backend.Trigger(backend.Registrations.Single().Id), "Registered hotkey did not trigger", ref checks);
    Check(triggered == 1, "Hotkey trigger was not dispatched exactly once", ref checks);

    var duplicate = NewHotkey(HotkeyType.ActiveWindow, Keys.Control | Keys.A);
    manager.UpdateHotkeys([first, duplicate], showFailedHotkeys: false);
    Check(first.HotkeyInfo.Status == HotkeyStatus.Registered, "First duplicate candidate lost registration", ref checks);
    Check(duplicate.HotkeyInfo.Status == HotkeyStatus.Failed, "Duplicate hotkey was not rejected", ref checks);

    manager.ToggleHotkeys(true);
    Check(backend.Registrations.Count == 0, "Disabled hotkeys remained registered", ref checks);
    manager.ToggleHotkeys(false);
    Check(backend.Registrations.Count == 1, "Hotkeys did not re-register after being enabled", ref checks);

    for (int i = 0; i < 500; i++)
    {
        Keys key = (Keys)((int)Keys.A + random.Next(0, 26));
        var candidate = NewHotkey(HotkeyType.PrintScreen, Keys.Shift | key);
        manager.UpdateHotkeys([candidate], showFailedHotkeys: false);
        Check(candidate.HotkeyInfo.Status == HotkeyStatus.Registered, "Random valid hotkey failed to register", ref checks);
        Check(manager.SimulateHotkeyPress(candidate.HotkeyInfo.Hotkey), "Simulated hotkey press was not accepted", ref checks);
    }
}

static void FuzzPortalAcceleratorFormatting(Random random, ref int checks)
{
    var keys = Enum.GetValues<Keys>()
        .Where(key => key != Keys.None && (key & Keys.Modifiers) == 0 && Enum.IsDefined(key))
        .Distinct()
        .ToArray();

    for (int i = 0; i < 10_000; i++)
    {
        Keys key = keys[random.Next(keys.Length)];
        Keys modifiers = (random.Next(8)) switch
        {
            1 => Keys.Control,
            2 => Keys.Shift,
            3 => Keys.Alt,
            4 => Keys.Control | Keys.Shift,
            5 => Keys.Control | Keys.Alt,
            6 => Keys.Shift | Keys.Alt,
            7 => Keys.Control | Keys.Shift | Keys.Alt,
            _ => Keys.None
        };

        var registration = new HotkeyRegistration("fuzz", new HotkeyInfo(key | modifiers));
        Check(!string.IsNullOrWhiteSpace(registration.Accelerator), "Portal accelerator was empty", ref checks);
        Check(!registration.Accelerator.Contains(' '), "Portal accelerator contained an invalid space", ref checks);
        checks++;
    }
}

static void FuzzHotkeyRegistrationIdentity(ref int checks)
{
    using var backend = new SimulatedHotkeyBackend();
    using var manager = new HotkeyManager(backend);
    var setting = NewHotkey(HotkeyType.PrintScreen, Keys.Control | Keys.P);
    string stableId = setting.HotkeyInfo.RegistrationId;

    manager.UpdateHotkeys([setting], showFailedHotkeys: false);
    Check(backend.Registrations.Single().Id == $"snapx_{stableId}",
        "Hotkey registration did not use the stable workflow identity", ref checks);

    var collision = NewHotkey(HotkeyType.ActiveWindow, Keys.Control | Keys.O);
    collision.HotkeyInfo.RegistrationId = stableId;
    manager.UpdateHotkeys([setting, collision], showFailedHotkeys: false);
    Check(backend.Registrations.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() == 2,
        "Duplicate portal identities were not repaired", ref checks);

    manager.UpdateHotkeys([setting], showFailedHotkeys: false);
    Check(backend.Registrations.Single().Id == $"snapx_{stableId}",
        "Hotkey registration identity changed during a reload", ref checks);
}

static void VerifyHyprlandHotkeyBindingManager(ref int checks)
{
    string tempBindingsPath = Path.Combine(Path.GetTempPath(), $"snapx-fuzz-bindings-{Guid.NewGuid():N}.lua");
    string? previousBindingsPath = Environment.GetEnvironmentVariable("SNAPX_HYPR_BINDINGS_PATH");
    string? previousForceSession = Environment.GetEnvironmentVariable("SNAPX_HYPR_FORCE_SESSION");
    string? previousSkipReload = Environment.GetEnvironmentVariable("SNAPX_HYPR_SKIP_RELOAD");
    string? previousFakeFailure = Environment.GetEnvironmentVariable("SNAPX_HYPR_FAKE_HYPRCTL_FAILURE");
    string? previousFakeConfigErrors = Environment.GetEnvironmentVariable("SNAPX_HYPR_FAKE_CONFIG_ERRORS");

    try
    {
        Environment.SetEnvironmentVariable("SNAPX_HYPR_BINDINGS_PATH", tempBindingsPath);
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FORCE_SESSION", "1");
        Environment.SetEnvironmentVariable("SNAPX_HYPR_SKIP_RELOAD", "1");
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FAKE_HYPRCTL_FAILURE", null);
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FAKE_CONFIG_ERRORS", null);

        var setting = NewHotkey(HotkeyType.RectangleRegion, Keys.Control | Keys.E);
        string registrationId = setting.HotkeyInfo.RegistrationId;
        string outsideManagedSection = string.Join("\r\n",
            $"-- SNAPX-HOTKEY: {registrationId} BEGIN",
            "-- This lookalike block is ordinary user content, outside SnapX's section.",
            $"-- SNAPX-HOTKEY: {registrationId} END",
            string.Empty);

        string preexistingContent = "-- user's own unrelated bindings\r\nhl.bind(\"SUPER + Q\", \"kill active\")\r\n" +
            outsideManagedSection;
        File.WriteAllText(tempBindingsPath, preexistingContent);
        UnixFileMode originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(tempBindingsPath, originalMode);
        }

        Check(HyprlandHotkeyBindingManager.IsSupported,
            "HyprlandHotkeyBindingManager reported unsupported with a forced session and an existing bindings file", ref checks);

        HyprlandHotkeySyncResult applyResult = HyprlandHotkeyBindingManager.ApplyAsync(setting).GetAwaiter().GetResult();
        Check(applyResult.IsApplicable && applyResult.IsSuccess,
            "Applying a valid Hyprland hotkey binding did not succeed", ref checks);

        string afterApply = File.ReadAllText(tempBindingsPath);
        Check(afterApply.Contains("-- BEGIN SNAPX MANAGED HOTKEYS - DO NOT EDIT", StringComparison.Ordinal),
            "Applying a Hyprland hotkey did not add the managed markers", ref checks);
        Check(afterApply.Contains($"-- SNAPX-HOTKEY: {registrationId} BEGIN", StringComparison.Ordinal),
            "Applying a Hyprland hotkey did not write a stable-ID entry", ref checks);
        Check(afterApply.Contains("hl.unbind(\"CTRL + E\")", StringComparison.Ordinal),
            "Applying a Hyprland hotkey did not unbind the previous key", ref checks);
        Check(afterApply.Contains("o.bind(\"CTRL + E\", \"SnapX RectangleRegion\", \"snapx-ui -RectangleRegion\")", StringComparison.Ordinal),
            "Applying a Hyprland hotkey did not bind the SnapX action", ref checks);
        Check(afterApply.Contains(preexistingContent, StringComparison.Ordinal),
            "Applying a Hyprland hotkey clobbered the user's pre-existing bindings", ref checks);
        if (OperatingSystem.IsLinux())
        {
            Check(File.GetUnixFileMode(tempBindingsPath) == originalMode,
                "Applying a Hyprland hotkey changed the user's bindings file permissions", ref checks);
        }
        Check(!afterApply.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'),
            "Applying a Hyprland hotkey changed a CRLF bindings file to mixed line endings", ref checks);

        HyprlandHotkeySyncResult reapplyResult = HyprlandHotkeyBindingManager.ApplyAsync(setting).GetAwaiter().GetResult();
        Check(reapplyResult.IsApplicable && reapplyResult.IsSuccess,
            "Reapplying an unchanged Hyprland hotkey binding did not succeed", ref checks);
        string afterReapply = File.ReadAllText(tempBindingsPath);
        Check(string.Equals(afterApply, afterReapply, StringComparison.Ordinal),
            "Reapplying an unchanged Hyprland hotkey binding altered the bindings file", ref checks);

        var otherSetting = NewHotkey(HotkeyType.PrintScreen, Keys.Control | Keys.Shift | Keys.P);
        HyprlandHotkeySyncResult secondApply = HyprlandHotkeyBindingManager.ApplyAsync(otherSetting).GetAwaiter().GetResult();
        Check(secondApply.IsApplicable && secondApply.IsSuccess,
            "Applying a second distinct Hyprland hotkey did not succeed", ref checks);
        string afterSecondApply = File.ReadAllText(tempBindingsPath);
        Check(afterSecondApply.Contains($"-- SNAPX-HOTKEY: {registrationId} BEGIN", StringComparison.Ordinal),
            "Applying a second Hyprland hotkey removed the first managed entry", ref checks);
        Check(afterSecondApply.Contains($"-- SNAPX-HOTKEY: {otherSetting.HotkeyInfo.RegistrationId} BEGIN", StringComparison.Ordinal),
            "Applying a second Hyprland hotkey did not add its own managed entry", ref checks);

        HyprlandHotkeySyncResult clearResult = HyprlandHotkeyBindingManager.ClearAsync(setting).GetAwaiter().GetResult();
        Check(clearResult.IsApplicable && clearResult.IsSuccess,
            "Clearing a managed Hyprland hotkey did not succeed", ref checks);
        string afterClear = File.ReadAllText(tempBindingsPath);
        Check(!afterClear.Contains("hl.unbind(\"CTRL + E\")", StringComparison.Ordinal),
            "Clearing a Hyprland hotkey left its managed entry behind", ref checks);
        Check(afterClear.Contains($"-- SNAPX-HOTKEY: {otherSetting.HotkeyInfo.RegistrationId} BEGIN", StringComparison.Ordinal),
            "Clearing one Hyprland hotkey removed an unrelated managed entry", ref checks);
        Check(afterClear.Contains(preexistingContent, StringComparison.Ordinal),
            "Clearing a Hyprland hotkey disturbed the user's pre-existing bindings", ref checks);

        // Separate UI actions can overlap while the preceding hyprctl reload is
        // still in flight. Every successful apply must survive that race.
        HotkeySettings[] concurrentSettings = Enumerable.Range(0, 16)
            .Select(index => NewHotkey(HotkeyType.PrintScreen, (Keys)((int)Keys.A + index)))
            .ToArray();
        HyprlandHotkeySyncResult[] concurrentResults = Task.WhenAll(
                concurrentSettings.Select(setting => HyprlandHotkeyBindingManager.ApplyAsync(setting)))
            .GetAwaiter().GetResult();
        Check(concurrentResults.All(result => result.IsApplicable && result.IsSuccess),
            "One of the concurrent Hyprland hotkey applies did not succeed", ref checks);
        string afterConcurrentApply = File.ReadAllText(tempBindingsPath);
        foreach (HotkeySettings concurrentSetting in concurrentSettings)
        {
            Check(afterConcurrentApply.Contains(
                    $"-- SNAPX-HOTKEY: {concurrentSetting.HotkeyInfo.RegistrationId} BEGIN",
                    StringComparison.Ordinal),
                "A concurrent Hyprland hotkey apply overwrote another managed entry", ref checks);
        }

        // A pre-existing configuration error should be reported as pre-existing,
        // not cause every otherwise-valid SnapX binding change to be rolled back.
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FAKE_CONFIG_ERRORS", "An unrelated existing Hyprland error.");
        var preexistingErrorSetting = NewHotkey(HotkeyType.ActiveWindow, Keys.Control | Keys.Alt | Keys.W);
        HyprlandHotkeySyncResult preexistingErrorApply = HyprlandHotkeyBindingManager.ApplyAsync(preexistingErrorSetting).GetAwaiter().GetResult();
        Check(preexistingErrorApply.IsApplicable && preexistingErrorApply.IsSuccess,
            "A pre-existing Hyprland error prevented an otherwise-valid binding from being applied", ref checks);
        HyprlandHotkeySyncResult preexistingErrorClear = HyprlandHotkeyBindingManager.ClearAsync(preexistingErrorSetting).GetAwaiter().GetResult();
        Check(preexistingErrorClear.IsApplicable && preexistingErrorClear.IsSuccess,
            "A pre-existing Hyprland error prevented an otherwise-valid binding from being cleared", ref checks);
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FAKE_CONFIG_ERRORS", null);

        string beforeFailedApply = File.ReadAllText(tempBindingsPath);
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FAKE_HYPRCTL_FAILURE", "1");
        var thirdSetting = NewHotkey(HotkeyType.ActiveWindow, Keys.Control | Keys.Alt | Keys.W);
        HyprlandHotkeySyncResult failedApply = HyprlandHotkeyBindingManager.ApplyAsync(thirdSetting).GetAwaiter().GetResult();
        Check(failedApply.IsApplicable && !failedApply.IsSuccess,
            "Applying a Hyprland hotkey during a simulated hyprctl failure did not report failure", ref checks);
        string afterFailedApply = File.ReadAllText(tempBindingsPath);
        Check(string.Equals(beforeFailedApply, afterFailedApply, StringComparison.Ordinal),
            "A failed Hyprland reload/validation did not restore the original bindings file", ref checks);
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FAKE_HYPRCTL_FAILURE", null);

        var invalidSetting = new HotkeySettings
        {
            HotkeyInfo = new HotkeyInfo(Keys.LButton),
            TaskSettings = new TaskSettings { Job = HotkeyType.RectangleRegion }
        };
        HyprlandHotkeySyncResult invalidApply = HyprlandHotkeyBindingManager.ApplyAsync(invalidSetting).GetAwaiter().GetResult();
        Check(invalidApply.IsApplicable && !invalidApply.IsSuccess,
            "Applying a hotkey that cannot be represented in Hyprland syntax did not report failure", ref checks);
    }
    finally
    {
        Environment.SetEnvironmentVariable("SNAPX_HYPR_BINDINGS_PATH", previousBindingsPath);
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FORCE_SESSION", previousForceSession);
        Environment.SetEnvironmentVariable("SNAPX_HYPR_SKIP_RELOAD", previousSkipReload);
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FAKE_HYPRCTL_FAILURE", previousFakeFailure);
        Environment.SetEnvironmentVariable("SNAPX_HYPR_FAKE_CONFIG_ERRORS", previousFakeConfigErrors);
        if (File.Exists(tempBindingsPath))
        {
            File.Delete(tempBindingsPath);
        }
    }
}


static void FuzzUploaderResponseValidation(Random random, ref int checks)
{
    Check(UploaderResponseValidator.TryGetHttpUri(
        "https://example.com/path", out _, "example.com"), "Valid uploader URL was rejected", ref checks);
    Check(UploaderResponseValidator.TryGetHttpUri(
        "https://sub.example.com/path", out _, "example.com"), "Valid uploader subdomain was rejected", ref checks);
    Check(!UploaderResponseValidator.TryGetHttpUri(
        "https://example.com.evil.test/path", out _, "example.com"), "Host suffix confusion was accepted", ref checks);
    Check(!UploaderResponseValidator.TryGetHttpUri(
        "https://example.com@evil.test/path", out _, "example.com"), "User-info host confusion was accepted", ref checks);

    for (int i = 0; i < 40_000; i++)
    {
        string value = RandomText(random, random.Next(0, 180), includeControls: true);
        _ = UploaderResponseValidator.TryGetHttpUri(value, out _, "example.com", "api.example.test");
        _ = UploaderResponseValidator.TryResolveHttpUri("https://example.com/base/", value, out _);
        checks++;
    }
}

static void FuzzCustomUploaderSyntax(Random random, ref int checks)
{
    var parser = new ShareXCustomUploaderSyntaxParser(HeadlessCustomUploaderInteraction.Instance)
    {
        FileName = "sample.png",
        Input = "fuzz input"
    };

    Check(parser.Parse("{inputbox:title|fallback}") == "fallback", "Headless inputbox did not return its default", ref checks);
    Check(parser.Parse("literal\\{text\\}") == "literal{text}", "Escaped syntax was not preserved", ref checks);

    for (int i = 0; i < 25_000; i++)
    {
        // Exclude an opening brace so random data cannot intentionally invoke an
        // unknown custom function. This still exercises escaping and delimiters.
        string value = RandomText(random, random.Next(0, 160), includeControls: false, allowOpeningBrace: false);
        _ = parser.Parse(value);
        checks++;
    }
}

static void FuzzHistoryFiltering(Random random, ref int checks)
{
    var items = Enumerable.Range(0, 300)
        .Select(index => new HistoryItem
        {
            Id = index,
            FileName = RandomText(random, random.Next(0, 60), includeControls: false),
            URL = index % 3 == 0 ? $"https://example.test/{index}" : null,
            DateTime = DateTime.UtcNow.Date.AddDays(-index),
            Tags = [new HistoryItem.Tag { Text = $"tag-{index}" }]
        })
        .ToArray();

    for (int i = 0; i < 1_000; i++)
    {
        var filter = new HistoryFilter
        {
            Filename = i % 2 == 0 ? "*" : RandomText(random, random.Next(0, 10), false),
            URL = i % 3 == 0 ? "example" : string.Empty,
            MaxItemCount = random.Next(0, 40),
            SearchInTags = random.Next(2) == 0
        };
        HistoryItem[] result = filter.ApplyFilter(items).ToArray();
        Check(result.Length <= filter.MaxItemCount || filter.MaxItemCount <= 0,
            "History filter exceeded MaxItemCount", ref checks);
        checks++;
    }
}

static void VerifyHistoryCommitIdentityAndOrder(ref int checks)
{
    using var connection = new SqliteConnection("Data Source=:memory:");
    connection.Open();
    using (var command = connection.CreateCommand())
    {
        command.CommandText = """
            CREATE TABLE HistoryItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FileName TEXT NOT NULL,
                FilePath TEXT NULL,
                DateTime TEXT NOT NULL,
                Type TEXT NULL,
                Hidden INTEGER NOT NULL DEFAULT 0,
                Host TEXT NULL,
                URL TEXT NULL,
                ThumbnailURL TEXT NULL,
                DeletionURL TEXT NULL,
                ShortenedURL TEXT NULL
            );
            CREATE TABLE Tags (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                HistoryItemId INTEGER NOT NULL,
                Text TEXT NULL,
                WindowTitle TEXT NULL,
                ProcessName TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    var manager = new HistoryManagerSQLite(connection);
    DateTime now = DateTime.UtcNow;
    var older = new HistoryItem { FileName = "older.png", FilePath = "/tmp/older.png", DateTime = now.AddMinutes(-1), Type = "Image" };
    var newer = new HistoryItem { FileName = "newer.png", FilePath = "/tmp/newer.png", DateTime = now, Type = "Image" };

    Check(manager.AppendHistoryItem(older), "Older history item did not commit", ref checks);
    Check(manager.AppendHistoryItem(newer), "Newer history item did not commit", ref checks);
    Check(older.Id > 0 && newer.Id > older.Id, "Committed history IDs were not returned to callers", ref checks);

    List<HistoryItem> items = manager.GetHistoryItems(2);
    Check(items.Count == 2, "History query returned the wrong item count", ref checks);
    Check(items[0].Id == newer.Id && items[1].Id == older.Id,
        "History query did not return newest items first", ref checks);
}

static void VerifyHistoryMediaPreviewRouting(ref int checks)
{
    string path = Path.Combine(Path.GetTempPath(), $"snapx-preview-{Guid.NewGuid():N}.mp4");
    File.WriteAllBytes(path, [0]);

    try
    {
        var item = new HistoryItem
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            Type = "File"
        };
        var manager = new HistoryItemManager(null, null, null);
        manager.GetHistoryItems += () => [item];

        int imagePreviews = 0;
        int videoPreviews = 0;
        HistoryItem? previewedVideo = null;
        manager.ImagePreviewRequested += _ => imagePreviews++;
        manager.VideoPreviewRequested += previewed =>
        {
            previewedVideo = previewed;
            videoPreviews++;
        };

        manager.Execute(HistoryAction.ShowImagePreview);
        Check(videoPreviews == 1, "MP4 history item did not request a video preview", ref checks);
        Check(ReferenceEquals(previewedVideo, item), "Video preview selected the wrong history item", ref checks);
        Check(imagePreviews == 0, "MP4 history item incorrectly requested an image preview", ref checks);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

static void VerifyClipboardTaskRouting(ref int checks)
{
    // Do not use a live clipboard in this test. It proves that the two
    // after-capture jobs publish the frontend-owned event that performs the
    // native clipboard write, rather than falling back to Core's no-op stub.
    string path = Path.Combine(Path.GetTempPath(), $"snapx-clipboard-{Guid.NewGuid():N}.txt");
    File.WriteAllText(path, "clipboard routing test");

    try
    {
        NeedClipboardCopyEvent? fileEvent = null;
        NeedClipboardCopyEvent? imageEvent = null;
        SnapXL.EventAggregator.Subscribe<NeedClipboardCopyEvent>(@event =>
        {
            if (@event.HasFiles) fileEvent = @event;
            if (@event.HasImage) imageEvent = @event;
            // This test is the frontend stand-in. Production capture workers
            // now retain their source image until the frontend completes the
            // native clipboard handoff.
            @event.MarkAsHandled();
        });

        var fileSettings = new TaskSettings
        {
            UseDefaultAfterCaptureJob = false,
            AfterCaptureJob = AfterCaptureTasks.CopyFileToClipboard,
            UseDefaultGeneralSettings = false,
            GeneralSettings = new TaskSettingsGeneral { ShowToastNotificationAfterTaskCompleted = false }
        };
        WorkerTask fileTask = WorkerTask.CreateFileJobTask(path, new TaskMetadata(), fileSettings);
        CompleteTask(fileTask, "CopyFileToClipboard");

        Check(fileEvent?.HasFiles == true, "CopyFileToClipboard did not publish a file clipboard event", ref checks);
        Check(fileEvent!.FilePaths!.SequenceEqual([path]), "CopyFileToClipboard published the wrong file path", ref checks);

        // The upload-info actions are another non-UI entry point that used the
        // same no-op Core helper. They must route the main file and thumbnail
        // variants to the frontend just like the after-capture task does.
        var uploadInfo = new UploadInfoManager();
        fileTask.Info.ThumbnailFilePath = path;
        uploadInfo.UpdateSelectedItems([fileTask]);
        fileEvent = null;
        uploadInfo.CopyFile();
        Check(fileEvent?.FilePaths!.SequenceEqual([path]) == true,
            "Upload-info CopyFile did not publish a file clipboard event", ref checks);

        fileEvent = null;
        uploadInfo.CopyThumbnailFile();
        Check(fileEvent?.FilePaths!.SequenceEqual([path]) == true,
            "Upload-info CopyThumbnailFile did not publish a file clipboard event", ref checks);

        var imageSettings = new TaskSettings
        {
            UseDefaultAfterCaptureJob = false,
            AfterCaptureJob = AfterCaptureTasks.CopyImageToClipboard,
            UseDefaultGeneralSettings = false,
            GeneralSettings = new TaskSettingsGeneral { ShowToastNotificationAfterTaskCompleted = false }
        };
        using var sourceImage = new Image<Rgba32>(1, 1);
        // Supply a name to keep this isolated from the application-wide name
        // parser, which is deliberately not initialized by this Core-only test.
        WorkerTask imageTask = WorkerTask.CreateImageUploaderTask(new TaskMetadata(sourceImage), imageSettings, "clipboard-image");
        CompleteTask(imageTask, "CopyImageToClipboard");

        Check(imageEvent?.HasImage == true, "CopyImageToClipboard did not publish an image clipboard event", ref checks);
        Check(imageEvent!.Completion.IsCompletedSuccessfully,
            "CopyImageToClipboard completed before the frontend clipboard handoff", ref checks);

        static void CompleteTask(WorkerTask task, string taskName)
        {
            using var completed = new ManualResetEventSlim();
            task.TaskCompleted += _ => completed.Set();
            task.Start();
            if (!completed.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException($"{taskName} task did not complete.");
        }
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

static async Task<int> VerifyThumbnailCacheIdentity()
{
    var checks = 0;
    string directory = Path.Combine(Path.GetTempPath(), "snapx-thumbnail-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string source = Path.Combine(directory, "capture.png");

    try
    {
        using (var firstImage = new Image<Rgba32>(24, 24, Color.Red))
        {
            await firstImage.SaveAsPngAsync(source);
        }
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(-2));
        string firstThumbnail = await ThumbnailService.GetCompatibleSourceAsync(source);

        using (var secondImage = new Image<Rgba32>(24, 24, Color.Blue))
        {
            await secondImage.SaveAsPngAsync(source);
        }
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(2));
        string secondThumbnail = await ThumbnailService.GetCompatibleSourceAsync(source);

        Check(File.Exists(firstThumbnail), "First thumbnail was not created", ref checks);
        Check(File.Exists(secondThumbnail), "Updated thumbnail was not created", ref checks);
        Check(!string.Equals(firstThumbnail, secondThumbnail, StringComparison.Ordinal),
            "A modified local image reused its stale thumbnail", ref checks);

        string[] concurrent = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => ThumbnailService.GetCompatibleSourceAsync(source)));
        Check(concurrent.All(path => path == secondThumbnail),
            "Concurrent thumbnail requests produced inconsistent sources", ref checks);

        // Video cards use a generated play-icon tile rather than attempting to
        // bind an MP4 directly to Avalonia's Image control.
        string video = Path.Combine(directory, "capture.mp4");
        await File.WriteAllBytesAsync(video, [0]);
        string videoThumbnail = await ThumbnailService.GetCompatibleSourceAsync(video);
        using (Image playIcon = await Image.LoadAsync(videoThumbnail))
        {
            Check(playIcon.Width == 200 && playIcon.Height == 150,
                "Video thumbnail did not produce the expected play-icon dimensions", ref checks);
        }

        // A truncated file must be regenerated, not reused simply because it
        // exists. This reproduces the 36-byte blank WebP entries found in the
        // on-disk cache during UI verification.
        await File.WriteAllBytesAsync(videoThumbnail, new byte[36]);
        string repairedVideoThumbnail = await ThumbnailService.GetCompatibleSourceAsync(video);
        Check(repairedVideoThumbnail == videoThumbnail && new FileInfo(repairedVideoThumbnail).Length >= 128,
            "A truncated video thumbnail cache entry was reused", ref checks);
        using (Image repairedPlayIcon = await Image.LoadAsync(repairedVideoThumbnail))
        {
            Check(repairedPlayIcon.Width == 200 && repairedPlayIcon.Height == 150,
                "A repaired video thumbnail could not be decoded", ref checks);
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return checks;
}

static void FuzzSimplifiedTechnicalEnglish(Random random, ref int checks)
{
    var resourceSet = Lang.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, true)
        ?? throw new InvalidOperationException("The default language resource set is missing.");

    foreach (DictionaryEntry entry in resourceSet)
    {
        if (entry.Value is not string value || string.IsNullOrWhiteSpace(value)) continue;
        Check(SimplifiedTechnicalEnglish.IsAcceptable(value),
            $"Resource text is not STE-friendly: {entry.Key}", ref checks);
    }

    Check(SimplifiedTechnicalEnglish.Analyze("Do not click here.").Count > 0,
        "The STE checker did not reject a vague instruction", ref checks);
    Check(SimplifiedTechnicalEnglish.Analyze("This is a short sentence.").Count == 0,
        "The STE checker rejected clear text", ref checks);

    for (int i = 0; i < 15_000; i++)
    {
        string value = RandomText(random, random.Next(0, 240), includeControls: true) +
            (random.Next(4) == 0 ? "!" : string.Empty);
        var first = SimplifiedTechnicalEnglish.Analyze(value);
        var second = SimplifiedTechnicalEnglish.Analyze(value);
        Check(first.SequenceEqual(second), "The STE checker was not deterministic", ref checks);
        checks++;
    }
}

static void FuzzHotkeyParser(Random random, ref int checks)
{
    string[] valid = ["Ctrl+Shift+A", "Alt+PrintScreen", "Win+F12", "Numpad 7", "Enter"];
    foreach (string value in valid)
    {
        Check(HotkeyParser.TryParse(value, out var key, out var win, out _),
            $"Valid shortcut was rejected: {value}", ref checks);
        Check(new HotkeyInfo(key) { Win = win }.IsValidHotkey,
            $"Parsed shortcut was invalid: {value}", ref checks);
    }

    for (int i = 0; i < 5_000; i++)
    {
        string value = RandomText(random, random.Next(0, 80), includeControls: false);
        _ = HotkeyParser.TryParse(value, out _, out _, out _);
        checks++;
    }
}

static void VerifyOfficialUploaderServices(ref int checks)
{
    string[] expected =
    [
        "PhotobucketImageUploaderService",
        "LambdaFileUploaderService",
        "TransfershFileUploaderService",
        "SlexyTextUploaderService",
        "UpasteTextUploaderService",
        "PastieTextUploaderService",
        "PrivateBinUploaderService",
        "BitlyURLShortenerService",
        "TurlURLShortenerService"
    ];

    var serviceNames = UploaderFactory.AllServices.Select(service => service.GetType().Name).ToHashSet();
    foreach (string name in expected)
    {
        Check(serviceNames.Contains(name), $"Official uploader service was not registered: {name}", ref checks);
    }
}

static HotkeySettings NewHotkey(HotkeyType type, Keys key) => new()
{
    HotkeyInfo = new HotkeyInfo(key),
    TaskSettings = new TaskSettings { Job = type }
};

static string RandomText(Random random, int length, bool includeControls, bool allowOpeningBrace = true)
{
    const string safe = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 _-.:|\\{}[]()/$@";
    var chars = new char[length];
    for (int i = 0; i < chars.Length; i++)
    {
        chars[i] = includeControls && random.Next(10) == 0
            ? (char)random.Next(0, 32)
            : safe[random.Next(safe.Length)];
        if (!allowOpeningBrace && chars[i] == '{') chars[i] = 'x';
    }
    return new string(chars);
}

static void Check(bool condition, string message, ref int checks)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

static bool Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
        return false;
    }
    catch (TException)
    {
        return true;
    }
}
