// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.SharpCapture.Linux.DBus;
using SnapX.Core.Utils;
using Tmds.DBus;
using Tmds.DBus.Protocol;

namespace SnapX.Core.Hotkey;

/// <summary>
/// Registers global shortcuts through the freedesktop GlobalShortcuts portal.
/// This is the native global-hotkey path for Wayland sessions.
/// </summary>
internal sealed class PortalGlobalHotkeyBackend : IHotkeyBackend
{
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string DesktopPath = "/org/freedesktop/portal/desktop";
    private const string DefaultApplicationId = "io.github.SnapXL.SnapX";
    private const string ApplicationIdEnvironmentVariable = "SNAPX_DESKTOP_APP_ID";

    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private DBusConnection? connection;
    private IDisposable? activationSubscription;
    private Session? globalShortcutsSession;
    private bool disposed;

    public event Action<string>? Activated;

    public string Name => "freedesktop GlobalShortcuts portal";

    public bool IsAvailable => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(DBusAddress.Session);

    public string? AvailabilityError => IsAvailable
        ? null
        : "The freedesktop GlobalShortcuts portal requires a Linux session D-Bus address.";

    public async Task<IReadOnlyDictionary<string, HotkeyBackendRegistrationResult>> RegisterAsync(
        IReadOnlyCollection<HotkeyRegistration> registrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (!IsAvailable)
            {
                string error = AvailabilityError ?? "The GlobalShortcuts portal is unavailable.";
                return registrations.ToDictionary(
                    registration => registration.Id,
                    _ => HotkeyBackendRegistrationResult.Failure(error),
                    StringComparer.Ordinal);
            }

            await UnregisterCoreAsync().ConfigureAwait(false);
            if (registrations.Count == 0)
            {
                return new Dictionary<string, HotkeyBackendRegistrationResult>(StringComparer.Ordinal);
            }

            using var registrationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            registrationTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            CancellationToken portalCancellationToken = registrationTimeout.Token;

            var newConnection = new DBusConnection(DBusAddress.Session!);
            try
            {
                await newConnection.ConnectAsync().AsTask().WaitAsync(portalCancellationToken).ConfigureAwait(false);
                string? hostApplicationId = ResolveHostApplicationId();
                if (hostApplicationId is not null)
                {
                    await RegisterHostApplicationAsync(
                        newConnection,
                        hostApplicationId,
                        portalCancellationToken).ConfigureAwait(false);
                }

                var desktop = new DesktopService(newConnection, Destination);
                var globalShortcuts = desktop.CreateGlobalShortcuts(new ObjectPath(DesktopPath));
                string token = $"snapx_{Environment.ProcessId}_{Guid.NewGuid():N}";

                PortalResponse sessionResponse;
                try
                {
                    sessionResponse = await newConnection.Call(
                        () => globalShortcuts.CreateSessionAsync(new Dictionary<string, VariantValue>
                        {
                            ["handle_token"] = token,
                            ["session_handle_token"] = $"{token}_session"
                        }), portalCancellationToken).ConfigureAwait(false);
                }
                catch (DBusErrorReplyException ex) when (
                    ex.ErrorName == "org.freedesktop.portal.Error.NotAllowed" &&
                    hostApplicationId is null)
                {
                    throw new InvalidOperationException(
                        $"The portal could not identify SnapX. Install {DefaultApplicationId}.desktop, " +
                        $"or set {ApplicationIdEnvironmentVariable} to an installed desktop application ID " +
                        "for a development launch.",
                        ex);
                }

                if (sessionResponse.ResponseCode != 0)
                {
                    throw new InvalidOperationException(
                        $"The desktop denied the global shortcut session (response {sessionResponse.ResponseCode}).");
                }

                if (!sessionResponse.Results.TryGetValue("session_handle", out VariantValue sessionValue))
                {
                    throw new InvalidOperationException("The GlobalShortcuts portal did not return a session handle.");
                }

                string sessionHandlePath = sessionValue.Type switch
                {
                    VariantValueType.ObjectPath => sessionValue.GetObjectPathAsString(),
                    VariantValueType.String => sessionValue.GetString(),
                    _ => throw new InvalidOperationException(
                        $"The GlobalShortcuts portal returned an unsupported session handle type: {sessionValue.Type}.")
                };
                var sessionHandle = new ObjectPath(sessionHandlePath);
                var newSession = desktop.CreateSession(sessionHandle);
                var newSubscription = await globalShortcuts.WatchActivatedAsync(
                    (error, signal) =>
                    {
                        if (error is not null)
                        {
                            DebugHelper.WriteException(error, "GlobalShortcuts portal activation signal failed");
                            return;
                        }

                        if (signal.SessionHandle.Equals(sessionHandle))
                        {
                            Activated?.Invoke(signal.ShortcutId);
                        }
                    }, emitOnCapturedContext: false).ConfigureAwait(false);

                var definitions = registrations
                    .Select(registration =>
                        (registration.Id, new Dictionary<string, VariantValue>
                        {
                            ["description"] = registration.HotkeyInfo.ToString(),
                            ["preferred_trigger"] = registration.Accelerator
                        }))
                    .ToArray();

                PortalResponse bindResponse = await PortalResponse.WaitAsync(
                    newConnection,
                    () => globalShortcuts.BindShortcutsAsync(
                        sessionHandle,
                        definitions,
                        string.Empty,
                        new Dictionary<string, VariantValue>
                        {
                            ["handle_token"] = $"{token}_bind"
                        }),
                    portalCancellationToken).WaitAsync(TimeSpan.FromSeconds(15), portalCancellationToken).ConfigureAwait(false);

                if (bindResponse.ResponseCode != 0)
                {
                    throw new InvalidOperationException(
                        $"The desktop denied the global shortcut request (response {bindResponse.ResponseCode}).");
                }

                connection = newConnection;
                activationSubscription = newSubscription;
                globalShortcutsSession = newSession;
                return registrations.ToDictionary(
                    registration => registration.Id,
                    _ => HotkeyBackendRegistrationResult.Success,
                    StringComparer.Ordinal);
            }
            catch
            {
                newConnection.Dispose();
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UnregisterCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        try
        {
            lifecycleGate.Wait();
            try
            {
                UnregisterCoreAsync().GetAwaiter().GetResult();
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to dispose the GlobalShortcuts portal backend");
        }
        finally
        {
            lifecycleGate.Dispose();
            Activated = null;
        }
    }

    private async Task UnregisterCoreAsync()
    {
        activationSubscription?.Dispose();
        activationSubscription = null;
        Session? session = globalShortcutsSession;
        globalShortcutsSession = null;
        if (session is not null)
        {
            try
            {
                // Closing the portal session explicitly revokes the shortcuts
                // immediately when a row is cleared or SnapX exits. Merely
                // dropping the D-Bus connection can leave the portal holding
                // the old session until it notices the client has gone away.
                await session.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Failed to close the GlobalShortcuts portal session");
            }
        }
        connection?.Dispose();
        connection = null;
    }

    private static async Task RegisterHostApplicationAsync(
        DBusConnection connection,
        string applicationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await HostPortalRegistry.RegisterAsync(connection, applicationId)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            DebugHelper.WriteLine($"Registered portal host application ID: {applicationId}");
        }
        catch (DBusErrorReplyException ex) when (
            ex.ErrorName is "org.freedesktop.DBus.Error.UnknownMethod"
                or "org.freedesktop.DBus.Error.UnknownObject"
                or "org.freedesktop.DBus.Error.ServiceUnknown")
        {
            DebugHelper.WriteLine(
                $"The portal host registry is unavailable ({ex.ErrorName}). " +
                "SnapX will use the portal's automatic application identity.");
        }
    }

    private static string? ResolveHostApplicationId()
    {
        if (IsSandboxedApplication() ||
            Environment.GetEnvironmentVariable("SNAPX_REGISTER_PORTAL_HOST") == "0")
        {
            return null;
        }

        string? configuredId = Environment.GetEnvironmentVariable(ApplicationIdEnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(configuredId))
        {
            if (!IsValidApplicationId(configuredId))
            {
                throw new InvalidOperationException(
                    $"{ApplicationIdEnvironmentVariable} is not a valid desktop application ID.");
            }

            return configuredId;
        }

        return DesktopEntryExists(DefaultApplicationId) ? DefaultApplicationId : null;
    }

    private static bool IsSandboxedApplication() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLATPAK_ID")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNAP")) ||
        string.Equals(
            Environment.GetEnvironmentVariable("container"),
            "flatpak",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsValidApplicationId(string applicationId) =>
        applicationId.Length <= 255 &&
        applicationId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool DesktopEntryExists(string applicationId)
    {
        string fileName = $"{applicationId}.desktop";
        foreach (string dataDirectory in GetDataDirectories())
        {
            if (File.Exists(Path.Combine(dataDirectory, "applications", fileName)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetDataDirectories()
    {
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                dataHome = Path.Combine(userProfile, ".local", "share");
            }
        }

        if (!string.IsNullOrWhiteSpace(dataHome))
        {
            yield return dataHome;
        }

        string dataDirectories = Environment.GetEnvironmentVariable("XDG_DATA_DIRS")
            ?? "/usr/local/share:/usr/share";
        foreach (string directory in dataDirectories.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return directory;
        }
    }
}
