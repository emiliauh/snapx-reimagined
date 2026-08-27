using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Diagnostics;
using System.Text.Json;
using SnapX.Core;
using SnapX.Core.Media;
using SnapX.Core.SharpCapture.Linux.DBus;
using SnapX.Core.ScreenCapture;
using SnapX.Core.Utils.Extensions;
using SnapX.Core.Utils.Native;
using Tmds.DBus;
using Tmds.DBus.Protocol;

namespace SnapX.Core.SharpCapture.Linux;

public class LinuxCapture : BaseCapture
{
    // xdg-desktop-portal-hyprland owns the screenshot file and replaces the
    // previous result when a new request starts. Keep the whole request and
    // decode operation serial so a second SnapX capture cannot invalidate the
    // first result before ImageSharp has read it.
    private static readonly SemaphoreSlim PortalScreenshotGate = new(1, 1);

    public override async Task<Image?> CaptureFullscreen()
    {
        var isWayland = LinuxAPI.IsWayland();
        var captureMode = SnapXL.Settings?.WaylandCaptureMode ?? WaylandCaptureMode.Automatic;

        if (isWayland && captureMode == WaylandCaptureMode.KWin && !IsCompositorKwin)
        {
            DebugHelper.WriteLine("KWin capture was selected, but the current compositor is not KDE KWin.");
            return null;
        }

        if (isWayland && (captureMode == WaylandCaptureMode.Automatic || captureMode == WaylandCaptureMode.KWin) && IsCompositorKwin)
        {
            try
            {
                return await TakeScreenshotWithKwin().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "KWin screen capture failed");
                if (captureMode == WaylandCaptureMode.KWin) return null;
            }
        }

        // grim is the dependable non-interactive wlroots/Hyprland path. Use
        // it first in Automatic mode; Portal remains selectable when a user
        // needs the desktop portal's permission-mediated capture path.
        if (isWayland && captureMode == WaylandCaptureMode.Automatic)
        {
            try
            {
                return await TakeScreenshotWithGrim().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "grim screen capture failed; trying the desktop portal");
            }
        }

        if (isWayland && (captureMode == WaylandCaptureMode.Automatic || captureMode == WaylandCaptureMode.Portal))
        {
            try
            {
                return await TakeScreenshotWithPortal().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Wayland portal screen capture failed");
                if (captureMode == WaylandCaptureMode.Portal) return null;
            }
        }

        if (!isWayland || captureMode == WaylandCaptureMode.X11Fallback)
        {
            try
            {
                return LinuxAPI.TakeFullscreenScreenshot();
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
            }
        }

        return null;
    }


    private static async Task<Image> TakeScreenshotWithPortal()
    {
        await PortalScreenshotGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A portal-owned file can disappear before it is opened. Retrying
            // the old path cannot fix that; request a fresh screenshot once.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await TakeScreenshotWithPortalCore().ConfigureAwait(false);
                }
                catch (FileNotFoundException) when (attempt == 0)
                {
                    DebugHelper.WriteLine("The portal removed its screenshot before it could be read. Retrying once.");
                }
            }

            throw new InvalidOperationException("The desktop portal did not provide a readable screenshot.");
        }
        finally
        {
            PortalScreenshotGate.Release();
        }
    }

    private static async Task<Image> TakeScreenshotWithPortalCore()
    {
        using var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync().ConfigureAwait(false);
        var desktop = new DesktopService(connection, "org.freedesktop.portal.Desktop");
        // var access = new DesktopService(connection, "org.freedesktop.access");
        var screenshot = desktop.CreateScreenshot("/org/freedesktop/portal/desktop");
        var options = new Dictionary<string, VariantValue>()
        {
            // { "interactive", true }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        PortalResponse response;
        try
        {
            // Connection.Call can block while the desktop portal is wedged.
            // Run the complete D-Bus request off the capture worker and apply
            // the timeout to both request creation and its response.
            response = await Task.Run(async () =>
                await connection.Call(() => screenshot.ScreenshotAsync("", options)).ConfigureAwait(false))
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The desktop portal did not answer the screenshot request within 10 seconds.");
        }

        if (response.ResponseCode != 0)
        {
            throw new InvalidOperationException(
                $"The desktop portal denied screen capture (response {response.ResponseCode}).");
        }

        if (!response.Results.TryGetValue("uri", out VariantValue uriValue))
        {
            throw new InvalidOperationException("The desktop portal response did not include a screenshot URI.");
        }

        string uriText = uriValue.GetString();
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
        {
            throw new InvalidOperationException("The desktop portal returned an invalid screenshot URI.");
        }

        string filePath = Uri.UnescapeDataString(uri.LocalPath);
        await using var file = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        return await Image.LoadAsync(file).ConfigureAwait(false);
    }

    private static async Task<Image> TakeScreenshotWithGrim(string? monitorName = null, string? geometry = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "grim",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(monitorName))
        {
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(monitorName);
        }
        else if (!string.IsNullOrWhiteSpace(geometry))
        {
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add(geometry);
        }
        // ImageSharp decodes this output immediately after grim produces it, so
        // grim's own zlib compression (default level 6) is pure overhead: on a
        // dual-4K desktop it accounted for ~2s of a ~2.1s capture. Level 0 still
        // emits a valid PNG (just uncompressed), which is ~8x faster to write.
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-");

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("SnapX could not start grim for Wayland screen capture.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var output = new MemoryStream();
        Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        string error = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        await Task.WhenAll(copyTask, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"grim exited with code {process.ExitCode}: {error.Trim()}");
        }

        if (output.Length == 0)
        {
            throw new InvalidOperationException("grim returned an empty screenshot.");
        }

        long grimMs = stopwatch.ElapsedMilliseconds;
        output.Position = 0;
        Image image = await Image.LoadAsync(output, timeout.Token).ConfigureAwait(false);
        DebugHelper.WriteLine(
            $"grim capture: {grimMs} ms subprocess, {stopwatch.ElapsedMilliseconds - grimMs} ms decode, {output.Length.ToSizeString()} PNG.");
        return image;
    }

    // A significantly faster solution for screen capturing on KDE Wayland over FreeDesktop Portals.
    //
    // Instead of creating/contributing a new wayland protocol or using an existing wayland protocol for screen capturing,
    // KWin provides a special dbus interface `org.kde.KWin.ScreenShot2` for taking screenshots without prompting the user. This is meant for their in-house screenshot app `Spectacle`.
    // However, this interface *can* be used by other apps, as long as you follow a few rules:
    //   1. There must be a .desktop file in a privileged location e.g., /usr/share/applications/
    //   2. The .desktop entry `Exec` *must* point to a bin located in a privileged location e.g., `Exec=/usr/bin/snapx`
    //   3. The .desktop file *must* contain the following entry: `X-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2`
    //
    // If all these rules are followed, KWin will allow SnapX to take privileged, unprompted screenshots on wayland.
    // Interface Documentation: https://github.com/KDE/kwin/blob/master/src/plugins/screenshot/org.kde.KWin.ScreenShot2.xml
    private static async Task<Image> TakeScreenshotWithKwin()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync().ConfigureAwait(false);

        var screenShotService = new ScreenShot2Service(connection, "org.kde.KWin.ScreenShot2");
        var screenshot = screenShotService.CreateScreenShot2("/org/kde/KWin/ScreenShot2");

        var options = new Dictionary<string, VariantValue>
        {
            { "include-cursor", false },
            { "native-resolution", true }
        };

        var tempFile = Path.GetTempFileName();
        try
        {
            using var fileHandle = File.OpenHandle(tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.DeleteOnClose);

            var result = await screenshot.CaptureWorkspaceAsync(options, fileHandle).WaitAsync(cts.Token).ConfigureAwait(false);
            var expectedSize = (long)result.Stride * result.Height;

            using var fileCheckCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (new FileInfo(tempFile).Length < expectedSize)
            {
                await Task.Delay(50, fileCheckCts.Token).ConfigureAwait(false);
            }

            return await QImage.LoadAsync(tempFile, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The KWin screenshot operation or file write timed out.");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }

    private static Image CropFullscreenScreenshotToBounds(Rectangle bounds, Image img)
    {
        if (img == null)
        {
            DebugHelper.Logger?.Debug("Crop failed: Source image is null.");
            return null;
        }

        var x = Math.Clamp(bounds.X, 0, img.Width);
        var y = Math.Clamp(bounds.Y, 0, img.Height);

        var width = Math.Clamp(bounds.Width, 0, img.Width - x);
        var height = Math.Clamp(bounds.Height, 0, img.Height - y);

        if (width <= 0 || height <= 0)
        {
            DebugHelper.Logger?.Debug($"Crop aborted: Resulting bounds {width}x{height} are empty. Original image kept.");
            return img;
        }

        var cropRectangle = new Rectangle(x, y, width, height);

        DebugHelper.Logger?.Debug($"Cropping {img.Width}x{img.Height} image to {cropRectangle.Width}x{cropRectangle.Height} at offset {cropRectangle.X},{cropRectangle.Y}");

        try
        {
            img.Mutate(ctx => ctx.Crop(cropRectangle));
        }
        catch (Exception ex)
        {
            DebugHelper.Logger?.Debug($"ImageSharp Mutation Error: {ex.Message}");
        }

        return img;
    }
    public override async Task<Image?> CaptureScreen(Rectangle bounds)
    {
        var fullscreenImage = await CaptureFullscreen().ConfigureAwait(false);

        if (fullscreenImage == null)
        {
            DebugHelper.Logger?.Error("[LinuxCapture] Fullscreen capture returned null.");
            return null;
        }

        return CropFullscreenScreenshotToBounds(bounds, fullscreenImage);
    }

    public override async Task<Image?> CaptureScreen(Point? pos)
    {
        if (pos == null)
        {
            DebugHelper.Logger?.Error("[LinuxCapture] Position point was null.");
            throw new ArgumentNullException(nameof(pos));
        }

        if (LinuxAPI.IsWayland())
        {
            string? monitorName = await GetWaylandMonitorName(pos.Value).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(monitorName))
            {
                return await TakeScreenshotWithGrim(monitorName).ConfigureAwait(false);
            }
        }

        var rect = await GetScreen(pos.Value).ConfigureAwait(false);

        if (rect != Rectangle.Empty) return await CaptureScreen(rect).ConfigureAwait(false);
        DebugHelper.Logger?.Error("[LinuxCapture] Could not find screen at coordinates: {Point}", pos.Value);
        return null;

    }

    private static async Task<string?> GetWaylandMonitorName(Point position)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "hyprctl",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("monitors");
        startInfo.ArgumentList.Add("-j");

        using var process = Process.Start(startInfo);
        if (process is null) return null;
        string json = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) return null;

        // Hyprland reports its cursor in compositor logical coordinates. The
        // legacy position passed to this method can originate from XWayland,
        // where fractional-scale and rotated displays use a different space.
        // Prefer the compositor value so grim -o always receives the output
        // actually beneath the cursor.
        (double X, double Y)? cursorPosition = await GetHyprlandCursorPosition().ConfigureAwait(false);
        double positionX = cursorPosition?.X ?? position.X;
        double positionY = cursorPosition?.Y ?? position.Y;

        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement monitor in document.RootElement.EnumerateArray())
        {
            double x = monitor.GetProperty("x").GetDouble();
            double y = monitor.GetProperty("y").GetDouble();
            double scale = monitor.GetProperty("scale").GetDouble();
            int transform = monitor.GetProperty("transform").GetInt32();
            double physicalWidth = monitor.GetProperty("width").GetDouble();
            double physicalHeight = monitor.GetProperty("height").GetDouble();
            bool rotated = transform is 1 or 3;
            double logicalWidth = (rotated ? physicalHeight : physicalWidth) / scale;
            double logicalHeight = (rotated ? physicalWidth : physicalHeight) / scale;
            if (positionX >= x && positionX < x + logicalWidth &&
                positionY >= y && positionY < y + logicalHeight)
            {
                return monitor.GetProperty("name").GetString();
            }
        }

        foreach (JsonElement monitor in document.RootElement.EnumerateArray())
        {
            if (monitor.TryGetProperty("focused", out JsonElement focused) &&
                focused.GetBoolean() &&
                monitor.TryGetProperty("name", out JsonElement name))
            {
                return name.GetString();
            }
        }

        return null;
    }

    private static async Task<(double X, double Y)?> GetHyprlandCursorPosition()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "hyprctl",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("cursorpos");
        startInfo.ArgumentList.Add("-j");

        using var process = Process.Start(startInfo);
        if (process is null) return null;
        string json = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) return null;

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("x", out JsonElement x) ||
            !root.TryGetProperty("y", out JsonElement y))
        {
            return null;
        }

        return (x.GetDouble(), y.GetDouble());
    }
    public override async Task<Image?> CaptureScreen(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            DebugHelper.Logger?.Error("[LinuxCapture] Screen to capture was null or empty.");
            throw new ArgumentNullException(nameof(name));
        }

        if (LinuxAPI.IsWayland())
        {
            // Hyprland coordinates are logical coordinates.  A full-desktop image is
            // physical pixels, so cropping it with X11 bounds shifts scaled displays.
            // Let grim address the output by name instead.
            return await TakeScreenshotWithGrim(name).ConfigureAwait(false);
        }

        var rect = await GetScreen(name).ConfigureAwait(false);
        return await CaptureScreen(rect).ConfigureAwait(false);
    }

    public override async Task<Image?> CaptureScreen(Screen screen)
    {
        if (LinuxAPI.IsWayland() && !string.IsNullOrWhiteSpace(screen.Name))
        {
            return await TakeScreenshotWithGrim(screen.Name).ConfigureAwait(false);
        }

        var fullscreenImage = await CaptureFullscreen().ConfigureAwait(false);

        if (fullscreenImage == null)
        {
            DebugHelper.Logger?.Error("[LinuxCapture] Fullscreen capture returned null.");
            return null;
        }

        return CropFullscreenScreenshotToBounds(screen.Bounds, fullscreenImage);
    }

    public override async Task<Rectangle> GetScreen(Point pos) => Methods.NativeAPI.GetScreen(pos)?.Bounds ?? Rectangle.Empty;
    public override async Task<Rectangle> GetScreen(string name) => ((LinuxAPI)Methods.NativeAPI).GetScreen(name)?.Bounds ?? Rectangle.Empty;

    public override async Task<Rectangle> GetWorkingArea() => ((LinuxAPI)Methods.NativeAPI).GetScreenBounds();
    public override async Task<Image?> CaptureRectangle(Rectangle rect)
    {
        if (LinuxAPI.IsWayland())
        {
            // Geometry from slurp/Hyprland is expressed in compositor logical
            // coordinates. Let grim perform each output's scale/transform
            // conversion instead of cropping a mixed-scale desktop image.
            string geometry = $"{rect.X},{rect.Y} {rect.Width}x{rect.Height}";
            return await TakeScreenshotWithGrim(geometry: geometry).ConfigureAwait(false);
        }

        return CropFullscreenScreenshotToBounds(rect, await CaptureFullscreen().ConfigureAwait(false));
    }
    public override Task<Image?> CaptureWindow(WindowInfo window)
    {
        if (LinuxAPI.IsWayland())
        {
            return CaptureActiveWaylandWindow();
        }

        return Task.Run(() => ((LinuxAPI)Methods.NativeAPI).TakeScreenshotOfX11Window(window));
    }
    public override async Task<Image?> CaptureWindow(Point pos)
    {
        if (LinuxAPI.IsWayland())
        {
            return await CaptureActiveWaylandWindow().ConfigureAwait(false);
        }

        var windows = Methods.GetWindowList();

        var targetWindow = windows
            .Where(w => w is { Rectangle: { Width: > 0, Height: > 0 } })
            .Reverse()
            .FirstOrDefault(window => window.Rectangle.Contains(pos));

        if (targetWindow == null)
        {
            DebugHelper.Logger?.Debug($"No window found at {pos}");
            return null;
        }

        return await CaptureWindow(targetWindow);
    }

    private async Task<Image?> CaptureActiveWaylandWindow()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "hyprctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-j");
        startInfo.ArgumentList.Add("activewindow");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("SnapX could not start hyprctl to read the active window.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        string json = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"hyprctl could not read the active window: {error.Trim()}");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("at", out JsonElement at) || !root.TryGetProperty("size", out JsonElement size) ||
            at.GetArrayLength() < 2 || size.GetArrayLength() < 2)
        {
            return null;
        }

        var bounds = new Rectangle(at[0].GetInt32(), at[1].GetInt32(), size[0].GetInt32(), size[1].GetInt32());
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        // `hyprctl activewindow` and grim both use Hyprland's logical layout
        // coordinates.  grim performs the output scale and transform conversion;
        // cropping a full physical-pixel image here does not.
        string geometry = $"{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}";
        return await TakeScreenshotWithGrim(geometry: geometry).ConfigureAwait(false);
    }

    private static bool IsCompositorKwin => Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland" && Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") == "KDE";
}
