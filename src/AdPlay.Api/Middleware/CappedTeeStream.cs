namespace AdPlay.Api.Middleware;

/// <summary>
/// Forwards every write to the real response body immediately and keeps a copy of at most <c>capacity</c> bytes,
/// so logging never buffers the whole response or delays streaming.
/// </summary>
public sealed class CappedTeeStream(Stream inner, int capacity) : Stream
{
    private readonly byte[] _captured = new byte[capacity];
    private int _capturedLength;

    public long TotalBytesWritten { get; private set; }
    public bool Truncated => TotalBytesWritten > _capturedLength;
    public ReadOnlySpan<byte> Captured => _captured.AsSpan(0, _capturedLength);

    /// <summary>Discards the capture, e.g. when an error handler clears a half-written response.</summary>
    public void ResetCapture()
    {
        _capturedLength = 0;
        TotalBytesWritten = 0;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Capture(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        Capture(buffer.Span);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void Capture(ReadOnlySpan<byte> data)
    {
        TotalBytesWritten += data.Length;
        var room = _captured.Length - _capturedLength;
        if (room <= 0) return;
        var take = Math.Min(room, data.Length);
        data[..take].CopyTo(_captured.AsSpan(_capturedLength));
        _capturedLength += take;
    }
}
