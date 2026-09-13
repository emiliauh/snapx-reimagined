using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Microsoft.Data.Sqlite;
using SnapX.Core;
using SnapX.Core.History;
using SnapX.Core.Hotkey;
using SnapX.Core.Job;
using SnapX.Core.Upload;
using SnapX.Core.Upload.Custom;
using SnapX.Core.Upload.Utils;
using SnapX.Core.ScreenCapture;
using SnapX.Core.ScreenCapture.ScreenRecording;
using SnapX.Core.Localization;
using SnapX.Core.Media.Services;
using SnapX.Core.Utils;

if (args.Contains("--frozen-composition-probe", StringComparer.Ordinal))
{
    int probeChecks = 0;
    VerifyFrozenRegionComposition(ref probeChecks);
    Console.WriteLine($"Frozen display composition passed: {probeChecks:N0} checks.");
    return 0;
}

if (args.Contains("--mac-recording-probe", StringComparer.Ordinal))
    return await MacOSRecordingChecks.Probe();

if (args.Contains("--macos-permission-probe", StringComparer.Ordinal))
    return MacOSRecordingChecks.PermissionProbe();

if (args.Contains("--safe-after-capture-default-probe", StringComparer.Ordinal))
{
    int probeChecks = 0;
    VerifyLegacyAutomaticUploadMigration(ref probeChecks);
    Console.WriteLine($"Safe after-capture default migration passed: {probeChecks:N0} checks.");
    return 0;
}

if (args.Contains("--configuration-binding-probe", StringComparer.Ordinal))
{
    int probeChecks = 0;
    VerifyConfigurationBinding(ref probeChecks);
    Console.WriteLine($"Configuration binding probe passed: {probeChecks:N0} checks.");
    return 0;
}

if (args.Contains("--annotation-probe", StringComparer.Ordinal))
{
    int probeChecks = await VerifyAnnotationLifecycleAsync();
    Console.WriteLine($"Annotation lifecycle and rendering probe passed: {probeChecks:N0} checks.");
    return 0;
}

if (args.Contains("--frozen-composer-probe", StringComparer.Ordinal))
{
    int probeChecks = 0;
    VerifyFrozenRegionComposition(ref probeChecks);
    Console.WriteLine($"Frozen display composition probe passed: {probeChecks:N0} checks.");
    return 0;
}

if (args.Contains("--macos-hotkey-probe", StringComparer.Ordinal))
{
    if (!OperatingSystem.IsMacOS()) return 2;
    using var backend = HotkeyBackendFactory.CreateDefault();
    var first = new HotkeyRegistration("mac_probe_first", new HotkeyInfo(Keys.Control | Keys.Alt | Keys.F18) { Win = true });
    var duplicate = new HotkeyRegistration("mac_probe_duplicate", first.HotkeyInfo);
    var results = backend.RegisterAsync([first, duplicate]).GetAwaiter().GetResult();
    if (!results[first.Id].IsRegistered || results[duplicate.Id].IsRegistered)
        throw new InvalidOperationException($"macOS registration/conflict probe failed: {results[first.Id]} / {results[duplicate.Id]}");
    backend.UnregisterAsync().GetAwaiter().GetResult();
    results = backend.RegisterAsync([duplicate]).GetAwaiter().GetResult();
    if (!results[duplicate.Id].IsRegistered)
        throw new InvalidOperationException("macOS unregister did not release the shortcut.");
    backend.UnregisterAsync().GetAwaiter().GetResult();
    Console.WriteLine("macOS native hotkey registration, duplicate rejection, release and re-registration passed.");
    return 0;
}

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

int seed = 0x5A17;
string? seedArgument = args.FirstOrDefault(argument => argument.StartsWith("--seed=", StringComparison.Ordinal));
if (seedArgument is not null && !int.TryParse(seedArgument[7..], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
{
    Console.Error.WriteLine("Use --seed=<32-bit integer> to replay a fuzz campaign.");
    return 2;
}
var random = new Random(seed);
var checks = 0;

try
{
    VerifyLazySecretStore(ref checks);
    VerifyConfigurationBinding(ref checks);
    VerifyPortablePathIsolation(ref checks);
    VerifyLegacyAutomaticUploadMigration(ref checks);
    FuzzRegionNormalization(random, ref checks);
    VerifyFrozenRegionComposition(ref checks);
    MacOSRecordingChecks.Fuzz(random, ref checks);
    checks += await VerifyRegionSelectionLifecycleAsync();
    checks += await VerifyAnnotationLifecycleAsync();
    FuzzHotkeyLifecycle(random, ref checks);
    FuzzPortalAcceleratorFormatting(random, ref checks);
    FuzzUploaderResponseValidation(random, ref checks);
    FuzzCustomUploaderSyntax(random, ref checks);
    FuzzNestedUploaderSyntax(random, ref checks);
    FuzzHistoryFiltering(random, ref checks);
    VerifyHistoryCommitIdentityAndOrder(ref checks);
    VerifyHistoryMediaPreviewRouting(ref checks);
    VerifyClipboardTaskRouting(ref checks);
    checks += await VerifyThumbnailCacheIdentity();
    FuzzSimplifiedTechnicalEnglish(random, ref checks);
    FuzzHotkeyParser(random, ref checks);
    FuzzHotkeyRegistrationIdentity(ref checks);
    VerifyOfficialUploaderServices(ref checks);
    if (OperatingSystem.IsLinux())
        VerifyHyprlandHotkeyBindingManager(ref checks);
    else
        Console.WriteLine("Skipped Linux-only Hyprland binding integration checks.");
    VerifyWfRecorderStopEscalation(ref checks);

    Console.WriteLine($"SnapX fuzz/property checks passed: {checks:N0} (seed {seed}).");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"SnapX fuzz/property check failed after {checks:N0} checks (seed {seed}):");
    Console.Error.WriteLine(ex);
    return 1;
}

static void VerifyLazySecretStore(ref int checks)
{
    int keyRequests = 0;
    var constructor = typeof(SecurePropertyStore).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
        binder: null, types: [typeof(Func<byte[]>)], modifiers: null)!;
    var store = (SecurePropertyStore)constructor.Invoke([(Func<byte[]>)(() => { keyRequests++; return new byte[32]; })]);
    Check(keyRequests == 0, "Secret-store construction opened the native vault", ref checks);
    Check(store.Protect("") == "" && store.Unprotect("ordinary setting") == "ordinary setting" && keyRequests == 0,
        "Unencrypted settings unnecessarily opened the native vault", ref checks);
    string encrypted = store.Protect("local regression fixture");
    Check(keyRequests == 1 && encrypted.StartsWith(SecurePropertyStore.Header), "Encryption did not initialize the key once", ref checks);
    Check(store.Unprotect(encrypted) == "local regression fixture" && keyRequests == 1,
        "Lazy-key secret did not round-trip with one vault lookup", ref checks);
}

static void VerifyConfigurationBinding(ref int checks)
{
    var application = new ApplicationConfig();
    IConfiguration applicationOverrides = new ConfigurationBuilder()
        .AddCommandLine([
            "--ShowTray=false",
            "--DefaultTaskSettings:Description=generated binder",
            "--FilePath=/must-not-bind"
        ])
        .Build();
    SettingManager.ApplyConfigurationOverrides(applicationOverrides, application);
    Check(!application.ShowTray, "Application scalar override was not bound", ref checks);
    Check(application.DefaultTaskSettings.Description == "generated binder",
        "Application nested override was not bound", ref checks);
    Check(string.IsNullOrEmpty(application.FilePath),
        "Configuration overrides changed loader-owned runtime state", ref checks);

    var uploaders = new UploadersConfig();
    IConfiguration uploaderOverrides = new ConfigurationBuilder()
        .AddCommandLine([
            "--HastebinCustomDomain=https://binding.invalid",
            "--FTPSelectedImage=7"
        ])
        .Build();
    SettingManager.ApplyConfigurationOverrides(uploaderOverrides, uploaders);
    Check(uploaders.HastebinCustomDomain == "https://binding.invalid",
        "Uploader string override was not bound", ref checks);
    Check(uploaders.FTPSelectedImage == 7, "Uploader numeric override was not bound", ref checks);

    var hotkeys = new HotkeysConfig();
    IConfiguration hotkeyOverrides = new ConfigurationBuilder()
        .AddCommandLine([
            "--Hotkeys:0:HotkeyInfo:Hotkey=F12",
            "--Hotkeys:0:HotkeyInfo:Win=true",
            "--Hotkeys:0:TaskSettings:Description=bound hotkey"
        ])
        .Build();
    SettingManager.ApplyConfigurationOverrides(hotkeyOverrides, hotkeys);
    Check(hotkeys.Hotkeys.Count == 1, "Hotkey collection override was not bound", ref checks);
    Check(hotkeys.Hotkeys[0].HotkeyInfo.Hotkey == Keys.F12 && hotkeys.Hotkeys[0].HotkeyInfo.Win,
        "Hotkey nested override was not bound", ref checks);
    Check(hotkeys.Hotkeys[0].TaskSettings.Description == "bound hotkey",
        "Hotkey task override was not bound", ref checks);
}

static void VerifyPortablePathIsolation(ref int checks)
{
    PropertyInfo portable = typeof(SnapXL).GetProperty(nameof(SnapXL.Portable))!;
    PropertyInfo config = typeof(SnapXL).GetProperty("CustomConfigPath", BindingFlags.NonPublic | BindingFlags.Static)!;
    bool previousPortable = SnapXL.Portable;
    string previousPersonal = SnapXL.CustomPersonalPath;
    object? previousConfig = config.GetValue(null);
    string personal = Path.Combine(Path.GetTempPath(), "snapx-portable-path-regression");
    try
    {
        portable.SetValue(null, true);
        SnapXL.CustomPersonalPath = personal;
        config.SetValue(null, null);
        Check(SnapXL.ConfigFolder == personal, "Portable settings escaped the personal folder", ref checks);
        Check(SnapXL.CacheFolder == Path.Combine(personal, "Cache"), "Portable cache escaped the personal folder", ref checks);
        Check(SnapXL.LogsFolder == Path.Combine(personal, "Logs"), "Portable logs escaped the personal folder", ref checks);
        string explicitConfig = Path.Combine(personal, "ExplicitConfig");
        config.SetValue(null, explicitConfig);
        Check(SnapXL.ConfigFolder == explicitConfig, "Portable mode ignored an explicit configuration directory", ref checks);
    }
    finally
    {
        portable.SetValue(null, previousPortable);
        SnapXL.CustomPersonalPath = previousPersonal;
        config.SetValue(null, previousConfig);
    }
}

static void VerifyLegacyAutomaticUploadMigration(ref int checks)
{
    const AfterCaptureTasks legacyDefault = AfterCaptureTasks.CopyImageToClipboard |
        AfterCaptureTasks.SaveImageToFile |
        AfterCaptureTasks.UploadImageToHost;
    const AfterCaptureTasks safeDefault = AfterCaptureTasks.CopyImageToClipboard |
        AfterCaptureTasks.SaveImageToFile;

    var legacyConfig = new ApplicationConfig();
    legacyConfig.DefaultTaskSettings.AfterCaptureJob = legacyDefault;
    Check(legacyConfig.MigrateLegacyAutomaticUploadDefault(),
        "Legacy automatic upload default was not migrated", ref checks);
    Check(legacyConfig.DefaultTaskSettings.AfterCaptureJob == safeDefault,
        "Legacy automatic upload migration removed local capture actions", ref checks);
    Check(!legacyConfig.MigrateLegacyAutomaticUploadDefault(),
        "Legacy automatic upload migration was not one-time", ref checks);

    var customizedConfig = new ApplicationConfig();
    customizedConfig.DefaultTaskSettings.AfterCaptureJob = AfterCaptureTasks.SaveImageToFile |
        AfterCaptureTasks.UploadImageToHost;
    Check(!customizedConfig.MigrateLegacyAutomaticUploadDefault(),
        "Automatic upload migration changed a customized task set", ref checks);
    Check(customizedConfig.DefaultTaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.UploadImageToHost),
        "Automatic upload migration disabled an explicitly customized uploader", ref checks);
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

static void VerifyFrozenRegionComposition(ref int checks)
{
    using var retina = new Image<Rgba32>(200, 200, Color.Red);
    using var standard = new Image<Rgba32>(100, 100, Color.Blue);
    FrozenDisplaySource[] mixedScaleFrames =
    [
        new(new Rectangle(-100, 0, 100, 100), retina),
        new(new Rectangle(0, 0, 100, 100), standard)
    ];

    using Image mixedScale = FrozenRegionComposer.Compose(
        mixedScaleFrames,
        new Rectangle(-50, 25, 100, 50));
    using Image<Rgba32> mixedScalePixels = mixedScale.CloneAs<Rgba32>();
    Check(mixedScale.Size == new Size(200, 100),
        "Mixed-DPI frozen selection did not preserve the highest backing scale", ref checks);
    Check(mixedScalePixels[25, 50] == new Rgba32(255, 0, 0, 255),
        "Frozen selection mapped the Retina display to the wrong output area", ref checks);
    Check(mixedScalePixels[175, 50] == new Rgba32(0, 0, 255, 255),
        "Frozen selection mapped the standard-DPI display to the wrong output area", ref checks);

    using Image singleDisplay = FrozenRegionComposer.Compose(
        [mixedScaleFrames[0]],
        new Rectangle(-75, 10, 25, 30));
    Check(singleDisplay.Size == new Size(50, 60),
        "Single-display frozen selection did not retain native backing pixels", ref checks);
    Check(retina[0, 0] == new Rgba32(255, 0, 0, 255),
        "Frozen composition mutated its caller-owned display frame", ref checks);

    using var separated = new Image<Rgba32>(100, 100, Color.Green);
    FrozenDisplaySource[] gappedFrames =
    [
        new(new Rectangle(-100, 0, 100, 100), standard),
        new(new Rectangle(20, 0, 100, 100), separated)
    ];
    using Image gapped = FrozenRegionComposer.Compose(gappedFrames, new Rectangle(-10, 0, 40, 20));
    using Image<Rgba32> gappedPixels = gapped.CloneAs<Rgba32>();
    Check(gappedPixels[15, 10].A == 0,
        "A desktop gap between frozen display frames was not transparent", ref checks);
}

static async Task<int> VerifyRegionSelectionLifecycleAsync()
{
    int checks = 0;
    var bounds = new Rectangle(-1920, 0, 3840, 1080);
    var expected = new Rectangle(-400, 120, 320, 240);

    try
    {
        var returnedImage = new Image<Rgba32>(expected.Width, expected.Height);
        RegionCaptureRequest? validRequest = null;
        RegionCaptureTasks.SetRegionSelector((request, _) =>
        {
            validRequest = request;
            return Task.FromResult<RegionCaptureSelection?>(new()
            {
                Rectangle = expected,
                CaptureBounds = bounds,
                Image = returnedImage
            });
        });

        RegionCaptureSelection? selection = await RegionCaptureTasks.SelectRegionAsync(captureImage: true);
        Check(selection is not null, "A valid selector result was discarded", ref checks);
        Check(validRequest is { RestoreHiddenWindowsAfterSelection: false },
            "A successful screenshot requested that application windows reopen over its result", ref checks);
        Check(validRequest is { CaptureImage: true, AnnotateImage: true },
            "A screenshot selection did not request inline annotation", ref checks);
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

static async Task<int> VerifyAnnotationLifecycleAsync()
{
    int checks = 0;
    VerifyRegionAnnotationPipeline(ref checks);
    var settings = new TaskSettings
    {
        UseDefaultAfterCaptureJob = false,
        AfterCaptureJob = AfterCaptureTasks.AnnotateImage
    };

    using var source = new Image<Rgba32>(160, 90, Color.White);
    try
    {
        AnnotationTasks.SetEditor(null);
        bool missingHostRejected = false;
        try
        {
            await AnnotationTasks.EditAsync(source, settings);
        }
        catch (InvalidOperationException)
        {
            missingHostRejected = true;
        }
        Check(missingHostRejected, "Requested annotation silently continued without an editor host", ref checks);

        AnnotationTasks.SetEditor((request, _) =>
        {
            Check(ReferenceEquals(request.SourceImage, source), "Annotation host did not receive the capture image", ref checks);
            Check(ReferenceEquals(request.TaskSettings, settings), "Annotation host did not receive task settings", ref checks);
            return Task.FromResult(ImageAnnotationResult.Cancelled);
        });
        ImageAnnotationResult cancelled = await AnnotationTasks.EditAsync(source, settings);
        Check(!cancelled.Accepted && cancelled.Image is null, "Annotation cancel was converted into an accepted result", ref checks);
        Check(source.Width == 160, "Annotation cancel disposed the worker-owned source image", ref checks);

        var replacement = new Image<Rgba32>(160, 90, Color.Blue);
        AnnotationTasks.SetEditor((_, _) => Task.FromResult(ImageAnnotationResult.Accept(replacement)));
        ImageAnnotationResult accepted = await AnnotationTasks.EditAsync(source, settings);
        Check(accepted.Accepted && ReferenceEquals(accepted.Image, replacement), "Accepted annotation image was replaced", ref checks);
        Check(source.Width == 160, "Accepted annotation disposed source image before ownership transfer", ref checks);
        replacement.Dispose();

        var illegalCancelledImage = new Image<Rgba32>(8, 8);
        AnnotationTasks.SetEditor((_, _) => Task.FromResult(new ImageAnnotationResult
        {
            Accepted = false,
            Image = illegalCancelledImage
        }));
        bool illegalResultRejected = false;
        try
        {
            await AnnotationTasks.EditAsync(source, settings);
        }
        catch (InvalidOperationException)
        {
            illegalResultRejected = true;
        }
        Check(illegalResultRejected, "Cancelled annotation result carrying an image was accepted", ref checks);
        bool illegalImageDisposed = false;
        try { _ = illegalCancelledImage[0, 0]; }
        catch (ObjectDisposedException) { illegalImageDisposed = true; }
        Check(illegalImageDisposed, "Invalid cancelled annotation result leaked its image", ref checks);

        using var cancellation = new CancellationTokenSource();
        AnnotationTasks.SetEditor(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ImageAnnotationResult.Cancelled;
        });
        Task<ImageAnnotationResult> pending = AnnotationTasks.EditAsync(source, settings, cancellation.Token);
        cancellation.Cancel();
        bool cancellationPropagated = false;
        try { await pending; }
        catch (OperationCanceledException) { cancellationPropagated = true; }
        Check(cancellationPropagated, "Annotation cancellation did not reach the registered editor", ref checks);

        var document = new AnnotationDocument();
        document.Add(new AnnotationElement
        {
            Tool = AnnotationTool.Rectangle,
            Bounds = new RectangleF(10, 10, 50, 30),
            Color = new Rgba32(255, 0, 0),
            StrokeWidth = 4
        });
        Check(document.HitTest(new PointF(12, 12), 4) == 0, "Annotation selection hit testing missed a rectangle", ref checks);
        foreach (AnnotationResizeHandle handle in Enum.GetValues<AnnotationResizeHandle>().Where(value => value != AnnotationResizeHandle.None))
        {
            document.SelectedIndex = 0;
            document.Elements[0].Bounds = new RectangleF(10, 10, 50, 30);
            PointF target = handle switch
            {
                AnnotationResizeHandle.TopLeft => new PointF(5, 5),
                AnnotationResizeHandle.Top => new PointF(30, 5),
                AnnotationResizeHandle.TopRight => new PointF(70, 5),
                AnnotationResizeHandle.Right => new PointF(70, 20),
                AnnotationResizeHandle.BottomRight => new PointF(70, 50),
                AnnotationResizeHandle.Bottom => new PointF(30, 50),
                AnnotationResizeHandle.BottomLeft => new PointF(5, 50),
                AnnotationResizeHandle.Left => new PointF(5, 20),
                _ => default
            };
            document.ResizeSelected(handle, target);
            RectangleF resized = AnnotationDocument.Normalize(document.Elements[0].Bounds);
            Check(resized.Width >= 2 && resized.Height >= 2, $"{handle} resize produced invalid bounds", ref checks);
        }

        document.Checkpoint();
        document.MoveSelected(7, 9);
        RectangleF moved = document.Selected!.Bounds;
        document.Undo();
        Check(document.Selected!.Bounds != moved, "Annotation undo did not restore the prior geometry", ref checks);
        document.Redo();
        Check(document.Selected!.Bounds == moved, "Annotation redo did not restore moved geometry", ref checks);

        var horizontalArrow = new AnnotationElement
        {
            Tool = AnnotationTool.Arrow,
            Points = [new PointF(10, 20), new PointF(70, 20)],
            Bounds = new RectangleF(10, 20, 60, 0)
        };
        document.Add(horizontalArrow);
        document.ResizeSelected(AnnotationResizeHandle.BottomRight, new PointF(90, 50));
        Check(horizontalArrow.Points[0] == new PointF(10, 20) &&
              horizontalArrow.Points[1] == new PointF(90, 50),
            "Horizontal arrow corner resize did not anchor its start and move its rendered end", ref checks);

        var verticalArrow = new AnnotationElement
        {
            Tool = AnnotationTool.Arrow,
            Points = [new PointF(40, 10), new PointF(40, 70)],
            Bounds = new RectangleF(40, 10, 0, 60)
        };
        document.Add(verticalArrow);
        document.ResizeSelected(AnnotationResizeHandle.TopLeft, new PointF(20, 0));
        Check(verticalArrow.Points[0] == new PointF(20, 0) &&
              verticalArrow.Points[1] == new PointF(40, 70),
            "Vertical arrow corner resize did not move its rendered start and anchor its end", ref checks);

        var straightStroke = new AnnotationElement
        {
            Tool = AnnotationTool.Freehand,
            Points = [new PointF(15, 45), new PointF(45, 45), new PointF(75, 45)],
            Bounds = new RectangleF(15, 45, 60, 0)
        };
        document.Add(straightStroke);
        document.ResizeSelected(AnnotationResizeHandle.Top, new PointF(45, 30));
        Check(straightStroke.Points.All(point => point.Y == 30),
            "Straight freehand resize changed only its bounds, not its rendered points", ref checks);

        if (AnnotationDocument.TextFontFamilyName is not null)
        {
            var fittedText = new AnnotationElement
            {
                Tool = AnnotationTool.Text,
                Text = "Short",
                Bounds = new RectangleF(12, 14, 1, 1),
                FontSize = 24
            };
            AnnotationDocument.FitTextBoundsToContent(fittedText);
            float shortWidth = fittedText.Bounds.Width;
            fittedText.Text = "A substantially longer label";
            AnnotationDocument.FitTextBoundsToContent(fittedText);
            Check(fittedText.Bounds.X == 12 && fittedText.Bounds.Y == 14 && fittedText.FontSize == 24,
                "Editing text changed its position or font size while fitting selection bounds", ref checks);
            Check(fittedText.Bounds.Width > shortWidth && fittedText.Bounds.Height > 1,
                "Editing text did not expand selection and hit-test bounds to cover its content", ref checks);
        }

        var textResizeTargets = new Dictionary<AnnotationResizeHandle, PointF>
        {
            [AnnotationResizeHandle.TopLeft] = new PointF(-30, 0),
            [AnnotationResizeHandle.Top] = new PointF(70, 0),
            [AnnotationResizeHandle.TopRight] = new PointF(170, 0),
            [AnnotationResizeHandle.Right] = new PointF(170, 40),
            [AnnotationResizeHandle.BottomRight] = new PointF(170, 80),
            [AnnotationResizeHandle.Bottom] = new PointF(70, 80),
            [AnnotationResizeHandle.BottomLeft] = new PointF(-30, 80),
            [AnnotationResizeHandle.Left] = new PointF(-30, 40)
        };
        foreach ((AnnotationResizeHandle handle, PointF target) in textResizeTargets)
        {
            var textDocument = new AnnotationDocument();
            textDocument.Add(new AnnotationElement
            {
                Tool = AnnotationTool.Text,
                Text = "Resize",
                Bounds = new RectangleF(20, 20, 100, 40),
                FontSize = 30
            });
            textDocument.ResizeSelected(handle, target);
            AnnotationElement resizedText = textDocument.Selected!;
            RectangleF resizedBounds = AnnotationDocument.Normalize(resizedText.Bounds);
            Check(Math.Abs(resizedBounds.Width - 150) < .01f &&
                  Math.Abs(resizedBounds.Height - 60) < .01f,
                $"{handle} did not uniformly resize text bounds", ref checks);
            Check(Math.Abs(resizedText.FontSize - 45) < .01f &&
                  AnnotationDocument.GetEffectiveTextFontSize(resizedText) == resizedText.FontSize,
                $"{handle} did not resize preview/export text size with its bounds", ref checks);
        }

        var incompleteArrowDocument = new AnnotationDocument();
        incompleteArrowDocument.Add(new AnnotationElement
        {
            Tool = AnnotationTool.Arrow,
            Bounds = new RectangleF(12, 12, 1, 1),
            Points = [new PointF(12, 12)],
            Color = new Rgba32(0, 0, 0),
            StrokeWidth = 4
        });
        using (Image<Rgba32> incompleteArrow = incompleteArrowDocument.Render(source))
        {
            bool renderedIncompleteArrow = false;
            incompleteArrow.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height && !renderedIncompleteArrow; y++)
                    renderedIncompleteArrow = accessor.GetRowSpan(y).ContainsAnyExcept(new Rgba32(255, 255, 255));
            });
            Check(!renderedIncompleteArrow,
                "An incomplete one-point arrow rendered differently from its preview", ref checks);
        }

        var provisionalDocument = new AnnotationDocument();
        var provisionalElement = new AnnotationElement
        {
            Tool = AnnotationTool.Freehand,
            Bounds = new RectangleF(10, 10, 20, 20),
            Points = [new PointF(10, 10), new PointF(30, 30)]
        };
        provisionalDocument.Add(provisionalElement, select: false);
        Check(provisionalDocument.Selected is null && provisionalDocument.SelectedIndex == -1,
            "An in-progress annotation became selectable before its drawing gesture completed", ref checks);
        Check(provisionalDocument.Select(provisionalElement) &&
              ReferenceEquals(provisionalDocument.Selected, provisionalElement),
            "A completed annotation did not become selected when its drawing gesture ended", ref checks);

        using (var effectSource = new Image<Rgba32>(40, 20, Color.White))
        {
            effectSource.Mutate(context => context.Fill(Color.Black, new Rectangle(0, 0, 20, 20)));
            Rgba32 sampled = AnnotationDocument.SampleRepresentativeColor(
                effectSource, new RectangleF(0, 0, 40, 20));
            Check(sampled == Color.Black.ToPixel<Rgba32>() || sampled == Color.White.ToPixel<Rgba32>(),
                "Erase sampling synthesized gray between distinct screenshot surfaces", ref checks);

            var eraseDocument = new AnnotationDocument();
            eraseDocument.Add(new AnnotationElement
            {
                Tool = AnnotationTool.Erase,
                Bounds = new RectangleF(5, 5, 10, 10),
                Color = new Rgba32(25, 75, 125)
            });
            using Image<Rgba32> erased = eraseDocument.Render(effectSource);
            Check(erased[10, 10] == new Rgba32(25, 75, 125) && erased[2, 2] == Color.Black.ToPixel<Rgba32>(),
                "Erase annotation did not fill only its selected bounds", ref checks);

            var blurDocument = new AnnotationDocument();
            blurDocument.Add(new AnnotationElement
            {
                Tool = AnnotationTool.Blur,
                Bounds = new RectangleF(10, 0, 20, 20),
                EffectStrength = 3
            });
            using Image<Rgba32> blurred = blurDocument.Render(effectSource);
            Check(blurred[19, 10] != Color.Black.ToPixel<Rgba32>() &&
                  blurred[19, 10] != Color.White.ToPixel<Rgba32>() &&
                  blurred[2, 10] == Color.Black.ToPixel<Rgba32>() &&
                  blurred[35, 10] == Color.White.ToPixel<Rgba32>(),
                "Blur annotation did not blur only its selected bounds", ref checks);

            AnnotationDocument scaledBlur = blurDocument.Transform(
                new RectangleF(0, 0, 40, 20), 80, 40);
            Check(scaledBlur.Elements.Single().EffectStrength == 6,
                "Annotation transform did not scale blur strength with the image", ref checks);
        }

        // Reproduce erasing light content from a dark UI (and the inverse).
        // Most of this selection is foreground, so an area average or an
        // interior majority produces a visibly mismatched rectangle.
        foreach (Rgba32 background in new[]
                 { new Rgba32(0, 0, 0), new Rgba32(255, 255, 255), new Rgba32(23, 47, 81) })
        {
            using var eraseSource = new Image<Rgba32>(80, 60, background);
            Rgba32 foreground = background.R < 128 ? new Rgba32(255, 255, 255) : new Rgba32(0, 0, 0);
            eraseSource.Mutate(context => context.Fill(Color.FromPixel(foreground), new Rectangle(10, 10, 60, 40)));
            var eraseBounds = new RectangleF(5, 5, 70, 50);
            Rgba32 matched = AnnotationDocument.SampleRepresentativeColor(eraseSource, eraseBounds);
            Check(matched == background,
                $"Erase included foreground content when matching background {background}", ref checks);
            Check(AnnotationDocument.SampleRepresentativeColor(eraseSource,
                      new RectangleF(75, 55, -70, -50)) == background,
                "Reverse-direction erase selection changed the background estimate", ref checks);
            Check(AnnotationDocument.SampleRepresentativeColor(eraseSource,
                      new RectangleF(-20, -20, 120, 100)) == background,
                "Clipped erase selection failed to sample the image boundary", ref checks);
            Check(AnnotationDocument.SampleRepresentativeColor(eraseSource,
                      new RectangleF(0, 0, 1, 1)) == background,
                "Single-pixel erase selection failed to sample its source pixel", ref checks);

            var backgroundErase = new AnnotationDocument();
            backgroundErase.Add(new AnnotationElement
            {
                Tool = AnnotationTool.Erase,
                Bounds = eraseBounds,
                Color = matched
            }, select: false);
            Check(backgroundErase.Selected is null,
                "In-progress erase displayed selection handles", ref checks);
            backgroundErase.Select(backgroundErase.Elements[0]);
            using (Image<Rgba32> result = backgroundErase.Render(eraseSource))
            {
                Check(result[40, 30] == background && result[0, 0] == background &&
                      eraseSource[40, 30] == foreground,
                    "Erase overlay failed to hide source content non-destructively", ref checks);
            }
            backgroundErase.Undo();
            using (Image<Rgba32> undone = backgroundErase.Render(eraseSource))
                Check(undone[40, 30] == foreground, "Undo erase failed to restore image content", ref checks);
            backgroundErase.Redo();
            using (Image<Rgba32> redone = backgroundErase.Render(eraseSource))
                Check(redone[40, 30] == background, "Redo erase failed to reapply matching fill", ref checks);
            AnnotationDocument transformedErase = backgroundErase.Transform(new RectangleF(5, 5, 70, 50), 140, 100);
            using var scaledSource = new Image<Rgba32>(140, 100, foreground);
            using Image<Rgba32> transformedResult = transformedErase.Render(scaledSource);
            Check(transformedResult[0, 0] == background && transformedResult[139, 99] == background,
                "Cropped and scaled erase export failed to retain the matched fill", ref checks);
        }

        document.Add(new AnnotationElement
        {
            Tool = AnnotationTool.Freehand,
            Points = [new PointF(5, 70), new PointF(40, 60), new PointF(80, 75)],
            Bounds = new RectangleF(5, 60, 75, 15),
            Color = new Rgba32(0, 120, 255),
            StrokeWidth = 3
        });
        document.Add(new AnnotationElement
        {
            Tool = AnnotationTool.Ellipse,
            Bounds = new RectangleF(90, 10, 50, 35),
            Color = new Rgba32(0, 160, 70),
            StrokeWidth = 3
        });
        document.Add(new AnnotationElement
        {
            Tool = AnnotationTool.Arrow,
            Bounds = new RectangleF(90, 60, 50, 20),
            Points = [new PointF(90, 80), new PointF(140, 60)],
            Color = new Rgba32(0, 0, 0),
            StrokeWidth = 3
        });

        var desktopDocument = new AnnotationDocument();
        desktopDocument.Elements.Add(new AnnotationElement
        {
            Tool = AnnotationTool.Arrow,
            Bounds = new RectangleF(120, 80, 200, 100),
            Points = [new PointF(120, 80), new PointF(320, 180)],
            Color = new Rgba32(255, 0, 0),
            StrokeWidth = 6,
            FontSize = 30
        });
        AnnotationDocument cropDocument = desktopDocument.Transform(
            new RectangleF(200, 100, 400, 200), 800, 400);
        AnnotationElement transformedArrow = cropDocument.Elements.Single();
        Check(transformedArrow.Points[0] == new PointF(-160, -40) &&
              transformedArrow.Points[1] == new PointF(240, 160),
            "Annotation crop export did not translate and scale point geometry", ref checks);
        Check(transformedArrow.Bounds == new RectangleF(-160, -40, 400, 200) &&
              transformedArrow.StrokeWidth == 12 && transformedArrow.FontSize == 60,
            "Annotation crop export did not scale bounds, stroke, and text metrics", ref checks);
        Check(desktopDocument.Elements[0].Points[0] == new PointF(120, 80) &&
              desktopDocument.Elements[0].StrokeWidth == 6,
            "Annotation crop export mutated the live document or its geometry", ref checks);
        using (var croppedAnnotation = new Image<Rgba32>(800, 400, Color.White))
        using (Image<Rgba32> croppedRendered = cropDocument.Render(croppedAnnotation))
        {
            Check(croppedRendered[0, 40] != new Rgba32(255, 255, 255),
                "An annotation crossing the capture boundary was not clipped into the export", ref checks);
        }

        using Image<Rgba32> rendered = document.Render(source);
        Check(rendered.Width == source.Width && rendered.Height == source.Height,
            "Annotation export changed source pixel dimensions", ref checks);
        bool containsAnnotationPixels = false;
        rendered.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height && !containsAnnotationPixels; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                containsAnnotationPixels = row.ContainsAnyExcept(new Rgba32(255, 255, 255));
            }
        });
        Check(containsAnnotationPixels, "Annotation export rendered no elements", ref checks);
        var ellipseDocument = new AnnotationDocument();
        ellipseDocument.Add(new AnnotationElement
        {
            Tool = AnnotationTool.Ellipse,
            Bounds = new RectangleF(10, 10, 100, 40),
            Color = new Rgba32(0, 0, 0),
            StrokeWidth = 3
        });
        using Image<Rgba32> ellipseImage = ellipseDocument.Render(source);
        foreach (var edge in new[] { new Point(10, 30), new Point(110, 30), new Point(60, 10), new Point(60, 50) })
            Check(ellipseImage[edge.X, edge.Y] != new Rgba32(255, 255, 255),
                "Ellipse export does not reach its selected bounds", ref checks);
    }
    finally
    {
        AnnotationTasks.SetEditor(null);
    }

    checks += await VerifyWorkerAnnotationStopAsync();
    return checks;
}

static async Task<int> VerifyWorkerAnnotationStopAsync()
{
    int checks = 0;
    MethodInfo afterCapture = typeof(WorkerTask).GetMethod(
        "DoAfterCaptureJobs",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    PropertyInfo status = typeof(WorkerTask).GetProperty(nameof(WorkerTask.Status))!;
    var source = new Image<Rgba32>(24, 16, Color.White);
    var metadata = new TaskMetadata(source) { RequiresAnnotation = true };
    var settings = new TaskSettings { UseDefaultAfterCaptureJob = false, AfterCaptureJob = 0 };
    WorkerTask worker = WorkerTask.CreateImageUploaderTask(metadata, settings, "annotation-stop-probe");
    status.SetValue(worker, SnapX.Core.TaskStatus.Working);

    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tokenCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var acceptedAfterStop = new Image<Rgba32>(24, 16, Color.Blue);
    AnnotationTasks.SetEditor(async (_, token) =>
    {
        using CancellationTokenRegistration registration = token.Register(() => tokenCancelled.TrySetResult());
        entered.TrySetResult();
        // Deliberately ignore cancellation until the test releases us. The
        // worker must still reject and dispose a late accepted editor result.
        await release.Task.ConfigureAwait(false);
        return ImageAnnotationResult.Accept(acceptedAfterStop);
    });

    try
    {
        Task<bool> pending = Task.Run(() => (bool)afterCapture.Invoke(worker, null)!);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Stop();
        await tokenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();

        Check(!await pending.WaitAsync(TimeSpan.FromSeconds(5)),
            "A stopped annotation worker continued to after-capture side effects", ref checks);
        Check(worker.StopRequested, "The annotation worker cleared a concurrent stop request", ref checks);
        bool lateResultDisposed = false;
        try { _ = acceptedAfterStop[0, 0]; }
        catch (ObjectDisposedException) { lateResultDisposed = true; }
        Check(lateResultDisposed, "A late accepted annotation result leaked after worker cancellation", ref checks);
        Check(source[0, 0] == new Rgba32(255, 255, 255, 255),
            "Stopping annotation disposed or replaced the worker-owned source prematurely", ref checks);
    }
    finally
    {
        release.TrySetResult();
        AnnotationTasks.SetEditor(null);
        worker.Dispose();
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

static void FuzzNestedUploaderSyntax(Random random, ref int checks)
{
    var parser = new EchoSyntaxParser();
    for (int i = 0; i < 5_000; i++)
    {
        string literal = RandomText(random, random.Next(0, 100), includeControls: true);
        string escaped = literal.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("|", "\\|");
        Check(parser.Parse(escaped) == literal, "Escaped template did not round-trip", ref checks);
        int depth = random.Next(1, 40);
        string nested = string.Concat(Enumerable.Repeat("{echo:", depth)) + escaped + new string('}', depth);
        Check(parser.Parse(nested) == literal, "Nested function parameters changed their value", ref checks);
    }

    foreach (string hostile in new[] { new string('{', 100_000), string.Concat(Enumerable.Repeat("{echo:", 10_000)) })
    {
        bool rejected = false;
        try { parser.Parse(hostile); }
        catch (FormatException) { rejected = true; }
        Check(rejected, "Excessively nested template was not rejected safely", ref checks);
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
    Check(SimplifiedTechnicalEnglish.Analyze("On — SnapX starts.").Count > 0,
        "The STE checker did not reject an em dash", ref checks);
    Check(SimplifiedTechnicalEnglish.Analyze("People who've helped.").Count > 0,
        "The STE checker did not reject a contraction", ref checks);
    Check(SimplifiedTechnicalEnglish.Analyze("People who’ve helped.").Count > 0,
        "The STE checker did not reject a contraction with a typographic apostrophe", ref checks);
    Check(SimplifiedTechnicalEnglish.Analyze("SnapX cannot start.").Count == 0,
        "The STE checker rejected the full form cannot", ref checks);
    Check(SimplifiedTechnicalEnglish.Analyze("This is a short sentence.").Count == 0,
        "The STE checker rejected clear text", ref checks);

    foreach (char dash in "‐‑‒–—―−﹘﹣－")
    {
        Check(SimplifiedTechnicalEnglish.Analyze($"First{dash}second.").Count > 0,
            $"The STE checker did not reject dash character U+{(int)dash:X4}", ref checks);
    }

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
    string[] valid = ["Ctrl+Shift+A", "Alt+PrintScreen", "Win+F12", "Numpad 7", "Enter", "Cmd+Option+F8", "Command+Shift+S"];
    foreach (string value in valid)
    {
        Check(HotkeyParser.TryParse(value, out var key, out var win, out _),
            $"Valid shortcut was rejected: {value}", ref checks);
        Check(new HotkeyInfo(key) { Win = win }.IsValidHotkey,
            $"Parsed shortcut was invalid: {value}", ref checks);
    }

    foreach (string invalid in new[] { "A,B", "65", "١", "A+B", "Ctrl", "LButton" })
    {
        Check(!HotkeyParser.TryParse(invalid, out _, out _, out _),
            $"Invalid shortcut was accepted: {invalid}", ref checks);
    }
    foreach (Keys code in Enum.GetValues<Keys>().Distinct())
    {
        if ((code & Keys.Modifiers) != 0) continue;
        var original = new HotkeyInfo(code | Keys.Control) { Win = true };
        if (!original.IsValidHotkey) continue;
        Check(HotkeyParser.TryParse(original.ToString(), out Keys parsed, out bool win, out _) &&
            parsed == original.Hotkey && win, $"Shortcut did not round-trip: {original}", ref checks);
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

static void VerifyRegionAnnotationPipeline(ref int checks)
{
    MethodInfo afterCapture = typeof(WorkerTask).GetMethod("DoAfterCaptureJobs", BindingFlags.Instance | BindingFlags.NonPublic)!;
    int editorCalls = 0;
    AnnotationTasks.SetEditor((_, _) =>
    {
        editorCalls++;
        return Task.FromResult(ImageAnnotationResult.Cancelled);
    });
    try
    {
        RegionCaptureTasks.SetRegionSelector((request, _) => Task.FromResult<RegionCaptureSelection?>(new()
        {
            Rectangle = new Rectangle(0, 0, 16, 16),
            CaptureBounds = new Rectangle(0, 0, 100, 100),
            Image = request.CaptureImage ? new Image<Rgba32>(16, 16) : null
        }));

        foreach (RegionCaptureType captureType in new[] { RegionCaptureType.Default, RegionCaptureType.Light, RegionCaptureType.Transparent })
        {
            var taskSettings = new TaskSettings { UseDefaultAfterCaptureJob = false, AfterCaptureJob = 0 };
            using TaskMetadata metadata = new RegionCaptureProbe(captureType).ExecuteForTest(taskSettings)!;
            Check(metadata.RequiresAnnotation, "A region screenshot without inline editing lost its annotation fallback", ref checks);
            Check(taskSettings.AfterCaptureJob == 0, "Region annotation changed the user's after-capture settings", ref checks);
            int callsBefore = editorCalls;
            WorkerTask worker = WorkerTask.CreateImageUploaderTask(metadata, taskSettings, "region-annotation-probe");
            bool continued = (bool)afterCapture.Invoke(worker, null)!;
            Check(editorCalls - callsBefore == 1, "Region image worker skipped its annotation fallback", ref checks);
            Check(!continued, "Cancelled region annotation did not stop after-capture processing", ref checks);
        }

        RegionCaptureTasks.SetRegionSelector((request, _) => Task.FromResult<RegionCaptureSelection?>(new()
        {
            Rectangle = new Rectangle(0, 0, 16, 16),
            CaptureBounds = new Rectangle(0, 0, 100, 100),
            Image = request.CaptureImage ? new Image<Rgba32>(16, 16) : null,
            AnnotationCompleted = request.AnnotateImage
        }));
        var inlineSettings = new TaskSettings
        {
            UseDefaultAfterCaptureJob = false,
            AfterCaptureJob = AfterCaptureTasks.AnnotateImage
        };
        using (TaskMetadata inlineMetadata = new RegionCaptureProbe(RegionCaptureType.Default).ExecuteForTest(inlineSettings)!)
        {
            Check(inlineMetadata.AnnotationCompleted && !inlineMetadata.RequiresAnnotation,
                "Inline annotation completion was not propagated into task metadata", ref checks);
            int callsBefore = editorCalls;
            WorkerTask inlineWorker = WorkerTask.CreateImageUploaderTask(
                inlineMetadata, inlineSettings, "inline-region-annotation-probe");
            Check((bool)afterCapture.Invoke(inlineWorker, null)!,
                "An already annotated region did not continue after-capture processing", ref checks);
            Check(editorCalls == callsBefore,
                "An already annotated region opened a duplicate editor", ref checks);
        }

        // Fullscreen/window/monitor captures use ordinary image metadata: only
        // their explicit after-capture setting should request an editor.
        using (var metadata = new TaskMetadata(new Image<Rgba32>(16, 16)) { RequiresAnnotation = true })
        {
            var taskSettings = new TaskSettings { UseDefaultAfterCaptureJob = false, AfterCaptureJob = AfterCaptureTasks.AnnotateImage };
            int callsBefore = editorCalls;
            WorkerTask worker = WorkerTask.CreateImageUploaderTask(metadata, taskSettings, "region-explicit-annotation-probe");
            Check(!(bool)afterCapture.Invoke(worker, null)!, "Region with explicit annotation ignored editor cancellation", ref checks);
            Check(editorCalls - callsBefore == 1, "Region with explicit annotation opened the editor more than once", ref checks);
        }
        foreach (bool requested in new[] { false, true })
        {
            using var metadata = new TaskMetadata(new Image<Rgba32>(16, 16));
            var taskSettings = new TaskSettings
            {
                UseDefaultAfterCaptureJob = false,
                AfterCaptureJob = requested ? AfterCaptureTasks.AnnotateImage | AfterCaptureTasks.PinToScreen : 0
            };
            int callsBefore = editorCalls;
            WorkerTask worker = WorkerTask.CreateImageUploaderTask(metadata, taskSettings, "non-region-annotation-probe");
            Check((bool)afterCapture.Invoke(worker, null)! == !requested, "Non-region annotation cancellation policy changed", ref checks);
            Check(editorCalls - callsBefore == (requested ? 1 : 0), "Non-region capture ignored its explicit annotation setting", ref checks);
        }

        int callsBeforeGeometry = editorCalls;
        RegionCaptureSelection? geometry = RegionCaptureTasks.SelectRegionAsync(captureImage: false).GetAwaiter().GetResult();
        Check(geometry is { Image: null }, "Recording selection unexpectedly produced an image", ref checks);
        Check(editorCalls == callsBeforeGeometry, "Recording geometry entered the annotation editor", ref checks);

        RegionCaptureTasks.SetRegionSelector((_, _) => Task.FromResult<RegionCaptureSelection?>(null));
        Check(new RegionCaptureProbe(RegionCaptureType.Default).ExecuteForTest(new TaskSettings()) is null,
            "Cancelled region selection created an annotation image task", ref checks);
    }
    finally
    {
        RegionCaptureTasks.SetRegionSelector(null);
        AnnotationTasks.SetEditor(null);
    }
}

sealed class RegionCaptureProbe(RegionCaptureType captureType) : SnapX.Core.Capture.CaptureRegion(captureType)
{
    public TaskMetadata? ExecuteForTest(TaskSettings settings) => Execute(settings);
}

sealed class EchoSyntaxParser : ShareXSyntaxParser
{
    protected override string? CallFunction(string functionName, string?[] parameters = null) =>
        parameters?.FirstOrDefault() ?? functionName;
}
