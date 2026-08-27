// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.Hotkey;

public static class HotkeyBackendFactory
{
    public static IHotkeyBackend CreateDefault(HotkeyBackendPreference preference = HotkeyBackendPreference.Automatic)
    {
        if (preference == HotkeyBackendPreference.Disabled)
        {
            return new UnavailableHotkeyBackend("Global hotkeys are disabled in settings.", "Disabled");
        }

        if (preference == HotkeyBackendPreference.WaylandPortal)
        {
            if (!OperatingSystem.IsLinux() || !IsWaylandEnvironment())
            {
                return new UnavailableHotkeyBackend(
                    "The Wayland portal backend requires a Linux Wayland session.",
                    "Wayland portal (unavailable)");
            }

            var portal = new PortalGlobalHotkeyBackend();
            return portal.IsAvailable
                ? portal
                : new UnavailableHotkeyBackend(
                    portal.AvailabilityError ?? "The GlobalShortcuts portal is unavailable.",
                    "Wayland portal (unavailable)");
        }

        if (preference == HotkeyBackendPreference.X11)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                return new UnavailableHotkeyBackend(
                    "No X11 display is available.",
                    "X11 (unavailable)");
            }

            var requestedX11 = new X11HotkeyBackend();
            if (requestedX11.IsAvailable) return requestedX11;

            string requestedError = requestedX11.AvailabilityError ?? "The X11 hotkey backend is unavailable.";
            requestedX11.Dispose();
            return new UnavailableHotkeyBackend(requestedError, "X11 (unavailable)");
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
                    portal.AvailabilityError ?? "The GlobalShortcuts portal is unavailable.",
                    "Wayland (unavailable)");
            }

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                return new UnavailableHotkeyBackend(
                    "No X11 DISPLAY is available. Global hotkeys are disabled for this session.",
                    "X11 (unavailable)");
            }

            var backend = new X11HotkeyBackend();
            if (backend.IsAvailable)
            {
                return backend;
            }

            string error = backend.AvailabilityError ?? "The X11 hotkey backend could not be initialized.";
            backend.Dispose();
            return new UnavailableHotkeyBackend(error, "X11 (unavailable)");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new UnavailableHotkeyBackend(
                "Global hotkey registration is not implemented for macOS.",
                "macOS (unsupported)");
        }

        return new UnavailableHotkeyBackend(
            "No global hotkey backend is available for this operating system yet.");
    }

    private static bool IsWaylandEnvironment()
    {
        string? sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        return string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) ||
            (!string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase) &&
             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));
    }
}
