namespace Astra.Desktop.Core.Update;

/// <summary>Where a running update is (<see cref="UpdateProgress.Stage"/>), in the order an update goes through them.</summary>
public enum UpdateStage
{
    /// <summary>Asked to update; the feed is read or the download is about to start.</summary>
    Preparing,

    /// <summary>The package / installer is being downloaded (<see cref="UpdateProgress.Received"/> / <see cref="UpdateProgress.Total"/>).</summary>
    Downloading,

    /// <summary>The download is checked (checksum, bundle id, signature) and unpacked.</summary>
    Verifying,

    /// <summary>The new server binary is put in place.</summary>
    Installing,

    /// <summary>The server is restarted on the new binary.</summary>
    Restarting,

    /// <summary>The app update is staged: the app quits now and the hand-over installs and reopens it.</summary>
    Relaunching,

    /// <summary>The server was updated and answers with the new version.</summary>
    Done,

    /// <summary>The update failed (<see cref="UpdateProgress.Error"/>); a server swap was rolled back.</summary>
    Failed,
}

/// <summary>
/// One step of an apply, as <see cref="UpdateCoordinator"/> reports it (<see cref="UpdateCoordinatorOptions.Progress"/>) to whatever shows it —
/// the bottom-right update card, whoever started the update (Settings, the tray, the reminder).
/// </summary>
/// <param name="Stage">Where the update is.</param>
/// <param name="Version">The version being installed, when known.</param>
/// <param name="Received">Bytes downloaded so far (<see cref="UpdateStage.Downloading"/>).</param>
/// <param name="Total">The download's size, or null when the server did not say.</param>
/// <param name="Error">What went wrong (<see cref="UpdateStage.Failed"/>), user-facing.</param>
public sealed record UpdateProgress(UpdateStage Stage, string? Version = null, long Received = 0, long? Total = null, string? Error = null)
{
    /// <summary>The download's share done, 0–1, or null when the size is unknown (an indeterminate bar).</summary>
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : null;

    /// <summary>The update has ended one way or the other (the app quits after <see cref="UpdateStage.Relaunching"/>).</summary>
    public bool IsFinished => Stage is UpdateStage.Done or UpdateStage.Failed or UpdateStage.Relaunching;
}

/// <summary>Bytes of one download so far; <paramref name="Total"/> is null when neither the feed nor the response said how large it is.</summary>
public readonly record struct TransferProgress(long Received, long? Total);

/// <summary>An <see cref="IProgress{T}"/> that calls back on the reporting thread (no <see cref="SynchronizationContext"/> capture, unlike <see cref="Progress{T}"/>).</summary>
public sealed class ProgressSink<T>(Action<T> report) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => report(value);
}

/// <summary>A read-only pass-through stream that reports how many bytes have been read from it (the npm tarball as it is unpacked).</summary>
public sealed class ProgressReadStream(Stream inner, long? total, IProgress<TransferProgress>? progress) : Stream
{
    private long _read;

    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Count(await inner.ReadAsync(buffer, cancellationToken));

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int n)
    {
        if (n <= 0) return n;
        _read += n;
        progress?.Report(new TransferProgress(_read, total));
        return n;
    }

    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
