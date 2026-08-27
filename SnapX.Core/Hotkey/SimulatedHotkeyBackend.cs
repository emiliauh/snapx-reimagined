// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.Hotkey;

/// <summary>
/// Deterministic in-memory backend for headless hosts and tests.
/// </summary>
public sealed class SimulatedHotkeyBackend : IHotkeyBackend
{
    private readonly object _sync = new();
    private Dictionary<string, HotkeyRegistration> _registrations = new(StringComparer.Ordinal);
    private bool _disposed;

    public event Action<string>? Activated;

    public string Name => "Simulated";

    public bool IsAvailable => !_disposed;

    public string? AvailabilityError => _disposed ? "The simulated hotkey backend is disposed." : null;

    public IReadOnlyCollection<HotkeyRegistration> Registrations
    {
        get
        {
            lock (_sync)
            {
                return _registrations.Values.ToArray();
            }
        }
    }

    public Task<IReadOnlyDictionary<string, HotkeyBackendRegistrationResult>> RegisterAsync(
        IReadOnlyCollection<HotkeyRegistration> registrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _registrations = registrations.ToDictionary(x => x.Id, StringComparer.Ordinal);
        }

        IReadOnlyDictionary<string, HotkeyBackendRegistrationResult> results = registrations.ToDictionary(
            registration => registration.Id,
            _ => HotkeyBackendRegistrationResult.Success,
            StringComparer.Ordinal);
        return Task.FromResult(results);
    }

    public Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_disposed) _registrations.Clear();
        }

        return Task.CompletedTask;
    }

    public bool Trigger(string registrationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);
        lock (_sync)
        {
            if (_disposed || !_registrations.ContainsKey(registrationId)) return false;
        }

        Activated?.Invoke(registrationId);
        return true;
    }

    public bool Trigger(Keys hotkey, bool win = false)
    {
        string? id;
        lock (_sync)
        {
            if (_disposed) return false;
            id = _registrations.Values.FirstOrDefault(registration =>
                registration.HotkeyInfo.Hotkey == hotkey && registration.HotkeyInfo.Win == win)?.Id;
        }

        return id is not null && Trigger(id);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _registrations.Clear();
            Activated = null;
        }
    }
}
