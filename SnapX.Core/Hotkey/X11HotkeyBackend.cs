// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SnapX.Core.Hotkey;

/// <summary>
/// X11 global hotkeys implemented with passive key grabs. Every Xlib call for
/// this connection is executed on one dedicated thread.
/// </summary>
internal sealed class X11HotkeyBackend : IHotkeyBackend
{
    private const string LibX11 = "libX11.so.6";
    private const int KeyPress = 2;
    private const int GrabModeAsync = 1;
    private const uint ShiftMask = 1u << 0;
    private const uint LockMask = 1u << 1;
    private const uint ControlMask = 1u << 2;
    private const uint Mod1Mask = 1u << 3;
    private const uint Mod4Mask = 1u << 6;
    private const ulong XkNumLock = 0xFF7F;
    private const int EventBufferSize = 192;

    private static readonly object ErrorHandlerSync = new();

    private readonly BlockingCollection<Action> _commands = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<(uint KeyCode, uint Modifiers), string> _bindings = [];
    private readonly List<NativeGrab> _nativeGrabs = [];
    private readonly XErrorHandler _errorHandler;
    private IntPtr _display;
    private IntPtr[] _rootWindows = [];
    private uint _numLockMask;
    private int _lastErrorCode;
    private bool _stopping;
    private bool _disposed;

    public event Action<string>? Activated;

    public string Name => "X11";

    public bool IsAvailable { get; private set; }

    public string? AvailabilityError { get; private set; }

    public X11HotkeyBackend()
    {
        _errorHandler = OnXError;
        _thread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "SnapX X11 hotkeys"
        };
        _thread.Start();

        if (!_initialized.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            AvailabilityError = "Timed out while opening the X11 display.";
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

        if (_thread.IsAlive)
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
                DebugHelper.WriteException(ex, "Failed to stop the X11 hotkey backend cleanly");
                _stopping = true;
            }

            _commands.CompleteAdding();
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                DebugHelper.WriteException("Timed out while stopping the X11 hotkey backend.");
            }
        }

        _disposed = true;
        _commands.Dispose();
        Activated = null;
    }

    private void EventLoop()
    {
        IntPtr eventBuffer = IntPtr.Zero;
        try
        {
            _display = XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                AvailabilityError = "XOpenDisplay failed for the current DISPLAY.";
            }
            else
            {
                var screenCount = Math.Max(1, XScreenCount(_display));
                _rootWindows = Enumerable.Range(0, screenCount)
                    .Select(screen => XRootWindow(_display, screen))
                    .Where(root => root != IntPtr.Zero)
                    .Distinct()
                    .ToArray();
                if (_rootWindows.Length == 0)
                {
                    AvailabilityError = "X11 did not expose a root window.";
                }
                else
                {
                    _numLockMask = FindModifierMask(XKeysymToKeycode(_display, XkNumLock));
                    eventBuffer = Marshal.AllocHGlobal(EventBufferSize);
                    IsAvailable = true;
                }
            }
        }
        catch (Exception ex)
        {
            AvailabilityError = $"X11 initialization failed: {ex.Message}";
        }
        finally
        {
            _initialized.TrySetResult(IsAvailable);
        }

        if (!IsAvailable)
        {
            if (_display != IntPtr.Zero)
            {
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }

            return;
        }

        try
        {
            while (IsAvailable && !_stopping)
            {
                if (_commands.TryTake(out var command, 10)) command();

                while (!_stopping && XPending(_display) > 0)
                {
                    XNextEvent(_display, eventBuffer);
                    if (Marshal.ReadInt32(eventBuffer) != KeyPress) continue;

                    var keyEvent = Marshal.PtrToStructure<XKeyEvent>(eventBuffer);
                    var normalizedModifiers = keyEvent.state & ~(LockMask | _numLockMask);
                    if (_bindings.TryGetValue((keyEvent.keycode, normalizedModifiers), out var id))
                    {
                        ThreadPool.QueueUserWorkItem(_ => Activated?.Invoke(id));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AvailabilityError = $"X11 event loop failed: {ex.Message}";
            IsAvailable = false;
            DebugHelper.WriteException(ex, "X11 hotkey event loop failed");
        }
        finally
        {
            if (_display != IntPtr.Zero)
            {
                try { UnregisterCore(); }
                catch (Exception ex) { DebugHelper.WriteException(ex, "X11 hotkey cleanup failed"); }
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }

            if (eventBuffer != IntPtr.Zero) Marshal.FreeHGlobal(eventBuffer);
            IsAvailable = false;
        }
    }

    private IReadOnlyDictionary<string, HotkeyBackendRegistrationResult> RegisterCore(
        IReadOnlyCollection<HotkeyRegistration> registrations)
    {
        if (!IsAvailable)
        {
            return registrations.ToDictionary(
                registration => registration.Id,
                _ => HotkeyBackendRegistrationResult.Failure(AvailabilityError ?? "X11 is unavailable."),
                StringComparer.Ordinal);
        }

        UnregisterCore();
        var results = new Dictionary<string, HotkeyBackendRegistrationResult>(StringComparer.Ordinal);
        var seen = new HashSet<(uint KeyCode, uint Modifiers)>();

        foreach (var registration in registrations)
        {
            if (!TryGetXKeySym(registration.HotkeyInfo.KeyCode, out var keySym))
            {
                results[registration.Id] = HotkeyBackendRegistrationResult.Failure(
                    $"{registration.HotkeyInfo.KeyCode} is not supported by the X11 backend.");
                continue;
            }

            var keyCode = XKeysymToKeycode(_display, keySym);
            if (keyCode == 0)
            {
                results[registration.Id] = HotkeyBackendRegistrationResult.Failure(
                    $"{registration.HotkeyInfo.KeyCode} is not present in the current X11 keymap.");
                continue;
            }

            var modifiers = ToXModifiers(registration.HotkeyInfo);
            if (!seen.Add((keyCode, modifiers)))
            {
                results[registration.Id] = HotkeyBackendRegistrationResult.Failure(
                    "Another hotkey in this registration batch uses the same key combination.");
                continue;
            }

            var grabs = CreateModifierVariants(modifiers)
                .SelectMany(variant => _rootWindows.Select(root => new NativeGrab(root, keyCode, variant)))
                .ToArray();
            if (!TryGrab(grabs, out var error))
            {
                results[registration.Id] = HotkeyBackendRegistrationResult.Failure(error);
                continue;
            }

            _nativeGrabs.AddRange(grabs);
            _bindings[(keyCode, modifiers)] = registration.Id;
            results[registration.Id] = HotkeyBackendRegistrationResult.Success;
        }

        return results;
    }

    private void UnregisterCore()
    {
        if (_display == IntPtr.Zero)
        {
            _bindings.Clear();
            _nativeGrabs.Clear();
            return;
        }

        lock (ErrorHandlerSync)
        {
            _lastErrorCode = 0;
            var callback = Marshal.GetFunctionPointerForDelegate(_errorHandler);
            var previousHandler = XSetErrorHandler(callback);
            try
            {
                foreach (var grab in _nativeGrabs)
                {
                    XUngrabKey(_display, (int)grab.KeyCode, grab.Modifiers, grab.RootWindow);
                }

                if (_nativeGrabs.Count > 0) XSync(_display, false);
            }
            finally
            {
                XSetErrorHandler(previousHandler);
            }
        }

        _bindings.Clear();
        _nativeGrabs.Clear();

        if (_lastErrorCode != 0)
        {
            throw new InvalidOperationException(
                $"XUngrabKey failed with X11 error {_lastErrorCode}.");
        }
    }

    private bool TryGrab(IReadOnlyCollection<NativeGrab> grabs, out string error)
    {
        lock (ErrorHandlerSync)
        {
            _lastErrorCode = 0;
            var callback = Marshal.GetFunctionPointerForDelegate(_errorHandler);
            var previousHandler = XSetErrorHandler(callback);
            try
            {
                foreach (var grab in grabs)
                {
                    XGrabKey(_display, (int)grab.KeyCode, grab.Modifiers, grab.RootWindow, false, GrabModeAsync, GrabModeAsync);
                }

                XSync(_display, false);
            }
            finally
            {
                XSetErrorHandler(previousHandler);
            }

            if (_lastErrorCode == 0)
            {
                error = string.Empty;
                return true;
            }

            foreach (var grab in grabs)
            {
                XUngrabKey(_display, (int)grab.KeyCode, grab.Modifiers, grab.RootWindow);
            }
            XSync(_display, false);

            error = _lastErrorCode == 10
                ? "The hotkey is already registered by another application."
                : $"XGrabKey failed with X11 error {_lastErrorCode}.";
            return false;
        }
    }

    private T Invoke<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(AvailabilityError ?? "X11 hotkeys are unavailable.");
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, cancellationToken);
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).GetAwaiter().GetResult();
    }

    private uint FindModifierMask(uint keyCode)
    {
        if (keyCode == 0) return 0;
        var mapPointer = XGetModifierMapping(_display);
        if (mapPointer == IntPtr.Zero) return 0;

        try
        {
            var map = Marshal.PtrToStructure<XModifierKeymap>(mapPointer);
            for (var modifier = 0; modifier < 8; modifier++)
            {
                for (var index = 0; index < map.maxKeyPerMod; index++)
                {
                    var offset = modifier * map.maxKeyPerMod + index;
                    if (Marshal.ReadByte(map.modifierMap, offset) == keyCode) return 1u << modifier;
                }
            }
        }
        finally
        {
            XFreeModifiermap(mapPointer);
        }

        return 0;
    }

    private static uint ToXModifiers(HotkeyInfo hotkey)
    {
        uint modifiers = 0;
        if (hotkey.Shift) modifiers |= ShiftMask;
        if (hotkey.Control) modifiers |= ControlMask;
        if (hotkey.Alt) modifiers |= Mod1Mask;
        if (hotkey.Win) modifiers |= Mod4Mask;
        return modifiers;
    }

    private IEnumerable<uint> CreateModifierVariants(uint modifiers)
    {
        var variants = new HashSet<uint> { modifiers, modifiers | LockMask };
        if (_numLockMask != 0)
        {
            variants.Add(modifiers | _numLockMask);
            variants.Add(modifiers | LockMask | _numLockMask);
        }
        return variants;
    }

    private int OnXError(IntPtr display, ref XErrorEvent error)
    {
        _lastErrorCode = error.errorCode;
        return 0;
    }

    private static bool TryGetXKeySym(Keys key, out ulong keySym)
    {
        if (key is >= Keys.A and <= Keys.Z)
        {
            keySym = (ulong)((int)key + ('a' - 'A'));
            return true;
        }
        if (key is >= Keys.D0 and <= Keys.D9)
        {
            keySym = (ulong)key;
            return true;
        }
        if (key is >= Keys.F1 and <= Keys.F24)
        {
            keySym = 0xFFBEu + (ulong)(key - Keys.F1);
            return true;
        }
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            keySym = 0xFFB0u + (ulong)(key - Keys.NumPad0);
            return true;
        }

        keySym = key switch
        {
            Keys.Back => 0xFF08,
            Keys.Tab => 0xFF09,
            Keys.Clear => 0xFF0B,
            Keys.Return or Keys.NumPadEnter => 0xFF0D,
            Keys.Pause => 0xFF13,
            Keys.CapsLock => 0xFFE5,
            Keys.Escape => 0xFF1B,
            Keys.Space => 0x20,
            Keys.PageUp => 0xFF55,
            Keys.PageDown => 0xFF56,
            Keys.End => 0xFF57,
            Keys.Home => 0xFF50,
            Keys.Left => 0xFF51,
            Keys.Up => 0xFF52,
            Keys.Right => 0xFF53,
            Keys.Down => 0xFF54,
            Keys.Print or Keys.Snapshot or Keys.PrintScreen => 0xFF61,
            Keys.Insert => 0xFF63,
            Keys.Delete => 0xFFFF,
            Keys.Help => 0xFF6A,
            Keys.NumLock => XkNumLock,
            Keys.Scroll => 0xFF14,
            Keys.NumPadMultiply => 0xFFAA,
            Keys.NumPadAdd => 0xFFAB,
            Keys.NumPadSeparator => 0xFFAC,
            Keys.NumPadSubtract => 0xFFAD,
            Keys.NumPadDecimal => 0xFFAE,
            Keys.NumPadDivide => 0xFFAF,
            Keys.NumPadEquals => 0xFFBD,
            Keys.BrowserBack => 0x1008FF26,
            Keys.BrowserForward => 0x1008FF27,
            Keys.BrowserRefresh => 0x1008FF29,
            Keys.BrowserStop => 0x1008FF28,
            Keys.BrowserSearch => 0x1008FF1B,
            Keys.BrowserFavorites => 0x1008FF30,
            Keys.BrowserHome => 0x1008FF18,
            Keys.VolumeMute => 0x1008FF12,
            Keys.VolumeDown => 0x1008FF11,
            Keys.VolumeUp => 0x1008FF13,
            Keys.MediaNextTrack => 0x1008FF17,
            Keys.MediaPreviousTrack => 0x1008FF16,
            Keys.MediaStop => 0x1008FF15,
            Keys.MediaPlayPause => 0x1008FF14,
            Keys.LaunchMail => 0x1008FF19,
            Keys.LaunchMediaSelect => 0x1008FF32,
            _ => 0
        };
        return keySym != 0;
    }

    private readonly record struct NativeGrab(IntPtr RootWindow, uint KeyCode, uint Modifiers);

    [StructLayout(LayoutKind.Sequential)]
    private struct XModifierKeymap
    {
        public int maxKeyPerMod;
        public IntPtr modifierMap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XKeyEvent
    {
        public int type;
        public nuint serial;
        public int sendEvent;
        public IntPtr display;
        public IntPtr window;
        public IntPtr root;
        public IntPtr subwindow;
        public nuint time;
        public int x;
        public int y;
        public int xRoot;
        public int yRoot;
        public uint state;
        public uint keycode;
        public int sameScreen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XErrorEvent
    {
        public int type;
        public IntPtr display;
        public nuint resourceId;
        public nuint serial;
        public byte errorCode;
        public byte requestCode;
        public byte minorCode;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(IntPtr display, ref XErrorEvent errorEvent);

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(string? displayName);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern int XScreenCount(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XRootWindow(IntPtr display, int screenNumber);
    [DllImport(LibX11)] private static extern uint XKeysymToKeycode(IntPtr display, ulong keySym);
    [DllImport(LibX11)] private static extern int XGrabKey(IntPtr display, int keyCode, uint modifiers, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(LibX11)] private static extern int XUngrabKey(IntPtr display, int keyCode, uint modifiers, IntPtr window);
    [DllImport(LibX11)] private static extern int XPending(IntPtr display);
    [DllImport(LibX11)] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport(LibX11)] private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport(LibX11)] private static extern IntPtr XSetErrorHandler(IntPtr handler);
    [DllImport(LibX11)] private static extern IntPtr XGetModifierMapping(IntPtr display);
    [DllImport(LibX11)] private static extern int XFreeModifiermap(IntPtr modifierMap);
}
