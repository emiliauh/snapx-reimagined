// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;

namespace SnapX.Core.Utils.Native;

/// <summary>ScreenCaptureKit H.264/AAC segment capture. Run on a worker thread.
/// Source coordinates are local display points; output dimensions are logical pixels.</summary>
public sealed class MacOSSystemAudioRecorder : IDisposable
{
    private const string Library = "snapx-system-audio";
    private readonly object gate = new();
    private ulong handle;
    private bool hasRun;
    private bool running;
    private bool disposed;

    public static bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsMacOSVersionAtLeast(13)) return false;
            try { return Available() != 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
        }
    }

    public MacOSSystemAudioRecorder(string outputPath, uint displayId,
        double x, double y, double sourceWidth, double sourceHeight,
        int outputWidth, int outputHeight, int fps, bool cursor,
        double duration = 0, int videoBitrate = 12_000_000, int audioBitrate = 192_000)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("Direct system audio requires macOS 13 or later and the SnapX native capture component.");
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        handle = Create(Path.GetFullPath(outputPath), displayId, x, y, sourceWidth, sourceHeight,
            outputWidth, outputHeight, fps, cursor ? 1 : 0, duration, videoBitrate, audioBitrate);
        if (handle == 0) throw new ArgumentException("Invalid screen and system audio recording configuration.");
    }

    public bool Run()
    {
        ulong current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (hasRun) throw new InvalidOperationException("A capture segment can only run once.");
            hasRun = true;
            running = true;
            current = handle;
        }
        try
        {
            if (RunNative(current) != 0) return true;
            byte[] buffer = new byte[4096];
            Error(current, buffer, buffer.Length);
            int length = Array.IndexOf(buffer, (byte)0);
            string message = Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
            throw new IOException(string.IsNullOrWhiteSpace(message)
                ? "Screen and system audio capture failed."
                : message);
        }
        finally
        {
            lock (gate)
            {
                running = false;
                if (disposed) { Destroy(handle); handle = 0; }
            }
        }
    }

    public void RequestStop()
    {
        lock (gate) { if (handle != 0) Stop(handle); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Stop(handle);
            // Run retains the token until it has read the native error. Dispose
            // requests cancellation without waiting for the worker or UI thread.
            if (!running) { Destroy(handle); handle = 0; }
        }
    }

    [DllImport(Library, EntryPoint = "snapx_system_audio_available")] private static extern int Available();
    [DllImport(Library, EntryPoint = "snapx_system_audio_create")] private static extern ulong Create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint display, double x, double y,
        double width, double height, int outputWidth, int outputHeight, int fps, int cursor,
        double duration, int videoBitrate, int audioBitrate);
    [DllImport(Library, EntryPoint = "snapx_system_audio_run")] private static extern int RunNative(ulong token);
    [DllImport(Library, EntryPoint = "snapx_system_audio_stop")] private static extern void Stop(ulong token);
    [DllImport(Library, EntryPoint = "snapx_system_audio_error")] private static extern void Error(ulong token, [Out] byte[] buffer, int capacity);
    [DllImport(Library, EntryPoint = "snapx_system_audio_destroy")] private static extern void Destroy(ulong token);
}
