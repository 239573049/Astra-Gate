using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Astra.Desktop.Core.Tray;

/// <summary>
/// One desktop app per data directory (Electron's <c>app.requestSingleInstanceLock()</c> + <c>'second-instance'</c>, desktop/src/main.ts):
/// a second launch asks the running app to show its main window and exits. A tray app is easy to "launch again" from the Dock, Start menu
/// or a launcher while it hides in the tray, and two of them would each supervise (and stop) the same server.
/// <para>
/// Ownership is an exclusive lock on <c>&lt;root&gt;/desktop.lock</c> (<see cref="FileShare.None"/>: <c>LockFileEx</c> on Windows, <c>flock</c> on
/// Unix), which the OS drops when the process dies — a crash never leaves a stale lock. The signal is a local named pipe
/// (a Unix domain socket in the temp directory on macOS / Linux) limited to the current user, named after the data directory, so a
/// scratch <c>ASTRA_HOME</c> (tests, a second profile) is its own instance just like it has its own server.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string LockFileName = "desktop.lock";
    private const string ActivateMessage = "activate";
    private const string AckMessage = "ok";

    private readonly FileStream? _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private SingleInstance(FileStream? lockFile, string pipeName)
    {
        _lock = lockFile;
        _pipeName = pipeName;
    }

    /// <summary>The pipe name for a data directory (stable, short: Unix socket paths are limited to ~104 bytes).</summary>
    public static string PipeNameFor(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) full = full.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return "astra-desktop-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// Becomes the instance for <paramref name="root"/>, or returns null when another process already is. A data directory that cannot
    /// be created or locked (read-only home) is not a reason to refuse to start: it yields an instance without the guarantee.
    /// </summary>
    public static SingleInstance? TryClaim(string root)
    {
        var path = Path.Combine(root, LockFileName);
        try
        {
            Directory.CreateDirectory(root);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return new SingleInstance(stream, PipeNameFor(root));
        }
        catch (IOException) when (File.Exists(path))
        {
            // Held by the running instance.
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SingleInstance(null, PipeNameFor(root));
        }
    }

    /// <summary>
    /// The second launch's side: tells the running instance to activate its window. True when it acknowledged within
    /// <paramref name="timeout"/> (it may still be starting up and not listening yet, so connecting is retried until then).
    /// </summary>
    public static bool SignalRunning(string root, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeNameFor(root), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                var left = deadline - DateTime.UtcNow;
                pipe.Connect((int)Math.Clamp(left.TotalMilliseconds, 1, 1000));
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 64, leaveOpen: true);
                writer.WriteLine(ActivateMessage);
                var readTask = reader.ReadLineAsync();
                if (readTask.Wait(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds)) && readTask.Result == AckMessage) return true;
                // Hung up without an answer: on Unix a connection that reached the listener just as it was recycled between two
                // launches is reset. Try again until the deadline (activating twice is harmless).
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(50);
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or AggregateException)
            {
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Starts answering later launches; <paramref name="onActivate"/> runs on a background thread (marshal to the UI thread).</summary>
    public void Listen(Action onActivate)
    {
        if (_loop is not null) throw new InvalidOperationException("already listening");
        _loop = Task.Run(() => LoopAsync(onActivate, _stop.Token));
    }

    private async Task LoopAsync(Action onActivate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, false, 64, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line != ActivateMessage) continue;
                await writer.WriteLineAsync(AckMessage.AsMemory(), ct).ConfigureAwait(false);
                onActivate();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A client that hung up or sent nothing: wait for the next one (briefly, so a persistent error cannot spin).
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Stops listening and releases the lock.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Cancelled.
        }
        _lock?.Dispose();
        _stop.Dispose();
    }
}
