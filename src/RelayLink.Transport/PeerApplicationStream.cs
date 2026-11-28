using System.Buffers.Binary;
using RelayLink.Protocol;

namespace RelayLink.Transport;

/// <summary>Byte stream over the existing length-prefixed peer TLS application records.</summary>
public sealed class PeerApplicationStream(Stream inner, bool leaveOpen = false) : Stream
{
    private static readonly byte[] ZeroHeader = new byte[4];
    private readonly byte[] header = new byte[4];
    private int remaining;
    private bool ended;
    private int writeCompleted;
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty || ended) return 0;
        if (remaining == 0)
        {
            await inner.ReadExactlyAsync(header, cancellationToken);
            remaining = BinaryPrimitives.ReadInt32BigEndian(header);
            if (remaining == 0) { ended = true; return 0; }
            if (remaining < 0 || remaining > ProtocolConstants.MaxDataPayloadLength)
                throw new ProtocolException("Invalid peer application frame length.");
        }
        var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, remaining)], cancellationToken);
        if (count == 0) throw new IOException("Peer application record was truncated.");
        remaining -= count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref writeCompleted) != 0)
            throw new InvalidOperationException("Peer application write stream has already completed.");
        var lengthHeader = new byte[4];
        while (!buffer.IsEmpty)
        {
            var count = Math.Min(buffer.Length, ProtocolConstants.MaxDataPayloadLength);
            BinaryPrimitives.WriteInt32BigEndian(lengthHeader, count);
            await inner.WriteAsync(lengthHeader, cancellationToken);
            await inner.WriteAsync(buffer[..count], cancellationToken);
            buffer = buffer[count..];
        }
    }

    public async ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref writeCompleted, 1) != 0) return;
        await inner.WriteAsync(ZeroHeader, cancellationToken);
        await inner.FlushAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => leaveOpen ? ValueTask.CompletedTask : inner.DisposeAsync();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
