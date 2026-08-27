// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.Hotkey;

public sealed class UnavailableHotkeyBackend : IHotkeyBackend
{
    public event Action<string>? Activated
    {
        add { }
        remove { }
    }

    public bool IsAvailable => false;

    public string Name { get; }

    public string? AvailabilityError { get; }

    public UnavailableHotkeyBackend(string error, string name = "Unsupported")
    {
        AvailabilityError = error;
        Name = name;
    }

    public Task<IReadOnlyDictionary<string, HotkeyBackendRegistrationResult>> RegisterAsync(
        IReadOnlyCollection<HotkeyRegistration> registrations,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var error = AvailabilityError ?? "Global hotkeys are unavailable.";
        IReadOnlyDictionary<string, HotkeyBackendRegistrationResult> results = registrations.ToDictionary(
            registration => registration.Id,
            _ => HotkeyBackendRegistrationResult.Failure(error),
            StringComparer.Ordinal);
        return Task.FromResult(results);
    }

    public Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
