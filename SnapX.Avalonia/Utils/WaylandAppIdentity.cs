using System.Reflection;
using Avalonia.Controls;
using SnapX.Core;

namespace SnapX.Avalonia.Utils;

/// <summary>
/// Supplies the xdg_toplevel app_id missing from Avalonia.Wayland 12.1.x.
///
/// Avalonia has a public AppId option under upstream review, but the pinned
/// runtime exposes only the underlying NWayland call. This bridge is narrowly
/// version-gated by the concrete backend shape and dispatches through its own
/// Wayland worker, so it is a no-op on every other platform/backend.
/// </summary>
internal static class WaylandAppIdentity
{
    private const string AppId = "io.github.SnapXL.SnapX";

    public static bool TrySet(TopLevel topLevel)
    {
        if (!OperatingSystem.IsLinux() ||
            !string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            object? platformImpl = topLevel.PlatformImpl;
            if (platformImpl?.GetType().FullName != "Avalonia.Wayland.WindowImpl")
            {
                return false;
            }

            const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
            object? surfaceProxy = platformImpl.GetType()
                .GetField("_surfaceProxy", instanceFields)
                ?.GetValue(platformImpl);
            object? persistentSurface = surfaceProxy?.GetType()
                .GetField("_target", instanceFields)
                ?.GetValue(surfaceProxy);
            object? xdgTopLevel = persistentSurface?.GetType()
                .GetField("_xdgTopLevel", instanceFields)
                ?.GetValue(persistentSurface);
            MethodInfo? setAppId = xdgTopLevel?.GetType().GetMethod("SetAppId", [typeof(string)]);
            Delegate? marshaller = surfaceProxy?.GetType()
                .GetField("_marshaller", instanceFields)
                ?.GetValue(surfaceProxy) as Delegate;

            if (xdgTopLevel is null || setAppId is null || marshaller is null)
            {
                DebugHelper.WriteLine("Wayland app-id bridge was not ready; retrying after the main window opens.");
                return false;
            }

            // WXdgTopLevelProxy is deliberately a UI-to-Wayland-thread bridge.
            // Use its private dispatcher rather than invoking NWayland from the
            // Avalonia UI thread, which would race the connection event loop.
            Action request = () =>
            {
                try
                {
                    setAppId.Invoke(xdgTopLevel, [AppId]);
                }
                catch (Exception ex)
                {
                    DebugHelper.WriteException(ex, "Failed to send Wayland xdg_toplevel app_id");
                }
            };
            Type priorityType = marshaller.GetType().GetMethod("Invoke")!
                .GetParameters()[1].ParameterType;
            object priority = Enum.Parse(priorityType, "Normal");
            marshaller.DynamicInvoke(request, priority);
            DebugHelper.WriteLine($"Requested native Wayland app_id: {AppId}");
            return true;
        }
        catch (Exception ex)
        {
            // The bridge is optional compatibility code. Do not allow a future
            // Avalonia private-layout change to prevent SnapX from starting.
            DebugHelper.WriteException(ex, "Wayland app-id bridge is unavailable");
            return false;
        }
    }
}
