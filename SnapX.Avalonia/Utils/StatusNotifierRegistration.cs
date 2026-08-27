// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core;
using Tmds.DBus.Protocol;

namespace SnapX.Avalonia.Utils;

/// <summary>
/// Repairs StatusNotifierItem watcher registration for Avalonia's native
/// Wayland tray implementation. Avalonia still owns and serves the item.
/// </summary>
internal sealed class StatusNotifierRegistration : IDisposable
{
    private const string BusDestination = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const string BusInterface = "org.freedesktop.DBus";
    private const string WatcherDestination = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string WatcherInterface = "org.kde.StatusNotifierWatcher";
    private const string ItemPath = "/StatusNotifierItem";
    private const string ItemInterface = "org.kde.StatusNotifierItem";

    private readonly CancellationTokenSource _cancellation = new();
    private Task? _worker;

    public void Start()
    {
        if (_worker is not null || !OperatingSystem.IsLinux() ||
            string.IsNullOrWhiteSpace(DBusAddress.Session))
        {
            return;
        }

        _worker = Task.Run(() => MonitorAsync(_cancellation.Token));
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private static async Task MonitorAsync(CancellationToken cancellationToken)
    {
        DBusConnection? connection = null;
        string? itemService = null;
        bool reconnecting = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (connection is null)
                    {
                        connection = new DBusConnection(DBusAddress.Session!);
                        await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (itemService is null || !await IsSnapXItemAsync(connection, itemService).ConfigureAwait(false))
                    {
                        itemService = await FindSnapXItemAsync(connection).ConfigureAwait(false);
                    }

                    if (itemService is not null &&
                        !await IsRegisteredAsync(connection, itemService).ConfigureAwait(false))
                    {
                        await RegisterAsync(connection, itemService).ConfigureAwait(false);
                        DebugHelper.WriteLine(
                            "Registered native Wayland tray item {0}{1} with StatusNotifierWatcher.",
                            itemService,
                            ItemPath);
                    }

                    reconnecting = false;
                    await Task.Delay(TimeSpan.FromSeconds(itemService is null ? 1 : 10), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (DBusConnectionClosedException)
                {
                    connection?.Dispose();
                    connection = null;
                    itemService = null;
                    if (!reconnecting)
                    {
                        DebugHelper.WriteLine("Session D-Bus connection closed; reconnecting tray monitor.");
                        reconnecting = true;
                    }
                    await DelayAfterFailureAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.WriteException(ex, "Failed to register the native Wayland tray item");
                    await DelayAfterFailureAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private static async Task DelayAfterFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown while waiting to retry.
        }
    }

    private static async Task<string?> FindSnapXItemAsync(DBusConnection connection)
    {
        string[] names = await connection.CallMethodAsync(
            CreateMethodCall(connection, BusDestination, BusPath, BusInterface, "", "ListNames"),
            static (Message message, object? _) => message.GetBodyReader().ReadArrayOfString(),
            null).ConfigureAwait(false);

        foreach (string name in names)
        {
            if (!name.StartsWith(':'))
            {
                continue;
            }

            try
            {
                uint processId = await connection.CallMethodAsync(
                    CreateStringMethodCall(
                        connection,
                        BusDestination,
                        BusPath,
                        BusInterface,
                        "GetConnectionUnixProcessID",
                        name),
                    static (Message message, object? _) => message.GetBodyReader().ReadUInt32(),
                    null).ConfigureAwait(false);

                if (processId == Environment.ProcessId && await IsSnapXItemAsync(connection, name).ConfigureAwait(false))
                {
                    return name;
                }
            }
            catch (DBusErrorReplyException)
            {
                // The peer disappeared or does not export a StatusNotifierItem.
            }
        }

        return null;
    }

    private static async Task<bool> IsSnapXItemAsync(DBusConnection connection, string service)
    {
        try
        {
            string id = await connection.CallMethodAsync(
                CreateGetPropertyCall(connection, service, ItemPath, ItemInterface, "Id"),
                static (Message message, object? _) =>
                {
                    var reader = message.GetBodyReader();
                    reader.ReadSignature("s"u8);
                    return reader.ReadString();
                },
                null).ConfigureAwait(false);
            return string.Equals(id, Core.SnapXL.AppName, StringComparison.Ordinal);
        }
        catch (DBusErrorReplyException)
        {
            return false;
        }
    }

    private static async Task<bool> IsRegisteredAsync(DBusConnection connection, string service)
    {
        try
        {
            string[] items = await connection.CallMethodAsync(
                CreateGetPropertyCall(
                    connection,
                    WatcherDestination,
                    WatcherPath,
                    WatcherInterface,
                    "RegisteredStatusNotifierItems"),
                static (Message message, object? _) =>
                {
                    var reader = message.GetBodyReader();
                    reader.ReadSignature("as"u8);
                    return reader.ReadArrayOfString();
                },
                null).ConfigureAwait(false);
            return items.Contains($"{service}{ItemPath}", StringComparer.Ordinal);
        }
        catch (DBusErrorReplyException)
        {
            return false;
        }
    }

    private static Task RegisterAsync(DBusConnection connection, string service)
    {
        return connection.CallMethodAsync(
            CreateStringMethodCall(
                connection,
                WatcherDestination,
                WatcherPath,
                WatcherInterface,
                "RegisterStatusNotifierItem",
                service));
    }

    private static MessageBuffer CreateGetPropertyCall(
        DBusConnection connection,
        string destination,
        string path,
        string propertyInterface,
        string property)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: destination,
            path: path,
            @interface: "org.freedesktop.DBus.Properties",
            signature: "ss",
            member: "Get");
        writer.WriteString(propertyInterface);
        writer.WriteString(property);
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateStringMethodCall(
        DBusConnection connection,
        string destination,
        string path,
        string @interface,
        string member,
        string value)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: destination,
            path: path,
            @interface: @interface,
            member: member,
            signature: "s");
        writer.WriteString(value);
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateMethodCall(
        DBusConnection connection,
        string destination,
        string path,
        string @interface,
        string signature,
        string member)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: destination,
            path: path,
            @interface: @interface,
            member: member,
            signature: signature);
        return writer.CreateMessage();
    }
}
