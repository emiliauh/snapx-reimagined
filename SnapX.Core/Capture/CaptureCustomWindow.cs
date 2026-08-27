// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.Job;
using SnapX.Core.Media;
using SnapX.Core.Utils.Native;

namespace SnapX.Core.Capture;

public class CaptureCustomWindow : CaptureWindow
{
    protected override TaskMetadata Execute(TaskSettings taskSettings)
    {
        string windowQuery = taskSettings.CaptureSettings.CaptureCustomWindow?.Trim() ?? string.Empty;
        if (windowQuery.Length == 0)
        {
            throw new InvalidOperationException("A custom-window title or process name was not configured.");
        }

        List<WindowInfo> windows = Methods.GetWindowList();
        bool isWayland = OperatingSystem.IsLinux()
            && (LinuxAPI.IsWayland()
                || string.Equals(
                    Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                    "wayland",
                    StringComparison.OrdinalIgnoreCase));
        if (windows.Count == 0 && isWayland)
        {
            throw new PlatformNotSupportedException(
                "Custom-window lookup is unavailable because this Wayland compositor does not expose a capturable XWayland window list.");
        }

        WindowInfo? window = windows
            .Where(IsCapturable)
            .OrderByDescending(candidate => candidate.IsActive)
            .ThenBy(candidate => candidate.Title.Length)
            .FirstOrDefault(candidate => candidate.Title.Equals(windowQuery, StringComparison.OrdinalIgnoreCase))
            ?? windows
                .Where(IsCapturable)
                .OrderByDescending(candidate => candidate.IsActive)
                .ThenBy(candidate => candidate.Title.Length)
                .FirstOrDefault(candidate => candidate.ProcessName.Equals(windowQuery, StringComparison.OrdinalIgnoreCase))
            ?? windows
                .Where(IsCapturable)
                .OrderByDescending(candidate => candidate.IsActive)
                .ThenBy(candidate => candidate.Title.Length)
                .FirstOrDefault(candidate => candidate.Title.Contains(windowQuery, StringComparison.OrdinalIgnoreCase));

        if (window == null)
        {
            throw new InvalidOperationException($"Unable to find a capturable window matching '{windowQuery}'.");
        }

        WindowHandle = window.Handle;
        var image = Methods.CaptureWindow(window).GetAwaiter().GetResult();
        if (image == null)
        {
            throw new InvalidOperationException($"Capturing window '{window.Title}' returned no image.");
        }

        var metadata = new TaskMetadata(image);
        metadata.UpdateInfo(window);
        return metadata;
    }

    private static bool IsCapturable(WindowInfo window) =>
        window.IsVisible && window.Handle != IntPtr.Zero && window.Rectangle.Width > 0 && window.Rectangle.Height > 0;
}
