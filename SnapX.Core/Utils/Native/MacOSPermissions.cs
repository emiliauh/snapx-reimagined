// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace SnapX.Core.Utils.Native;

public enum MacOSPermissionKind
{
    ScreenRecording
}

public enum MacOSPermissionStatus
{
    Unavailable = -1,
    Denied,
    Authorized
}

public sealed class MacOSPermissionException : UnauthorizedAccessException
{
    public MacOSPermissionKind Permission { get; }
    public string SettingsUrl { get; }

    public MacOSPermissionException(MacOSPermissionKind permission)
        : base("SnapX needs Screen & System Audio Recording permission. Open System Settings > Privacy & Security > Screen & System Audio Recording, allow SnapX, then try again.")
    {
        Permission = permission;
        SettingsUrl = MacOSPermissions.GetSettingsUrl(permission);
    }
}

/// <summary>
/// Reads and requests the macOS screen-capture permission. ScreenCaptureKit
/// uses this same authorization for optional playback-audio capture; SnapX
/// never requests microphone access.
/// </summary>
public static partial class MacOSPermissions
{
    public const string ScreenRecordingSettingsUrl =
        "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture";

    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    public static bool IsSupported => OperatingSystem.IsMacOSVersionAtLeast(10, 15);

    /// <remarks>
    /// Core Graphics does not distinguish a first-time request from a denial,
    /// so every non-authorized state is reported as denied. This method never
    /// displays a system prompt.
    /// </remarks>
    public static MacOSPermissionStatus GetScreenCaptureStatus()
    {
        if (!IsSupported) return MacOSPermissionStatus.Unavailable;
        return CGPreflightScreenCaptureAccess()
            ? MacOSPermissionStatus.Authorized
            : MacOSPermissionStatus.Denied;
    }

    public static bool HasScreenCaptureAccess() =>
        GetScreenCaptureStatus() == MacOSPermissionStatus.Authorized;

    /// <summary>Displays the macOS capture prompt when the choice is not yet determined.</summary>
    public static bool RequestScreenCaptureAccess()
    {
        if (!IsSupported) return false;
        return CGRequestScreenCaptureAccess();
    }

    public static string GetSettingsUrl(MacOSPermissionKind permission) => permission switch
    {
        MacOSPermissionKind.ScreenRecording => ScreenRecordingSettingsUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(permission))
    };

    public static void ThrowIfScreenCaptureAccessDenied()
    {
        if (OperatingSystem.IsMacOS() && !HasScreenCaptureAccess())
            throw new MacOSPermissionException(MacOSPermissionKind.ScreenRecording);
    }

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CGPreflightScreenCaptureAccess();

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CGRequestScreenCaptureAccess();
}
