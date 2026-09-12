using System.Diagnostics;
using System.Globalization;
using SixLabors.ImageSharp;
using SnapX.Core.Media;
using SnapX.Core.Utils.Native;

namespace SnapX.Core.SharpCapture.macOS;

public class macOSCapture : BaseCapture
{
    public override Task<Image?> CaptureFullscreen() => CaptureRectangle(MacOSAPI.GetDesktopBounds());

    public override Task<Image?> CaptureScreen(Screen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return CaptureRectangle(screen.Bounds);
    }

    public override Task<Image?> CaptureScreen(Rectangle bounds) => CaptureRectangle(bounds);

    public override Task<Image?> CaptureScreen(Point? pos) =>
        CaptureRectangle(FindScreen(pos ?? Methods.GetCursorPosition()).Bounds);

    public override Task<Image?> CaptureScreen(string name) => CaptureRectangle(FindScreen(name).Bounds);

    public override Task<Image?> CaptureRectangle(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(rect), "Capture bounds must have positive dimensions.");
        var bounds = Rectangle.Intersect(rect, MacOSAPI.GetDesktopBounds());
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(rect), "Capture bounds do not intersect a display.");
        return CaptureNative("-R" + string.Create(CultureInfo.InvariantCulture,
            $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}"));
    }

    public override Task<Image?> CaptureWindow(Point pos)
    {
        var window = new MacOSAPI().GetWindowList().FirstOrDefault(w => w.IsVisible && w.Rectangle.Contains(pos))
            ?? throw new InvalidOperationException("No window exists at the requested position.");
        return CaptureWindow(window);
    }

    public override Task<Image?> CaptureWindow(WindowInfo windowInfo)
    {
        ArgumentNullException.ThrowIfNull(windowInfo);
        return CaptureNative("-l" + windowInfo.Handle.ToInt64().ToString(CultureInfo.InvariantCulture), "-o");
    }

    public override Task<Rectangle> GetWorkingArea() => Task.FromResult(MacOSAPI.GetDesktopBounds());
    public override Task<Rectangle> GetPrimaryScreen() => Task.FromResult(MacOSAPI.GetScreens().First(s => s.IsPrimary).Bounds);
    public override Task<Rectangle> GetScreen(Point pos) => Task.FromResult(FindScreen(pos).Bounds);
    public override Task<Rectangle> GetScreen(string name) => Task.FromResult(FindScreen(name).Bounds);

    private static Screen FindScreen(Point pos) => MacOSAPI.GetScreens().FirstOrDefault(s => s.Bounds.Contains(pos))
        ?? throw new ArgumentOutOfRangeException(nameof(pos), "No screen contains the requested position.");
    private static Screen FindScreen(string name) => MacOSAPI.GetScreens().FirstOrDefault(s =>
        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) || s.Id == name)
        ?? throw new ArgumentException("No screen matches the requested name.", nameof(name));

    private static async Task<Image?> CaptureNative(params string[] arguments)
    {
        // The Rust fullscreen compositor assumes a 1:1 point/pixel ratio and panics on Retina.
        // Apple's capture utility supports signed global coordinates and native backing pixels.
        string path = Path.Combine(Path.GetTempPath(), $"snapx-capture-{Guid.NewGuid():N}.png");
        try
        {
            var info = new ProcessStartInfo("/usr/sbin/screencapture")
            {
                UseShellExecute = false,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-x");
            info.ArgumentList.Add("-t");
            info.ArgumentList.Add("png");
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            info.ArgumentList.Add(path);
            using var process = Process.Start(info) ?? throw new IOException("Unable to start macOS screen capture.");
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                throw;
            }
            var message = await error.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(path))
                throw new IOException($"macOS screen capture failed. Check Screen Recording permission. {message}");
            return await Image.LoadAsync(path).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
