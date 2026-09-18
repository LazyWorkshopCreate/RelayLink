using RelayLink.Protocol;

namespace RelayLink.Transport;

/// <summary>One peer connection's ordered DATA/FIN frames exposed as a duplex byte stream.</summary>
public sealed class FramedDuplexStream(FrameReader reader, FrameWriter writer) : Stream
{
    private ReadOnlyMemory<byte> pending;
    private bool readCompleted;
    private int writeCompleted;
    public override bool CanRead => true;
    public override bool CanWrite => Volatile.Read(ref writeCompleted) == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (readCompleted) return 0;
        while (pending.IsEmpty)
        {
            var frame = await reader.ReadAsync(ProtocolConstants.MaxDataPayloadLength, cancellationToken)
                ?? throw new IOException("Peer tunnel ended before FIN.");
            switch (frame.Type)
            {
                case FrameType.Data: pending = frame.Payload; break;
                case FrameType.Fin: readCompleted = true; return 0;
                case FrameType.Reset: throw new IOException("Peer tunnel was reset.");
                default: throw new ProtocolException("Unexpected peer frame.");
            }
        }
        var length = Math.Min(buffer.Length, pending.Length);
        pending[..length].CopyTo(buffer);
        pending = pending[length..];
        return length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!CanWrite) throw new IOException("Peer tunnel write side is closed.");
        while (!buffer.IsEmpty)
        {
            var length = Math.Min(buffer.Length, ProtocolConstants.MaxDataPayloadLength);
            await writer.WriteAsync(new Frame(FrameType.Data, buffer[..length].ToArray()), cancellationToken);
            buffer = buffer[length..];
        }
    }

    public async Task CompleteWritesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref writeCompleted, 1) == 0)
            await writer.WriteAsync(new Frame(FrameType.Fin, ReadOnlyMemory<byte>.Empty), cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
