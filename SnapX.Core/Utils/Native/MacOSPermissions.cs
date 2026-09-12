// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SnapX.Core.Utils.Native;

public enum MacOSPermissionKind
{
    ScreenRecording,
    Microphone
}

public enum MacOSPermissionStatus
{
    Unavailable = -1,
    NotDetermined,
    Restricted,
    Denied,
    Authorized
}

public sealed class MacOSPermissionException : UnauthorizedAccessException
{
    public MacOSPermissionKind Permission { get; }
    public string SettingsUrl { get; }

    public MacOSPermissionException(MacOSPermissionKind permission)
        : base(GetMessage(permission))
    {
        Permission = permission;
        SettingsUrl = MacOSPermissions.GetSettingsUrl(permission);
    }

    private static string GetMessage(MacOSPermissionKind permission) => permission switch
    {
        MacOSPermissionKind.ScreenRecording =>
            "SnapX needs Screen Recording permission. Open System Settings > Privacy & Security > Screen Recording, allow SnapX, then try again.",
        MacOSPermissionKind.Microphone =>
            "SnapX needs Audio Input permission for the selected system-audio loopback device. Open System Settings > Privacy & Security > Microphone, allow SnapX, then try again. macOS classifies virtual loopback feeds as microphone inputs.",
        _ => "SnapX does not have the required macOS permission."
    };
}

/// <summary>Read and request the macOS privacy permissions used by capture and recording.</summary>
public static unsafe partial class MacOSPermissions
{
    public const string ScreenRecordingSettingsUrl =
        "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture";
    public const string MicrophoneSettingsUrl =
        "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone";

    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string AVFoundation =
        "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const int BlockHasSignature = 1 << 30;

    private static readonly object MicrophoneRequestLock = new();
    private static Task<bool>? microphoneRequest;
    private static long microphoneRequestCompletedAt;

    private static readonly Lazy<AVFoundationSymbols> AVSymbols = new(LoadAVFoundationSymbols);
    private static readonly Lazy<BlockSymbols> Blocks = new(LoadBlockSymbols);

    public static bool IsSupported => OperatingSystem.IsMacOSVersionAtLeast(10, 15);

    /// <remarks>
    /// Core Graphics does not distinguish a first-time request from a denial, so every
    /// non-authorized screen state is reported as <see cref="MacOSPermissionStatus.Denied"/>.
    /// Calling this method never displays a system prompt.
    /// </remarks>
    public static MacOSPermissionStatus GetScreenCaptureStatus()
    {
        if (!IsSupported) return MacOSPermissionStatus.Unavailable;
        return CGPreflightScreenCaptureAccess()
            ? MacOSPermissionStatus.Authorized
            : MacOSPermissionStatus.Denied;
    }

    public static bool HasScreenCaptureAccess() =>
        GetScreenCaptureStatus() == MacOSPermissionStatus.Authorized;

    /// <summary>Displays the macOS Screen Recording prompt when the choice is not yet determined.</summary>
    public static bool RequestScreenCaptureAccess()
    {
        if (!IsSupported) return false;
        return CGRequestScreenCaptureAccess();
    }

    public static MacOSPermissionStatus GetMicrophoneStatus()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(10, 14)) return MacOSPermissionStatus.Unavailable;

        AVFoundationSymbols symbols = AVSymbols.Value;
        nint value = SendAuthorizationStatus(
            symbols.CaptureDeviceClass,
            symbols.AuthorizationStatusSelector,
            symbols.AudioMediaType);
        return value switch
        {
            0 => MacOSPermissionStatus.NotDetermined,
            1 => MacOSPermissionStatus.Restricted,
            2 => MacOSPermissionStatus.Denied,
            3 => MacOSPermissionStatus.Authorized,
            _ => MacOSPermissionStatus.Unavailable
        };
    }

    /// <summary>
    /// Requests microphone access if it has not been decided. Concurrent callers share one native request.
    /// </summary>
    public static Task<bool> RequestMicrophoneAccessAsync(CancellationToken cancellationToken = default)
    {
        Task<bool> request;
        lock (MicrophoneRequestLock)
        {
            // Read inside the lock so two first-time callers cannot both act on a
            // stale NotDetermined result while a very fast native callback finishes.
            MacOSPermissionStatus status = GetMicrophoneStatus();
            if (status != MacOSPermissionStatus.NotDetermined)
                return Task.FromResult(status == MacOSPermissionStatus.Authorized);

            // Only coalesce callers while the native request is in flight. Keeping a
            // completed task forever prevents a new AVFoundation request if TCC is
            // reset while SnapX is still running and the status becomes NotDetermined
            // again. A brief grace interval also absorbs status-propagation lag after
            // a synchronous/very fast callback without dispatching a duplicate prompt.
            bool recentlyCompleted = microphoneRequest?.IsCompleted == true &&
                Environment.TickCount64 - Volatile.Read(ref microphoneRequestCompletedAt) < 1000;
            if (microphoneRequest is null || microphoneRequest.IsCompleted && !recentlyCompleted)
            {
                microphoneRequest = BeginMicrophoneRequest();
                Volatile.Write(ref microphoneRequestCompletedAt, long.MaxValue);
                _ = microphoneRequest.ContinueWith(
                    _ => Volatile.Write(ref microphoneRequestCompletedAt, Environment.TickCount64),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            request = microphoneRequest;
        }

        return cancellationToken.CanBeCanceled ? request.WaitAsync(cancellationToken) : request;
    }

    public static string GetSettingsUrl(MacOSPermissionKind permission) => permission switch
    {
        MacOSPermissionKind.ScreenRecording => ScreenRecordingSettingsUrl,
        MacOSPermissionKind.Microphone => MicrophoneSettingsUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(permission))
    };

    public static void ThrowIfScreenCaptureAccessDenied()
    {
        if (OperatingSystem.IsMacOS() && !HasScreenCaptureAccess())
            throw new MacOSPermissionException(MacOSPermissionKind.ScreenRecording);
    }

    public static void ThrowIfMicrophoneAccessDenied()
    {
        if (OperatingSystem.IsMacOS() && GetMicrophoneStatus() != MacOSPermissionStatus.Authorized)
            throw new MacOSPermissionException(MacOSPermissionKind.Microphone);
    }

    private static Task<bool> BeginMicrophoneRequest()
    {
        AVFoundationSymbols symbols = AVSymbols.Value;
        Task<bool> task = CreateMicrophoneCompletionBlock(out MicrophoneRequestState state);
        try
        {
            SendRequestAccess(
                symbols.CaptureDeviceClass,
                symbols.RequestAccessSelector,
                symbols.AudioMediaType,
                state.Block);
        }
        catch
        {
            state.DisposeWithoutCallback();
            throw;
        }
        return task;
    }

    // Invoked by the macOS native smoke/property test through reflection. It exercises the
    // exact unmanaged block trampoline and lifetime rules without requesting a host permission.
    internal static Task<bool> RunMicrophoneCallbackInteropProbe(bool granted)
    {
        Task<bool> task = CreateMicrophoneCompletionBlock(out MicrophoneRequestState state);
        nint retainedBlock = BlockCopy(state.Block);
        try
        {
            var invoke = (delegate* unmanaged[Cdecl]<nint, byte, void>)((BlockLiteral*)retainedBlock)->Invoke;
            invoke(retainedBlock, granted ? (byte)1 : (byte)0);
        }
        finally
        {
            BlockRelease(retainedBlock);
        }
        return task;
    }

    private static Task<bool> CreateMicrophoneCompletionBlock(out MicrophoneRequestState state)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        state = new MicrophoneRequestState(completion);
        state.Handle = GCHandle.Alloc(state);

        BlockSymbols blocks = Blocks.Value;
        BlockLiteral* literal = (BlockLiteral*)NativeMemory.Alloc((nuint)sizeof(BlockLiteral));
        literal->Isa = blocks.ConcreteStackBlock;
        literal->Flags = BlockHasSignature;
        literal->Reserved = 0;
        literal->Invoke = (nint)(delegate* unmanaged[Cdecl]<nint, byte, void>)&MicrophoneAccessCompleted;
        literal->Descriptor = blocks.Descriptor;
        literal->Context = GCHandle.ToIntPtr(state.Handle);

        try
        {
            state.Block = BlockCopy((nint)literal);
            if (state.Block == 0) throw new InvalidOperationException("macOS could not create a microphone permission callback.");
        }
        catch
        {
            state.Handle.Free();
            throw;
        }
        finally
        {
            NativeMemory.Free(literal);
        }

        return completion.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MicrophoneAccessCompleted(nint block, byte granted)
    {
        try
        {
            nint context = ((BlockLiteral*)block)->Context;
            if (context == 0) return;
            var handle = GCHandle.FromIntPtr(context);
            if (handle.Target is MicrophoneRequestState state) state.Complete(granted != 0);
        }
        catch
        {
            // Exceptions cannot cross the unmanaged block invocation boundary.
        }
    }

    private static AVFoundationSymbols LoadAVFoundationSymbols()
    {
        nint framework = NativeLibrary.Load(AVFoundation);
        nint captureDeviceClass = GetClass("AVCaptureDevice");
        nint audioMediaTypeAddress = NativeLibrary.GetExport(framework, "AVMediaTypeAudio");
        nint audioMediaType = Marshal.ReadIntPtr(audioMediaTypeAddress);
        if (captureDeviceClass == 0 || audioMediaType == 0)
            throw new PlatformNotSupportedException("AVFoundation microphone authorization is unavailable.");
        return new AVFoundationSymbols(
            captureDeviceClass,
            Selector("authorizationStatusForMediaType:"),
            Selector("requestAccessForMediaType:completionHandler:"),
            audioMediaType);
    }

    private static BlockSymbols LoadBlockSymbols()
    {
        nint systemLibrary = NativeLibrary.Load(SystemLibrary);
        nint concreteStackBlock = NativeLibrary.GetExport(systemLibrary, "_NSConcreteStackBlock");

        // Descriptor fields are reserved, literal size, then the Objective-C block signature.
        nint descriptor = Marshal.AllocHGlobal(IntPtr.Size * 3);
        Marshal.WriteIntPtr(descriptor, 0, 0);
        Marshal.WriteIntPtr(descriptor, IntPtr.Size, sizeof(BlockLiteral));
        Marshal.WriteIntPtr(descriptor, IntPtr.Size * 2, Marshal.StringToCoTaskMemUTF8("v@?c"));
        return new BlockSymbols(concreteStackBlock, descriptor);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
        public nint Context;
    }

    private sealed class MicrophoneRequestState(TaskCompletionSource<bool> completion)
    {
        private int completed;
        public GCHandle Handle;
        public nint Block;

        public void Complete(bool granted)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            completion.TrySetResult(granted);
            BlockRelease(Block);
            Handle.Free();
        }

        public void DisposeWithoutCallback()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            BlockRelease(Block);
            Handle.Free();
        }
    }

    private sealed record AVFoundationSymbols(
        nint CaptureDeviceClass,
        nint AuthorizationStatusSelector,
        nint RequestAccessSelector,
        nint AudioMediaType);

    private sealed record BlockSymbols(nint ConcreteStackBlock, nint Descriptor);

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CGPreflightScreenCaptureAccess();

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CGRequestScreenCaptureAccess();

    [LibraryImport(ObjectiveC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjectiveC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial nint SendAuthorizationStatus(nint receiver, nint selector, nint mediaType);

    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial void SendRequestAccess(nint receiver, nint selector, nint mediaType, nint completionHandler);

    [LibraryImport(SystemLibrary, EntryPoint = "_Block_copy")]
    private static partial nint BlockCopy(nint block);

    [LibraryImport(SystemLibrary, EntryPoint = "_Block_release")]
    private static partial void BlockRelease(nint block);
}
