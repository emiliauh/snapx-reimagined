// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.Media;

namespace SnapX.Avalonia.Utils;

/// <summary>
/// Adds the macOS interaction Avalonia's TrayIcon does not currently expose:
/// an ordinary left click can stop a recording while right click keeps the
/// attached native menu. Other platforms continue through TrayIcon.Clicked.
/// </summary>
internal static unsafe partial class MacOSTrayClickService
{
    private const string Library = "snapx-tray";
    private static bool initialized;

    public static bool Initialize()
    {
        if (!OperatingSystem.IsMacOS()) return false;

        try
        {
            initialized = NativeInitialize(&OnPrimaryClick) != 0;
            return initialized;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            DebugHelper.WriteException(ex, "macOS tray click support is unavailable in this build");
            return false;
        }
    }

    public static void SetRecordingClickEnabled(bool enabled)
    {
        if (!initialized) return;

        try
        {
            NativeSetRecordingEnabled(enabled ? 1 : 0);
        }
        catch (Exception ex)
        {
            initialized = false;
            DebugHelper.WriteException(ex, "Failed to update macOS tray click behavior");
        }
    }

    public static void Dispose()
    {
        if (!initialized) return;
        initialized = false;

        try
        {
            NativeShutdown();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to remove macOS tray click support");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPrimaryClick()
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (ScreenRecordManager.CanStopInteractively)
                        TaskHelpers.StopScreenRecording();
                }
                catch (Exception ex)
                {
                    DebugHelper.WriteException(ex, "macOS tray stop action failed");
                }
            });
        }
        catch
        {
            // Native callbacks must never allow a managed exception to unwind.
        }
    }

    [LibraryImport(Library, EntryPoint = "snapx_tray_initialize")]
    private static partial int NativeInitialize(delegate* unmanaged[Cdecl]<void> callback);

    [LibraryImport(Library, EntryPoint = "snapx_tray_set_recording_enabled")]
    private static partial void NativeSetRecordingEnabled(int enabled);

    [LibraryImport(Library, EntryPoint = "snapx_tray_shutdown")]
    private static partial void NativeShutdown();
}
