// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.Hotkey;

public static class HotkeyBackendFactory
{
    public static IHotkeyBackend CreateDefault(HotkeyBackendPreference preference = HotkeyBackendPreference.Automatic)
    {
        if (preference == HotkeyBackendPreference.Disabled)
        {
            return new UnavailableHotkeyBackend("Keyboard shortcuts are off in Settings.", "Disabled");
        }

        if (preference == HotkeyBackendPreference.WaylandPortal)
        {
            if (!OperatingSystem.IsLinux() || !IsWaylandEnvironment())
            {
                return new UnavailableHotkeyBackend(
                    "The Wayland portal needs a Linux Wayland session.",
                    "Wayland portal (not available)");
            }

            var portal = new PortalGlobalHotkeyBackend();
            return portal.IsAvailable
                ? portal
                : new UnavailableHotkeyBackend(
                    portal.AvailabilityError ?? "The Wayland keyboard shortcut portal is not available.",
                    "Wayland portal (not available)");
        }

        if (preference == HotkeyBackendPreference.X11)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                return new UnavailableHotkeyBackend(
                    "No X11 display is available.",
                    "X11 (not available)");
            }

            var requestedX11 = new X11HotkeyBackend();
            if (requestedX11.IsAvailable) return requestedX11;

            string requestedError = requestedX11.AvailabilityError ?? "SnapX could not start the X11 keyboard shortcut service.";
            requestedX11.Dispose();
            return new UnavailableHotkeyBackend(requestedError, "X11 (not available)");
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsHotkeyBackend();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(waylandDisplay) &&
                 !string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase)))
            {
                var portal = new PortalGlobalHotkeyBackend();
                if (portal.IsAvailable)
                {
                    return portal;
                }

                return new UnavailableHotkeyBackend(
                    portal.AvailabilityError ?? "The Wayland keyboard shortcut portal is not available.",
                    "Wayland (not available)");
            }

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                return new UnavailableHotkeyBackend(
                    "No X11 display is available. Keyboard shortcuts are off for this session.",
                    "X11 (not available)");
            }

            var backend = new X11HotkeyBackend();
            if (backend.IsAvailable)
            {
                return backend;
            }

            string error = backend.AvailabilityError ?? "SnapX could not start the X11 keyboard shortcut service.";
            backend.Dispose();
            return new UnavailableHotkeyBackend(error, "X11 (not available)");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOSHotkeyBackend();
        }

        return new UnavailableHotkeyBackend(
            "This operating system does not have a keyboard shortcut service for SnapX.");
    }

    private static bool IsWaylandEnvironment()
    {
        string? sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        return string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) ||
            (!string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase) &&
             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));
    }
}
