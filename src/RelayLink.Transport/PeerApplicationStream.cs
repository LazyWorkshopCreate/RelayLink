using System.Buffers.Binary;
using RelayLink.Protocol;

namespace RelayLink.Transport;

/// <summary>Byte stream over the existing length-prefixed peer TLS application records.</summary>
public sealed class PeerApplicationStream(Stream inner) : Stream
{
    private readonly byte[] header = new byte[4];
    private int remaining;
    private bool ended;
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

    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
