// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace SnapX.Core.Hotkey;

/// <summary>Carbon global shortcuts, delivered through the application's native event loop.</summary>
internal sealed class MacOSHotkeyBackend : IHotkeyBackend
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const uint Signature = 0x536E7058; // SnpX
    private static int nextId;
    private static readonly Lazy<IntPtr> MainQueue = new(() =>
        NativeLibrary.GetExport(NativeLibrary.Load(SystemLibrary), "_dispatch_main_q"));
    private readonly Dictionary<uint, (string Id, IntPtr Handle)> bindings = [];
    private readonly EventHandlerCallback callback;
    private IntPtr handler;
    private bool disposed;

    public event Action<string>? Activated;
    public string Name => "macOS Carbon";
    public bool IsAvailable => OperatingSystem.IsMacOS();
    public string? AvailabilityError => IsAvailable ? null : "macOS global shortcuts require macOS.";

    public MacOSHotkeyBackend() => callback = OnHotkey;

    public Task<IReadOnlyDictionary<string, HotkeyBackendRegistrationResult>> RegisterAsync(
        IReadOnlyCollection<HotkeyRegistration> registrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        return OnMainThread<IReadOnlyDictionary<string, HotkeyBackendRegistrationResult>>(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            UnregisterCore();
            var results = new Dictionary<string, HotkeyBackendRegistrationResult>(StringComparer.Ordinal);
            if (handler == IntPtr.Zero)
            {
                var eventType = new EventType { Class = 0x6B657962, Kind = 5 }; // keyb, pressed
                int status = InstallEventHandler(GetApplicationEventTarget(), callback, 1, ref eventType, IntPtr.Zero, out handler);
                if (status != 0)
                {
                    foreach (var item in registrations)
                        results[item.Id] = HotkeyBackendRegistrationResult.Failure($"macOS event handler failed (OSStatus {status}).");
                    return results;
                }
            }
            foreach (var item in registrations)
            {
                if (!item.HotkeyInfo.IsValidHotkey || !TryGetKeyCode(item.HotkeyInfo.KeyCode, out uint code))
                {
                    results[item.Id] = HotkeyBackendRegistrationResult.Failure("This key has no macOS equivalent. Choose a letter, number, function, or navigation key.");
                    continue;
                }
                uint modifiers = (item.HotkeyInfo.Win ? 256u : 0) | (item.HotkeyInfo.Shift ? 512u : 0) |
                    (item.HotkeyInfo.Alt ? 2048u : 0) | (item.HotkeyInfo.Control ? 4096u : 0);
                uint id = unchecked((uint)Interlocked.Increment(ref nextId));
                int status = RegisterEventHotKey(code, modifiers, new NativeHotkeyId { Signature = Signature, Id = id },
                    GetApplicationEventTarget(), 1, out IntPtr reference);
                if (status == 0)
                {
                    bindings.Add(id, (item.Id, reference));
                    results[item.Id] = HotkeyBackendRegistrationResult.Success;
                }
                else results[item.Id] = HotkeyBackendRegistrationResult.Failure($"macOS could not register this shortcut (OSStatus {status}); it may already be in use.");
            }
            return results;
        }, cancellationToken);
    }

    public Task UnregisterAsync(CancellationToken cancellationToken = default) => OnMainThread(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnregisterCore();
        return true;
    }, cancellationToken);

    public void Dispose()
    {
        // The UI waits for Core shutdown on a worker. Never synchronously wait
        // for the main queue here; queued cleanup roots the callback until removed.
        _ = OnMainThread(() =>
        {
            if (disposed) return true;
            disposed = true;
            UnregisterCore();
            if (handler != IntPtr.Zero) { RemoveEventHandler(handler); handler = IntPtr.Zero; }
            Activated = null;
            return true;
        }, CancellationToken.None);
    }

    private void UnregisterCore()
    {
        foreach (var binding in bindings.Values) UnregisterEventHotKey(binding.Handle);
        bindings.Clear();
    }

    private int OnHotkey(IntPtr call, IntPtr nativeEvent, IntPtr userData)
    {
        try
        {
            int status = GetEventParameter(nativeEvent, 0x2D2D2D2D, 0x686B6964, IntPtr.Zero, 8, IntPtr.Zero, out NativeHotkeyId id);
            if (status != 0 || id.Signature != Signature || !bindings.TryGetValue(id.Id, out var binding)) return -9874;
            Activated?.Invoke(binding.Id);
            return 0;
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "macOS global shortcut callback failed");
            return -9874;
        }
    }

    private static Task<T> OnMainThread<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pthread_main_np() != 0)
        {
            try { return Task.FromResult(action()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc((Action)(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }));
        dispatch_async_f(MainQueue.Value, GCHandle.ToIntPtr(handle), DispatchCallback);
        return completion.Task;
    }

    private static readonly DispatchAction DispatchCallback = context =>
    {
        var handle = GCHandle.FromIntPtr(context);
        var action = (Action)handle.Target!;
        handle.Free();
        action();
    };

    // Carbon virtual keycodes use physical ANSI positions, as defined in Events.h.
    internal static bool TryGetKeyCode(Keys key, out uint code)
    {
        int value = key switch
        {
            Keys.A => 0, Keys.S => 1, Keys.D => 2, Keys.F => 3, Keys.H => 4, Keys.G => 5,
            Keys.Z => 6, Keys.X => 7, Keys.C => 8, Keys.V => 9, Keys.B => 11, Keys.Q => 12,
            Keys.W => 13, Keys.E => 14, Keys.R => 15, Keys.Y => 16, Keys.T => 17,
            Keys.D1 => 18, Keys.D2 => 19, Keys.D3 => 20, Keys.D4 => 21, Keys.D6 => 22, Keys.D5 => 23,
            Keys.D9 => 25, Keys.D7 => 26, Keys.D8 => 28, Keys.D0 => 29, Keys.O => 31,
            Keys.U => 32, Keys.I => 34, Keys.P => 35,
            Keys.Return => 36, Keys.L => 37, Keys.J => 38, Keys.K => 40,
            Keys.N => 45, Keys.M => 46, Keys.Tab => 48, Keys.Space => 49,
            Keys.Back => 51, Keys.Escape => 53,
            Keys.NumPadDecimal => 65, Keys.NumPadMultiply => 67, Keys.Add => 69, Keys.Clear => 71, Keys.NumPadEnter => 76, Keys.NumPadEquals => 81,
            Keys.NumPadDivide => 75, Keys.NumPadSubtract => 78, Keys.NumPad0 => 82, Keys.NumPad1 => 83,
            Keys.NumPad2 => 84, Keys.NumPad3 => 85, Keys.NumPad4 => 86, Keys.NumPad5 => 87,
            Keys.NumPad6 => 88, Keys.NumPad7 => 89, Keys.NumPad8 => 91, Keys.NumPad9 => 92,
            Keys.F1 => 122, Keys.F2 => 120, Keys.F3 => 99, Keys.F4 => 118, Keys.F5 => 96,
            Keys.F6 => 97, Keys.F7 => 98, Keys.F8 => 100, Keys.F9 => 101, Keys.F10 => 109,
            Keys.F11 => 103, Keys.F12 => 111, Keys.F13 => 105, Keys.F14 => 107, Keys.F15 => 113,
            Keys.F16 => 106, Keys.F17 => 64, Keys.F18 => 79, Keys.F19 => 80, Keys.F20 => 90,
            Keys.Home => 115, Keys.PageUp => 116, Keys.Delete => 117, Keys.End => 119,
            Keys.PageDown => 121, Keys.Left => 123, Keys.Right => 124, Keys.Down => 125, Keys.Up => 126,
            _ => -1
        };
        code = (uint)Math.Max(0, value);
        return value >= 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct EventType { public uint Class; public uint Kind; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeHotkeyId { public uint Signature; public uint Id; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EventHandlerCallback(IntPtr call, IntPtr nativeEvent, IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DispatchAction(IntPtr context);
    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(IntPtr target, EventHandlerCallback callback, uint count, ref EventType events, IntPtr userData, out IntPtr handler);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handler);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint code, uint modifiers, NativeHotkeyId id, IntPtr target, uint options, out IntPtr reference);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr reference);
    [DllImport(Carbon)] private static extern int GetEventParameter(IntPtr nativeEvent, uint name, uint type, IntPtr actualType, uint size, IntPtr actualSize, out NativeHotkeyId id);
    [DllImport(SystemLibrary)] private static extern int pthread_main_np();
    [DllImport(SystemLibrary)] private static extern void dispatch_async_f(IntPtr queue, IntPtr context, DispatchAction action);
}
