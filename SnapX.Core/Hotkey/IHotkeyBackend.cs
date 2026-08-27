// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.Hotkey;

public sealed record HotkeyRegistration(string Id, HotkeyInfo HotkeyInfo)
{
    /// <summary>
    /// Freedesktop accelerator spelling used by the Wayland GlobalShortcuts portal.
    /// </summary>
    public string Accelerator => HotkeyAccelerator.ToPortalAccelerator(HotkeyInfo);
}

public readonly record struct HotkeyBackendRegistrationResult(bool IsRegistered, string? Error = null)
{
    public static HotkeyBackendRegistrationResult Success => new(true);

    public static HotkeyBackendRegistrationResult Failure(string error) => new(false, error);
}

public interface IHotkeyBackend : IDisposable
{
    event Action<string>? Activated;

    string Name { get; }

    bool IsAvailable { get; }

    string? AvailabilityError { get; }

    Task<IReadOnlyDictionary<string, HotkeyBackendRegistrationResult>> RegisterAsync(
        IReadOnlyCollection<HotkeyRegistration> registrations,
        CancellationToken cancellationToken = default);

    Task UnregisterAsync(CancellationToken cancellationToken = default);
}
