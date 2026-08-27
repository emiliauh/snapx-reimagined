// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SnapX.Core.Hotkey;

/// <summary>
/// Win32 thread-level RegisterHotKey backend. A private message queue keeps the
/// implementation independent of any UI framework or window handle.
/// </summary>
internal sealed class WindowsHotkeyBackend : IHotkeyBackend
{
    private const uint WmHotkey = 0x0312;
    private const uint PmRemove = 0x0001;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly BlockingCollection<Action> _commands = new();
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, string> _registrations = [];
    private readonly Thread? _thread;
    private bool _stopping;
    private bool _disposed;

    public event Action<string>? Activated;

    public string Name => "Windows RegisterHotKey";

    public bool IsAvailable { get; private set; }

    public string? AvailabilityError { get; private set; }

    public WindowsHotkeyBackend()
    {
        if (!OperatingSystem.IsWindows())
        {
            AvailabilityError = "RegisterHotKey is only available on Windows.";
            return;
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "SnapX Windows hotkeys"
        };
        _thread.Start();
        if (!_initialized.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            AvailabilityError = "Timed out while creating the Win32 hotkey message queue.";
        }
    }

    public Task<IReadOnlyDictionary<string, HotkeyBackendRegistrationResult>> RegisterAsync(
        IReadOnlyCollection<HotkeyRegistration> registrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Invoke(() => RegisterCore(registrations), cancellationToken));
    }

    public Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(() =>
        {
            UnregisterCore();
            return true;
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_thread?.IsAlive == true)
        {
            try
            {
                Invoke(() =>
                {
                    UnregisterCore();
                    _stopping = true;
                    return true;
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Failed to stop the Windows hotkey backend cleanly");
                _stopping = true;
            }

            _commands.CompleteAdding();
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                DebugHelper.WriteException("Timed out while stopping the Windows hotkey backend.");
            }
        }

        _disposed = true;
        _commands.Dispose();
        Activated = null;
    }

    private void MessageLoop()
    {
        try
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            AvailabilityError = $"Unable to initialize RegisterHotKey: {ex.Message}";
        }
        finally
        {
            _initialized.TrySetResult(IsAvailable);
        }

        try
        {
            while (IsAvailable && !_stopping)
            {
                if (_commands.TryTake(out var command, 10)) command();

                while (PeekMessage(out var message, IntPtr.Zero, WmHotkey, WmHotkey, PmRemove))
                {
                    var id = unchecked((int)message.wParam);
                    if (_registrations.TryGetValue(id, out var registrationId))
                    {
                        ThreadPool.QueueUserWorkItem(_ => Activated?.Invoke(registrationId));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AvailabilityError = $"The RegisterHotKey message loop failed: {ex.Message}";
            IsAvailable = false;
            DebugHelper.WriteException(ex, "Windows hotkey message loop failed");
        }
        finally
        {
            try { UnregisterCore(); }
            catch (Exception ex) { DebugHelper.WriteException(ex, "Windows hotkey cleanup failed"); }
            IsAvailable = false;
        }
    }

    private IReadOnlyDictionary<string, HotkeyBackendRegistrationResult> RegisterCore(
        IReadOnlyCollection<HotkeyRegistration> registrations)
    {
        UnregisterCore();
        var results = new Dictionary<string, HotkeyBackendRegistrationResult>(StringComparer.Ordinal);

        foreach (var registration in registrations)
        {
            var id = registration.HotkeyInfo.ID;
            var virtualKey = ToVirtualKey(registration.HotkeyInfo.KeyCode);
            if (id == 0 || virtualKey == 0)
            {
                results[registration.Id] = HotkeyBackendRegistrationResult.Failure(
                    $"{registration.HotkeyInfo.KeyCode} is not a valid Windows hotkey key.");
                continue;
            }

            var modifiers = ToWindowsModifiers(registration.HotkeyInfo) | ModNoRepeat;
            if (!RegisterHotKey(IntPtr.Zero, id, modifiers, virtualKey))
            {
                var error = Marshal.GetLastWin32Error();
                results[registration.Id] = HotkeyBackendRegistrationResult.Failure(
                    new Win32Exception(error, "RegisterHotKey failed; the combination may already be in use.").Message);
                continue;
            }

            _registrations[id] = registration.Id;
            results[registration.Id] = HotkeyBackendRegistrationResult.Success;
        }

        return results;
    }

    private void UnregisterCore()
    {
        Win32Exception? firstError = null;
        foreach (var id in _registrations.Keys.ToArray())
        {
            if (!UnregisterHotKey(IntPtr.Zero, id))
            {
                var error = Marshal.GetLastWin32Error();
                var exception = new Win32Exception(error, $"Failed to unregister hotkey ID {id}.");
                firstError ??= exception;
                DebugHelper.WriteException(exception);
            }
        }
        _registrations.Clear();

        if (firstError is not null) throw firstError;
    }

    private T Invoke<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(AvailabilityError ?? "Windows hotkeys are unavailable.");
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, cancellationToken);
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).GetAwaiter().GetResult();
    }

    private static uint ToWindowsModifiers(HotkeyInfo hotkey)
    {
        uint modifiers = 0;
        if (hotkey.Alt) modifiers |= ModAlt;
        if (hotkey.Control) modifiers |= ModControl;
        if (hotkey.Shift) modifiers |= ModShift;
        if (hotkey.Win) modifiers |= ModWin;
        return modifiers;
    }

    private static uint ToVirtualKey(Keys key) => key switch
    {
        Keys.PrintScreen => (uint)Keys.Snapshot,
        Keys.NumPadEnter => (uint)Keys.Return,
        _ when (uint)key <= ushort.MaxValue => (uint)key,
        _ => 0
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int pointX;
        public int pointY;
        public uint privateData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint removeMessage);
}
