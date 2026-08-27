// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SnapX.Core.CLI;
using SnapX.Core;

namespace SnapX.Avalonia.Utils;

/// <summary>
/// Ensures only one GUI instance owns the tray/window and forwards any CLI
/// commands from additional launches to that instance, instead of spawning a
/// second window. The primary instance listens on a Unix domain socket in the
/// lock directory; secondary instances send their raw arguments and then exit.
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    private const string SocketName = "snapx-instance.sock";
    private const string LockName = "snapx-instance.lock";
    private const int MaximumPayloadBytes = 64 * 1024;
    private const int MaximumPendingArgSets = 32;
    private const int MaximumConcurrentClients = 8;
    private const int ClientReceiveTimeoutMilliseconds = 5000;
    private const int ForwardRetryCount = 40;
    private const int ForwardRetryDelayMilliseconds = 50;
    private const int SocketDirectoryMode = 0b111000000; // 0o700
    private const int SocketFileMode = 0b110000000; // 0o600
    private readonly Socket? _listener;
    // Keep an advisory file lock for the complete lifetime of the listener.
    // Besides electing the primary, this prevents a stale-socket cleanup from
    // unlinking a socket another SnapX process has just bound.
    private readonly FileStream _instanceLock;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string[]> _pendingArgs = new();
    private readonly SemaphoreSlim _clientSlots = new(MaximumConcurrentClients, MaximumConcurrentClients);
    private volatile bool _dispatchReady;
    private bool _disposed;

    private SingleInstanceManager(Socket listener, FileStream instanceLock)
    {
        _listener = listener;
        _instanceLock = instanceLock;
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    public static bool TryForward(string[] args, out SingleInstanceManager? primary)
    {
        primary = null;
        // FileStream.Lock is unavailable on macOS. Do not turn that platform's
        // unsupported advisory-lock operation into a false "secondary" result
        // that immediately closes the only application window.
        if (OperatingSystem.IsMacOS())
        {
            return false;
        }

        string socketPath = SocketPath();
        try
        {
            EnsureSocketDirectory(socketPath);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to create the SnapX single-instance socket directory. Running standalone.");
            return false;
        }

        // Connect first: if a live listener already exists, forward and exit.
        if (TryForwardToPrimary(socketPath, args))
        {
            return true; // Secondary; caller should exit.
        }

        // Serialize listener creation and stale-socket replacement. This lock
        // is intentionally retained by the primary for its whole lifetime.
        // Without it, two processes can both decide a socket is stale and one
        // can unlink the other process's newly bound socket.
        if (!TryAcquireInstanceLock(out var instanceLock) || instanceLock is null)
        {
            // The primary may be between acquiring the lock and listening on
            // the socket. Wait briefly for it rather than starting a second UI.
            if (TryForwardToPrimary(socketPath, args, ForwardRetryCount))
            {
                return true;
            }

            DebugHelper.WriteLine("Another SnapX instance is starting, but did not accept forwarded arguments.");
            return true;
        }

        Socket? listener = null;
        try
        {
            // A legacy primary (without the lock) may have appeared before we
            // acquired it, so check the socket once more while serialized.
            if (TryForwardToPrimary(socketPath, args))
            {
                instanceLock.Dispose();
                return true;
            }

            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener!.Bind(new UnixDomainSocketEndPoint(socketPath));
            if (OperatingSystem.IsLinux())
            {
                // Only the owning user may talk to the single-instance socket:
                // other local users must not be able to inject CLI arguments.
                Chmod(socketPath, SocketFileMode);
            }
            listener.Listen(8);
            primary = new SingleInstanceManager(listener, instanceLock);
            listener = null; // Ownership moved to primary.
            return false; // We are the primary; do not exit.
        }
        catch (SocketException)
        {
            listener?.Dispose();
            instanceLock.Dispose();
            // A process that does not use the advisory lock can still race us.
            // It must receive the command before this process exits.
            if (TryForwardToPrimary(socketPath, args, ForwardRetryCount))
            {
                return true;
            }

            DebugHelper.WriteLine("Unable to bind or forward through the SnapX single-instance socket.");
            return true;
        }
        catch (Exception ex)
        {
            listener?.Dispose();
            instanceLock.Dispose();
            DebugHelper.WriteException(ex, "Failed to establish the SnapX single-instance socket. Running standalone.");
            return false;
        }
    }

    private static void EnsureSocketDirectory(string socketPath)
    {
        string? directory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The SnapX single-instance socket has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            // The socket directory must not be world-traversable, otherwise
            // any local user could reach (or replace) the single-instance socket.
            Chmod(directory, SocketDirectoryMode);
        }
    }

    private static bool TryAcquireInstanceLock(out FileStream? instanceLock)
    {
        instanceLock = null;
        try
        {
            var stream = new FileStream(
                Path.Combine(SnapXL.LockDirectory, LockName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            stream.Lock(0, 1);
            instanceLock = stream;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryForwardToPrimary(string socketPath, string[] args, int attempts = 1)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                {
                    ReceiveTimeout = 1000,
                    SendTimeout = 1000
                };
                client.Connect(new UnixDomainSocketEndPoint(socketPath));

                byte[] payload = EncodeArgs(args);
                SendAll(client, BitConverter.GetBytes(payload.Length));
                SendAll(client, payload);
                client.Shutdown(SocketShutdown.Send);

                Span<byte> acknowledgement = stackalloc byte[1];
                return client.Receive(acknowledgement) == 1 && acknowledgement[0] == 1;
            }
            catch (SocketException) when (attempt + 1 < attempts)
            {
                Thread.Sleep(ForwardRetryDelayMilliseconds);
            }
            catch (IOException) when (attempt + 1 < attempts)
            {
                Thread.Sleep(ForwardRetryDelayMilliseconds);
            }
            catch (ObjectDisposedException) when (attempt + 1 < attempts)
            {
                Thread.Sleep(ForwardRetryDelayMilliseconds);
            }
            catch (SocketException)
            {
                // No listener is expected during normal primary startup.
                return false;
            }
            catch (IOException)
            {
                // A stale socket is handled by the caller while it owns the
                // advisory lock; do not report it as an application error.
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Failed to forward arguments to the running SnapX instance.");
                return false;
            }
        }

        return false;
    }

    private static byte[] EncodeArgs(string[] args)
    {
        string payload = string.Join('\0', args);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > MaximumPayloadBytes)
        {
            throw new InvalidOperationException("Forwarded SnapX arguments exceed the single-instance message limit.");
        }

        return bytes;
    }

    private static void SendAll(Socket socket, byte[] bytes)
    {
        int sent = 0;
        while (sent < bytes.Length)
        {
            int written = socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
            if (written <= 0)
            {
                throw new IOException("The SnapX single-instance socket closed while sending.");
            }

            sent += written;
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket client = await listener.AcceptAsync(token).ConfigureAwait(false);
                bool acquired = await _clientSlots.WaitAsync(0, token).ConfigureAwait(false);
                if (!acquired)
                {
                    // Too many simultaneous forwarders; drop instead of piling
                    // up unbounded handler tasks. The secondary's connect would
                    // fail or time out and the user can relaunch.
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClientAsync(client, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _clientSlots.Release();
                    }
                }, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Single-instance listener stopped unexpectedly.");
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                // A malicious or wedged client must not be able to hold a
                // handler slot indefinitely with a partial message.
                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                receiveCts.CancelAfter(ClientReceiveTimeoutMilliseconds);

                byte[]? payload = await ReceivePayloadAsync(client, receiveCts.Token).ConfigureAwait(false);
                if (payload is null)
                {
                    return;
                }
                string[] args = DecodeArgs(payload);

                // Bound the backlog so a flood of forwarded launches cannot
                // grow the queue without limit while dispatch is unavailable.
                while (_pendingArgs.Count >= MaximumPendingArgSets && _pendingArgs.TryDequeue(out _))
                {
                }

                _pendingArgs.Enqueue(args);
                await client.SendAsync(new byte[] { 1 }, SocketFlags.None, token).ConfigureAwait(false);

                if (_dispatchReady)
                {
                    Dispatcher.UIThread.Post(DrainQueuedArgs);
                }
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to handle forwarded SnapX arguments.");
        }
    }

    public void DrainQueuedArgs()
    {
        while (_pendingArgs.TryDequeue(out string[]? args))
        {
            Dispatcher.UIThread.Post(() => ExecuteForwardedArgs(args));
        }
    }

    public void MarkDispatchReady()
    {
        _dispatchReady = true;
        DrainQueuedArgs();
    }

    private static async Task<byte[]?> ReceivePayloadAsync(Socket client, CancellationToken token)
    {
        var lengthBuffer = new byte[sizeof(int)];
        if (!await ReceiveExactlyAsync(client, lengthBuffer, token).ConfigureAwait(false))
        {
            return null;
        }

        int length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length < 0 || length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Received an invalid SnapX single-instance message length.");
        }

        var payload = new byte[length];
        return await ReceiveExactlyAsync(client, payload, token).ConfigureAwait(false) ? payload : null;
    }

    private static async Task<bool> ReceiveExactlyAsync(Socket client, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await client.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, token).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static string[] DecodeArgs(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return [];
        }

        return Encoding.UTF8.GetString(payload).Split('\0');
    }

    private static void ExecuteForwardedArgs(string[] args)
    {
        // Dispatch the forwarded CLI commands on a background thread so running
        // a capture job (which may itself await the UI or select a region) does
        // not block the UI thread or deadlock around Dispatcher.UIThread.
        _ = Task.Run(() =>
        {
            try
            {
                var manager = new SnapXCLIManager(args);
                manager.ParseCommands();
                manager.UseCommandLineArgs().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                    DebugHelper.WriteException(ex, "Failed to execute forwarded SnapX arguments."));
            }
        });
    }

    private static string SocketPath() =>
        Path.Combine(SnapXL.LockDirectory, SocketName);

    /// <summary>
    /// Restricts the single-instance socket and its directory to the owning
    /// user. There is no cross-platform permission API in the current
    /// dependency set, so this shells down to libc directly (Linux-only path).
    /// </summary>
    private static void Chmod(string path, int mode)
    {
        if (chmod(path, mode) != 0)
        {
            throw new IOException($"Failed to set permissions {Convert.ToString(mode, 8)} on '{path}'.");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, int mode);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        // Unlink while we still hold the ownership lock. A successor cannot
        // bind a replacement socket until _instanceLock is disposed below.
        try
        {
            string socketPath = SocketPath();
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }
        }
        catch
        {
            // Best-effort shutdown.
        }
        try
        {
            _listener?.Close();
        }
        catch
        {
            // Best-effort cleanup.
        }
        _cts.Dispose();
        _instanceLock.Dispose();
    }
}
