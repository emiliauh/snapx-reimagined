// SPDX-License-Identifier: GPL-3.0-or-later

using Tmds.DBus;
using Tmds.DBus.Protocol;
using SnapX.Core.Utils.Miscellaneous;

namespace SnapX.Core;

/// <summary>
/// Sends desktop notifications through the freedesktop Notifications D-Bus
/// interface (org.freedesktop.Notifications). This is the native Wayland/DBus
/// path used by Hyprland shells (e.g. quickshell) and replaces reliance on
/// only the in-process Avalonia toast window, which can be hidden behind
/// fullscreen windows or a compositor that does not surface Avalonia windows.
/// </summary>
public sealed class DesktopNotificationService : IAsyncDisposable
{
    private const string Destination = "org.freedesktop.Notifications";
    private const string NotificationsPath = "/org/freedesktop/Notifications";
    private const string NotificationsInterface = "org.freedesktop.Notifications";
    // Keep the notification's application identity identical to the desktop
    // filename and xdg_toplevel.app_id. Wayland shells use this identity to
    // associate a notification with its task-view/dock entry; using the human
    // label "SnapX" here left those systems with two unrelated app keys.
    private const string ApplicationId = Links.APP_ID;
    private const string ApplicationIcon = Links.APP_ID;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DBusConnection? _connection;
    private bool _disposed;
    private uint _nextId = 1;

    public bool IsAvailable => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(DBusAddress.Session);

    public async Task<uint> NotifyAsync(
        string summary,
        string body,
        string? appIcon = null,
        uint replacesId = 0,
        TimeSpan? timeout = null,
        IReadOnlyList<(string key, string value)>? actions = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            uint id = replacesId != 0 ? replacesId : _nextId++;

            var hints = new Dictionary<string, VariantValue>
            {
                ["sender-pid"] = (long)Environment.ProcessId,
                // Freedesktop's desktop-entry hint is the desktop filename
                // without its .desktop suffix. Supplying it lets shells that
                // prefer desktop-entry lookup resolve the same SnapX task
                // entry even if they do not use app_name directly.
                ["desktop-entry"] = ApplicationId
            };

            return await connection.CallMethodAsync(
                CreateMessage(connection, id, summary, body, appIcon, timeout, actions, hints),
                (Message m, object? _) => ReadMessage_u(m),
                this).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex)
        {
            DebugHelper.WriteException(ex, "Failed to send desktop notification");
            return 0;
        }
        finally
        {
            _gate.Release();
        }

        MessageBuffer CreateMessage(DBusConnection c, uint id, string s, string b, string? icon, TimeSpan? t, IReadOnlyList<(string key, string value)>? a, Dictionary<string, VariantValue> h)
        {
            var writer = c.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: Destination,
                path: NotificationsPath,
                @interface: NotificationsInterface,
                signature: "susssasa{sv}i",
                member: "Notify");

            writer.WriteString(ApplicationId);
            writer.WriteUInt32(id);
            writer.WriteString(icon ?? ApplicationIcon);
            writer.WriteString(s);
            writer.WriteString(b);
            writer.WriteArray(Array.Empty<string>());
            writer.WriteDictionary(h);
            writer.WriteInt32(t.HasValue ? (int)t.Value.TotalMilliseconds : -1);
            return writer.CreateMessage();
        }
    }

    public async Task CloseNotificationAsync(uint id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (id == 0) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            await connection.CallMethodAsync(CreateMessage(connection, id)).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex)
        {
            DebugHelper.WriteException(ex, "Failed to close desktop notification");
        }
        finally
        {
            _gate.Release();
        }

        MessageBuffer CreateMessage(DBusConnection c, uint id)
        {
            var writer = c.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: Destination,
                path: NotificationsPath,
                @interface: NotificationsInterface,
                signature: "s",
                member: "CloseNotification");
            writer.WriteString(id.ToString());
            return writer.CreateMessage();
        }
    }

    private async Task<DBusConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { } existing)
        {
            return existing;
        }

        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "Desktop notifications require a Linux session D-Bus address.");
        }

        var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        _connection = connection;
        return connection;
    }

    private static uint ReadMessage_u(Message message)
    {
        var reader = message.GetBodyReader();
        return reader.ReadUInt32();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_connection is not null)
        {
            _connection.Dispose();
            _connection = null;
        }
        _gate.Dispose();
    }
}
