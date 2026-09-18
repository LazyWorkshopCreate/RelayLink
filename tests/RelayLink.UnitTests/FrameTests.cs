using RelayLink.Protocol;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class FrameTests
{
    [Fact]
    public async Task Reader_handles_fragmented_frame()
    {
        await using var stream = new FragmentedReadStream(new byte[]
        {
            (byte)'N', (byte)'T', (byte)'P', (byte)'1', ProtocolConstants.Version, (byte)FrameType.Data, 0, 0, 0, 0, 0, 3, 1, 2, 3
        }, 1);

        var frame = await new FrameReader(stream).ReadAsync(ProtocolConstants.MaxDataPayloadLength, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(FrameType.Data, frame.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload.ToArray());
    }

    [Fact]
    public async Task Reader_rejects_empty_data_frame()
    {
        await using var stream = new MemoryStream(new byte[] { (byte)'N', (byte)'T', (byte)'P', (byte)'1', ProtocolConstants.Version, (byte)FrameType.Data, 0, 0, 0, 0, 0, 0 });
        await Assert.ThrowsAsync<ProtocolException>(async () => await new FrameReader(stream).ReadAsync(ProtocolConstants.MaxDataPayloadLength, CancellationToken.None));
    }

    [Fact]
    public async Task Reader_rejects_pre_split_protocol_version()
    {
        await using var stream = new MemoryStream(new byte[] { (byte)'N', (byte)'T', (byte)'P', (byte)'1', 1, (byte)FrameType.Register, 0, 0, 0, 0, 0, 0 });
        await Assert.ThrowsAsync<ProtocolException>(async () => await new FrameReader(stream).ReadAsync(ProtocolConstants.MaxInitialPayloadLength, CancellationToken.None));
    }

    [Fact]
    public async Task Writer_and_reader_round_trip_control_message()
    {
        await using var stream = new MemoryStream();
        var writer = new FrameWriter(stream);
        var message = new PingMessage(42);
        await writer.WriteAsync(new Frame(FrameType.Ping, JsonProtocolSerializer.Serialize(message)), CancellationToken.None);
        stream.Position = 0;

        var frame = await new FrameReader(stream).ReadAsync(ProtocolConstants.MaxControlPayloadLength, CancellationToken.None);

        Assert.Equal(42, JsonProtocolSerializer.Deserialize<PingMessage>(frame!.Payload.Span).Sequence);
    }

    [Fact]
    public async Task Reader_times_out_only_after_a_partial_frame_arrives()
    {
        await using var stream = new StallAfterFirstReadStream(new byte[] { (byte)'N', (byte)'T', (byte)'P', (byte)'1' });
        var reader = new FrameReader(stream, TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, CancellationToken.None));
    }

    [Fact]
    public async Task Bidirectional_completion_aborts_peer_when_one_direction_fails()
    {
        var peer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = false;
        var failure = Task.FromException(new IOException("broken tunnel"));

        await Assert.ThrowsAsync<IOException>(() => RelayPump.CompleteBidirectionalAsync(failure, peer.Task, () =>
        {
            aborted = true;
            peer.TrySetCanceled();
        }));

        Assert.True(aborted);
    }

    [Fact]
    public async Task Peer_framed_stream_preserves_bytes_and_fin()
    {
        await using var wire = new MemoryStream();
        var sender = new FramedDuplexStream(new FrameReader(Stream.Null), new FrameWriter(wire));
        var input = Enumerable.Range(0, 100_000).Select(index => (byte)index).ToArray();
        await sender.WriteAsync(input);
        await sender.CompleteWritesAsync(CancellationToken.None);
        wire.Position = 0;
        var receiver = new FramedDuplexStream(new FrameReader(wire), new FrameWriter(Stream.Null));
        var result = new byte[input.Length];
        await receiver.ReadExactlyAsync(result);
        Assert.Equal(input, result);
        Assert.Equal(0, await receiver.ReadAsync(new byte[1]));
        Assert.Equal(0, await receiver.ReadAsync(new byte[1]));
    }

    private sealed class FragmentedReadStream(byte[] payload, int fragmentSize) : MemoryStream(payload)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return await base.ReadAsync(buffer[..Math.Min(fragmentSize, buffer.Length)], cancellationToken);
        }
    }

    private sealed class StallAfterFirstReadStream(byte[] firstChunk) : Stream
    {
        private bool sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!sent)
            {
                sent = true;
                firstChunk.CopyTo(buffer);
                return ValueTask.FromResult(firstChunk.Length);
            }

            return new ValueTask<int>(StallAsync(cancellationToken));
        }

        private static async Task<int> StallAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
