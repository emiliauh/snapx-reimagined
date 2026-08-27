// SPDX-License-Identifier: GPL-3.0-or-later

using Tmds.DBus.Protocol;

namespace SnapX.Core.SharpCapture.Linux.DBus;

/// <summary>
/// Low-level binding for the host application registry introduced by
/// xdg-desktop-portal. Unsandboxed applications use it to associate their
/// D-Bus peer with the desktop application ID used by portal requests.
/// </summary>
internal static class HostPortalRegistry
{
    // The host registry is exported by the desktop portal service itself.
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string Path = "/org/freedesktop/portal/desktop";
    private const string Interface = "org.freedesktop.host.portal.Registry";

    public static Task RegisterAsync(DBusConnection connection, string applicationId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        return connection.CallMethodAsync(CreateMessage());

        MessageBuffer CreateMessage()
        {
            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: Destination,
                path: Path,
                @interface: Interface,
                signature: "sa{sv}",
                member: "Register");
            writer.WriteString(applicationId);
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            return writer.CreateMessage();
        }
    }
}
