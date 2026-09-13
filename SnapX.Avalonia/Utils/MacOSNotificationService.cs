// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using SnapX.Core;

namespace SnapX.Avalonia.Utils;

/// <summary>Notification Center delivery for the packaged macOS application, including tray operation.</summary>
internal static unsafe partial class MacOSNotificationService
{
    private const string Library = "snapx-notifications";
    private static readonly ConcurrentDictionary<long, Action> ClickActions = new();
    private static readonly ConcurrentQueue<long> ActionOrder = new();
    private static long nextToken = DateTime.UtcNow.Ticks;

    public static bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsMacOSVersionAtLeast(11)) return false;
            try { return NativeAvailable() != 0; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                DebugHelper.WriteException(ex, "macOS notifications are not available in this build");
                return false;
            }
        }
    }

    /// <summary>Queues delivery without waiting for authorization or the native notification service.</summary>
    public static bool Send(string title, string message, Action? onClick = null)
    {
        if (!IsAvailable) return false;
        long token = Interlocked.Increment(ref nextToken);
        if (onClick is not null)
        {
            ClickActions[token] = onClick;
            ActionOrder.Enqueue(token);
            // Notification Center may retain notifications beyond this app session.
            // Bound managed closures; older notifications still open SnapX normally.
            while (ActionOrder.Count > 256 && ActionOrder.TryDequeue(out long oldest))
                ClickActions.TryRemove(oldest, out _);
        }
        try
        {
            NativeSend(title, message, token, &OnResult);
            return true;
        }
        catch (Exception ex)
        {
            ClickActions.TryRemove(token, out _);
            DebugHelper.WriteException(ex, "SnapX could not add the macOS notification to the queue");
            return false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResult(long token, int status)
    {
        try
        {
            if (status == 1) return;
            ClickActions.TryRemove(token, out Action? action);
            if (status == 2 && action is not null)
                Dispatcher.UIThread.Post(() =>
                {
                    try { action(); }
                    catch (Exception ex) { DebugHelper.WriteException(ex, "macOS notification action failed"); }
                });
            else if (status <= 0)
                DebugHelper.WriteLine(status == 0
                    ? "Notifications are off for SnapX. In System Settings, go to Notifications, and then enable SnapX notifications."
                    : "macOS could not show the SnapX notification.");
        }
        catch { /* Managed exceptions must never unwind through the native callback. */ }
    }

    [LibraryImport(Library, EntryPoint = "snapx_notifications_available")]
    private static partial int NativeAvailable();

    [LibraryImport(Library, EntryPoint = "snapx_notifications_send", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void NativeSend(string title, string body, long token,
        delegate* unmanaged[Cdecl]<long, int, void> callback);
}
