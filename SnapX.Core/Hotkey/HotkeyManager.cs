// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.Job;

namespace SnapX.Core.Hotkey;

public sealed class HotkeyManager : IDisposable
{
    private readonly IHotkeyBackend _backend;
    private readonly object _sync = new();
    private readonly object _registrationSync = new();
    private readonly Dictionary<string, HotkeySettings> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastActivation = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private ushort _nextId = 1;
    private bool _hotkeysDisabled;
    private bool _disposed;

    public List<HotkeySettings> Hotkeys { get; private set; } = [];
    public bool IgnoreHotkeys { get; set; }
    public bool RegistrationAllowed { get; }
    public TimeSpan HotkeyRepeatLimit { get; set; } = TimeSpan.FromSeconds(1);
    public IHotkeyBackend Backend => _backend;

    public delegate void HotkeyTriggerEventHandler(HotkeySettings hotkeySetting);
    public delegate void HotkeysToggledEventHandler(bool hotkeysDisabled);
    public HotkeyTriggerEventHandler? HotkeyTrigger;
    public HotkeysToggledEventHandler? HotkeysToggledTrigger;

    public HotkeyManager(
        IHotkeyBackend? backend = null,
        bool hotkeysDisabled = false,
        bool registrationAllowed = true,
        TimeProvider? timeProvider = null)
    {
        _backend = backend ?? HotkeyBackendFactory.CreateDefault();
        _hotkeysDisabled = hotkeysDisabled;
        RegistrationAllowed = registrationAllowed;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _backend.Activated += BackendActivated;
    }

    public void UpdateHotkeys(List<HotkeySettings>? hotkeys, bool showFailedHotkeys)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            foreach (HotkeySettings previous in Hotkeys)
                MarkNotConfigured(previous, "Hotkey configuration was replaced.");
            Hotkeys = hotkeys?.Where(setting => setting != null).ToList() ?? [];
            _lastActivation.Clear();
        }
        ApplyRegistrations(Hotkeys);
        if (showFailedHotkeys) LogFailures();
    }

    public void RegisterHotkey(HotkeySettings setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ThrowIfDisposed();
        lock (_sync)
        {
            if (!Hotkeys.Contains(setting)) Hotkeys.Add(setting);
        }
        ApplyRegistrations(Hotkeys);
    }

    public void RegisterAllHotkeys()
    {
        ThrowIfDisposed();
        ApplyRegistrations(Hotkeys);
    }

    public void RegisterFailedHotkeys() => RegisterAllHotkeys();

    public void UnregisterHotkey(HotkeySettings setting, bool removeFromList = true)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ThrowIfDisposed();
        List<HotkeySettings> desired;
        lock (_sync)
        {
            if (removeFromList) Hotkeys.Remove(setting);
            desired = Hotkeys.Where(candidate => !ReferenceEquals(candidate, setting)).ToList();
            MarkNotConfigured(setting, "Hotkey is not registered.");
        }
        ApplyRegistrations(desired);
    }

    public void UnregisterAllHotkeys(bool removeFromList = true, bool temporary = false)
    {
        ThrowIfDisposed();
        List<HotkeySettings> retained;
        lock (_sync)
        {
            retained = temporary
                ? Hotkeys.Where(x => x?.TaskSettings?.Job == HotkeyType.DisableHotkeys).ToList()
                : [];
            foreach (var setting in Hotkeys.Except(retained))
                MarkNotConfigured(setting, "Hotkey is not registered.");
            if (removeFromList)
            {
                Hotkeys.Clear();
                Hotkeys.AddRange(retained);
            }
        }
        ApplyRegistrations(retained);
    }

    public void ToggleHotkeys(bool hotkeysDisabled)
    {
        ThrowIfDisposed();
        lock (_sync) _hotkeysDisabled = hotkeysDisabled;
        ApplyRegistrations(Hotkeys);
        HotkeysToggledTrigger?.Invoke(hotkeysDisabled);
    }

    public void ResetHotkeys()
    {
        ThrowIfDisposed();
        lock (_sync) Hotkeys = GetDefaultHotkeyList();
        ApplyRegistrations(Hotkeys);
    }

    public bool SimulateHotkeyPress(Keys hotkey, bool win = false)
    {
        ThrowIfDisposed();
        string? id;
        lock (_sync)
        {
            id = _active.FirstOrDefault(x =>
                x.Value.HotkeyInfo.Hotkey == hotkey && x.Value.HotkeyInfo.Win == win).Key;
        }
        if (id is null) return false;
        BackendActivated(id);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.Activated -= BackendActivated;
        string? unregistrationError = null;
        lock (_registrationSync)
        {
            try
            {
                _backend.UnregisterAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                unregistrationError = ex.Message;
                DebugHelper.WriteException(ex, $"Failed to unregister hotkeys from {_backend.Name}");
            }
        }
        lock (_sync)
        {
            foreach (var setting in Hotkeys)
            {
                if (unregistrationError == null)
                {
                    MarkNotConfigured(setting, "Hotkey manager was disposed.");
                }
                else if (setting.HotkeyInfo != null)
                {
                    setting.HotkeyInfo.ID = 0;
                    setting.HotkeyInfo.Status = HotkeyStatus.Failed;
                    setting.HotkeyInfo.StatusMessage = $"Hotkey cleanup failed: {unregistrationError}";
                }
            }
            _active.Clear();
            _lastActivation.Clear();
        }
        _backend.Dispose();
        HotkeyTrigger = null;
        HotkeysToggledTrigger = null;
    }

    private void ApplyRegistrations(IEnumerable<HotkeySettings> desiredSettings)
    {
        lock (_registrationSync)
        {
            ApplyRegistrationsCore(desiredSettings);
        }
    }

    private void ApplyRegistrationsCore(IEnumerable<HotkeySettings> desiredSettings)
    {
        List<HotkeyRegistration> registrations;
        lock (_sync)
        {
            registrations = CreateRegistrations(desiredSettings);
            _active.Clear();
        }

        IReadOnlyDictionary<string, HotkeyBackendRegistrationResult> results;
        try
        {
            _backend.UnregisterAsync().GetAwaiter().GetResult();
            if (registrations.Count == 0) return;
            if (_backend.IsAvailable)
            {
                results = _backend.RegisterAsync(registrations).GetAwaiter().GetResult();
            }
            else
            {
                string error = _backend.AvailabilityError ?? $"{_backend.Name} is unavailable.";
                results = FailureResults(registrations, error);
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, $"Global hotkey registration failed in {_backend.Name}");
            results = FailureResults(registrations, ex.Message);
        }

        lock (_sync)
        {
            foreach (var registration in registrations)
            {
                results.TryGetValue(registration.Id, out var result);
                HotkeySettings? setting = Hotkeys.FirstOrDefault(x =>
                    ReferenceEquals(x.HotkeyInfo, registration.HotkeyInfo));
                if (result.IsRegistered && setting is not null)
                {
                    registration.HotkeyInfo.Status = HotkeyStatus.Registered;
                    registration.HotkeyInfo.StatusMessage = null;
                    _active[registration.Id] = setting;
                    DebugHelper.WriteLine($"Hotkey registered by {_backend.Name}: {setting}");
                }
                else
                {
                    registration.HotkeyInfo.ID = 0;
                    registration.HotkeyInfo.Status = HotkeyStatus.Failed;
                    registration.HotkeyInfo.StatusMessage = result.Error ??
                        $"{_backend.Name} returned no registration result.";
                    DebugHelper.WriteException(
                        $"Hotkey registration failed in {_backend.Name}: {registration.HotkeyInfo} - {registration.HotkeyInfo.StatusMessage}");
                }
            }
        }
    }

    private List<HotkeyRegistration> CreateRegistrations(IEnumerable<HotkeySettings> settings)
    {
        var registrations = new List<HotkeyRegistration>();
        var gestures = new HashSet<(Keys Key, Modifiers Modifiers)>();
        var ids = new HashSet<ushort>();
        var registrationIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var setting in settings)
        {
            if (setting?.HotkeyInfo is null || setting.TaskSettings is null)
            {
                if (setting?.HotkeyInfo is not null)
                    MarkNotConfigured(setting, "Hotkey task settings are missing.");
                continue;
            }

            HotkeyInfo info = setting.HotkeyInfo;
            if (!info.IsValidHotkey)
            {
                MarkNotConfigured(setting, "The key combination is empty or invalid.");
                continue;
            }
            if (!RegistrationAllowed)
            {
                MarkNotConfigured(setting, "Global hotkeys were disabled for this process.");
                continue;
            }
            if (_hotkeysDisabled && setting.TaskSettings.Job != HotkeyType.DisableHotkeys)
            {
                MarkNotConfigured(setting, "Global hotkeys are currently disabled.");
                continue;
            }
            if (!gestures.Add((info.KeyCode, info.ModifiersEnum)))
            {
                info.ID = 0;
                info.Status = HotkeyStatus.Failed;
                info.StatusMessage = $"Duplicate hotkey combination: {info}.";
                continue;
            }

            if (info.ID == 0 || ids.Contains(info.ID)) info.ID = AllocateId(ids);
            ids.Add(info.ID);
            if (!IsSafeRegistrationId(info.RegistrationId) || registrationIds.Contains(info.RegistrationId))
            {
                do
                {
                    info.RegistrationId = Guid.NewGuid().ToString("N");
                }
                while (!registrationIds.Add(info.RegistrationId));
            }
            else
            {
                registrationIds.Add(info.RegistrationId);
            }

            registrations.Add(new HotkeyRegistration($"snapx_{info.RegistrationId}", info));
        }
        return registrations;
    }

    private ushort AllocateId(ISet<ushort> used)
    {
        for (int attempts = 0; attempts < ushort.MaxValue - 1; attempts++)
        {
            if (_nextId == 0) _nextId = 1;
            ushort candidate = _nextId++;
            if (!used.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("No global hotkey registration IDs remain available.");
    }

    private static bool IsSafeRegistrationId(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiLetterOrDigit);

    private void BackendActivated(string registrationId)
    {
        HotkeySettings? setting;
        lock (_sync)
        {
            if (_disposed || !_active.TryGetValue(registrationId, out setting)) return;
            if (IgnoreHotkeys && setting.TaskSettings.Job != HotkeyType.DisableHotkeys) return;
            if (_hotkeysDisabled && setting.TaskSettings.Job != HotkeyType.DisableHotkeys) return;
            long now = _timeProvider.GetTimestamp();
            if (HotkeyRepeatLimit > TimeSpan.Zero &&
                _lastActivation.TryGetValue(registrationId, out long previous) &&
                _timeProvider.GetElapsedTime(previous, now) < HotkeyRepeatLimit) return;
            _lastActivation[registrationId] = now;
        }

        try
        {
            HotkeyTrigger?.Invoke(setting);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, $"Hotkey dispatch failed for {setting}");
        }
    }

    private void LogFailures()
    {
        lock (_sync)
        {
            foreach (var setting in Hotkeys.Where(x => x?.HotkeyInfo?.Status == HotkeyStatus.Failed))
                DebugHelper.WriteLine($"Hotkey unavailable: {setting.HotkeyInfo} - {setting.HotkeyInfo.StatusMessage}");
        }
    }

    private static IReadOnlyDictionary<string, HotkeyBackendRegistrationResult> FailureResults(
        IEnumerable<HotkeyRegistration> registrations, string error) =>
        registrations.ToDictionary(x => x.Id, _ => HotkeyBackendRegistrationResult.Failure(error), StringComparer.Ordinal);

    private static void MarkNotConfigured(HotkeySettings? setting, string message)
    {
        if (setting?.HotkeyInfo is null) return;
        setting.HotkeyInfo.ID = 0;
        setting.HotkeyInfo.Status = HotkeyStatus.NotConfigured;
        setting.HotkeyInfo.StatusMessage = message;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public static List<HotkeySettings> GetDefaultHotkeyList() =>
    [
        new(HotkeyType.RectangleRegion, Keys.Control | Keys.PrintScreen),
        new(HotkeyType.PrintScreen, Keys.PrintScreen),
        new(HotkeyType.ActiveWindow, Keys.Alt | Keys.PrintScreen),
        new(HotkeyType.ScreenRecorder, Keys.Shift | Keys.PrintScreen),
        new(HotkeyType.ScreenRecorderGIF, Keys.Control | Keys.Shift | Keys.PrintScreen),
        new(HotkeyType.ScrollingCapture, Keys.Control | Keys.Shift | Keys.S)
    ];
}
