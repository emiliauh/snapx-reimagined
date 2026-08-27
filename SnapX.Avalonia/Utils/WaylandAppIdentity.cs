// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using SnapX.Core;

namespace SnapX.Avalonia.Utils;

/// <summary>
/// Supplies the xdg_toplevel app_id missing from Avalonia.Wayland 12.1.x.
///
/// Avalonia's persistent xdg-toplevel wrapper exposes no SetAppId method,
/// so this bridge reaches the raw NWayland XdgToplevel protocol object
/// (WindowImpl -> WXdgTopLevelProxy -> WXdgShellSurfaceProxy._target ->
/// WXdgTopLevel._xdgTopLevel) and invokes its SetAppId through the
/// wrapper's Wayland-thread marshaller. Reflection targets are pinned with
/// DynamicDependency because the shipped binary is AOT-compiled with full
/// IL trimming. This is a no-op on every other platform/backend.
/// </summary>
internal static class WaylandAppIdentity
{
    private const string AppId = "io.emiliauh.SnapXL.SnapX";
    private const int MaximumAttempts = 10;
    private const int RetryIntervalMilliseconds = 250;

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "Avalonia.Wayland.WindowImpl", "Avalonia.Wayland")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "Avalonia.Wayland.Server.Persistent.WXdgShellSurfaceProxy", "Avalonia.Wayland")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "Avalonia.Wayland.Server.Persistent.WXdgTopLevel", "Avalonia.Wayland")]
    [DynamicDependency("SetAppId", "NWayland.Protocols.XdgShell.XdgToplevel", "NWayland")]
    public static void Attach(TopLevel topLevel)
    {
        if (!IsWaylandSession())
        {
            return;
        }

        // The xdg surface is configured shortly after the platform window is
        // created; sending app_id before the first commit is protocol-ideal,
        // so try immediately and retry briefly until the bridge is ready.
        if (TrySet(topLevel))
        {
            return;
        }

        int attempts = 1;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RetryIntervalMilliseconds) };
        timer.Tick += (_, _) =>
        {
            if (TrySet(topLevel) || ++attempts >= MaximumAttempts)
            {
                timer.Stop();
                if (attempts >= MaximumAttempts)
                {
                    DebugHelper.WriteLine("Wayland app-id bridge gave up after {0} attempts.", attempts);
                }
            }
        };
        timer.Start();
    }

    private static bool TrySet(TopLevel topLevel)
    {
        if (!IsWaylandSession())
        {
            return false;
        }

        try
        {
            object? platformImpl = topLevel.PlatformImpl;
            if (platformImpl?.GetType().FullName != "Avalonia.Wayland.WindowImpl")
            {
                return NotReady("platform implementation is not the Avalonia Wayland WindowImpl");
            }

            const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
            object? surfaceProxy = platformImpl.GetType()
                .GetField("_surfaceProxy", instanceFields)
                ?.GetValue(platformImpl);
            if (surfaceProxy is null)
            {
                return NotReady("WindowImpl._surfaceProxy");
            }

            object? persistentTopLevel = surfaceProxy.GetType()
                .GetField("_target", instanceFields)
                ?.GetValue(surfaceProxy);
           if (persistentTopLevel is null)
           {
                var fields = string.Join(", ", surfaceProxy.GetType()
                    .GetFields(instanceFields)
                    .Select(f => f.FieldType.FullName + " " + f.Name));
                return NotReady("proxy._target (fields: " + fields + ")");
           }

            object? xdgTopLevel = persistentTopLevel.GetType()
                .GetField("_xdgTopLevel", instanceFields)
                ?.GetValue(persistentTopLevel);
            if (xdgTopLevel is null)
            {
                return NotReady("persistent top level._xdgTopLevel");
            }

            MethodInfo? setAppId = xdgTopLevel.GetType().GetMethod("SetAppId", [typeof(string)]);
            if (setAppId is null)
            {
                return NotReady("NWayland XdgToplevel.SetAppId");
            }

            Delegate? marshaller = surfaceProxy.GetType()
                .GetField("_marshaller", instanceFields)
                ?.GetValue(surfaceProxy) as Delegate;
            if (marshaller is null)
            {
                return NotReady("proxy._marshaller");
            }

            // WXdgShellSurfaceProxy._marshaller is deliberately a
            // UI-to-Wayland-thread bridge. Use it rather than invoking
            // NWayland from the Avalonia UI thread, which would race the
            // connection event loop. Its second parameter is the private
            // WaylandDispatchPriority enum; resolve it dynamically.
            Type priorityType = marshaller.GetType().GetMethod("Invoke")!
                .GetParameters()[1].ParameterType;
            object priority = Enum.Parse(priorityType, "Normal");
            Action request = () =>
            {
                try
                {
                    setAppId.Invoke(xdgTopLevel, [AppId]);
                    DebugHelper.WriteLine($"Requested native Wayland app_id: {AppId}");
                }
                catch (Exception ex)
                {
                    DebugHelper.WriteException(ex, "Failed to send Wayland xdg_toplevel app_id");
                }
            };
            marshaller.DynamicInvoke(request, priority);
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

    private static bool NotReady(string missing)
    {
        DebugHelper.WriteLine($"Wayland app-id bridge was not ready ({missing}); retrying.");
        return false;
    }

    private static bool IsWaylandSession() =>
        OperatingSystem.IsLinux() &&
        (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase) ||
         !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));
}
