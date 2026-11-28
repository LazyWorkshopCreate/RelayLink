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
    public async Task Writer_rejects_frames_the_reader_cannot_accept_before_writing()
    {
        await using var stream = new MemoryStream();
        var writer = new FrameWriter(stream);
        await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(new Frame(FrameType.Data, ReadOnlyMemory<byte>.Empty), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(new Frame(FrameType.Data, new byte[ProtocolConstants.MaxDataPayloadLength + 1]), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(new Frame(FrameType.Fin, new byte[] { 1 }), CancellationToken.None).AsTask());
        Assert.Equal(0, stream.Length);

        await writer.WriteAsync(new Frame(FrameType.Data, new byte[ProtocolConstants.MaxDataPayloadLength]), CancellationToken.None);
        await writer.WriteAsync(new Frame(FrameType.Fin, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task Reader_rejects_empty_data_frame()
    {
        await using var stream = new MemoryStream(new byte[] { (byte)'N', (byte)'T', (byte)'P', (byte)'1', ProtocolConstants.Version, (byte)FrameType.Data, 0, 0, 0, 0, 0, 0 });
        await Assert.ThrowsAsync<ProtocolException>(async () => await new FrameReader(stream).ReadAsync(ProtocolConstants.MaxDataPayloadLength, CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData((byte)(ProtocolConstants.Version + 1))]
    public async Task Reader_rejects_unsupported_protocol_version(byte version)
    {
        await using var stream = new MemoryStream(new byte[] { (byte)'N', (byte)'T', (byte)'P', (byte)'1', version, (byte)FrameType.Register, 0, 0, 0, 0, 0, 0 });
        await Assert.ThrowsAsync<ProtocolException>(async () => await new FrameReader(stream).ReadAsync(ProtocolConstants.MaxInitialPayloadLength, CancellationToken.None));
    }

    [Fact]
    public async Task Reader_accepts_v3_and_rejects_version_change_within_connection()
    {
        await using var stream = new MemoryStream();
        await new FrameWriter(stream, protocolVersion: 3).WriteAsync(new Frame(FrameType.Ping, new byte[] { 1 }), CancellationToken.None);
        await new FrameWriter(stream).WriteAsync(new Frame(FrameType.Ping, new byte[] { 1 }), CancellationToken.None);
        stream.Position = 0;
        var reader = new FrameReader(stream);
        Assert.Equal(FrameType.Ping, (await reader.ReadAsync(100, CancellationToken.None))!.Type);
        Assert.Equal((byte)3, reader.ProtocolVersion);
        await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync(100, CancellationToken.None));
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

    [Fact]
    public async Task FrameWriter_latches_permanent_fault_after_write_error()
    {
        await using var stream = new ToggleThrowStream();
        var writer = new FrameWriter(stream);
        var ping = new Frame(FrameType.Ping, new byte[] { 1 });

        await Assert.ThrowsAsync<FrameWriteException>(async () => await writer.WriteAsync(ping, CancellationToken.None));
        stream.ShouldThrow = false;
        var second = await Assert.ThrowsAsync<FrameWriteException>(async () => await writer.WriteAsync(ping, CancellationToken.None));
        Assert.Contains("faulted", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, stream.WriteCalls);
    }

    [Fact]
    public async Task Reader_times_out_when_header_complete_but_payload_missing()
    {
        var header = new byte[12];
        header[0] = (byte)'N'; header[1] = (byte)'T'; header[2] = (byte)'P'; header[3] = (byte)'1';
        header[4] = ProtocolConstants.Version;
        header[5] = (byte)FrameType.Ping;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), 8);
        await using var stream = new StallAfterFirstReadStream(header);
        var reader = new FrameReader(stream, TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, CancellationToken.None));
    }

    [Fact]
    public async Task Reader_resets_partial_timeout_on_incremental_payload_progress()
    {
        var payload = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        await using var wire = new MemoryStream();
        await new FrameWriter(wire).WriteAsync(new Frame(FrameType.Pong, payload), CancellationToken.None);
        var fullFrame = wire.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var time = new ManualTimeProvider();
        await using var stream = new ManualChunkStream([
            fullFrame[..12],
            fullFrame[12..16],
            fullFrame[16..20],
            fullFrame[20..24],
            fullFrame[24..28],
            fullFrame[28..]
        ]);
        var reader = new FrameReader(stream, TimeSpan.FromSeconds(10), time);

        var readTask = reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token).AsTask();
        for (var i = 1; i <= 5; i++)
        {
            await stream.WaitUntilChunkRequestedAsync(i, deadline.Token);
            // Total elapsed fake time reaches 40s (4x partialFrameTimeout), so completion requires per-chunk resets.
            time.Advance(TimeSpan.FromSeconds(8));
            Assert.False(readTask.IsCompleted);
            stream.ReleaseChunk(i);
        }

        var decoded = await readTask;
        Assert.NotNull(decoded);
        Assert.Equal(FrameType.Pong, decoded!.Type);
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public async Task Reader_times_out_when_progress_stalls_after_partial_payload()
    {
        var payload = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        await using var wire = new MemoryStream();
        await new FrameWriter(wire).WriteAsync(new Frame(FrameType.Pong, payload), CancellationToken.None);
        var fullFrame = wire.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var time = new ManualTimeProvider();
        await using var stream = new ManualChunkStream([
            fullFrame[..12],
            fullFrame[12..16],
            fullFrame[16..20],
            fullFrame[20..24],
            fullFrame[24..28],
            fullFrame[28..]
        ]);
        var reader = new FrameReader(stream, TimeSpan.FromSeconds(10), time);

        var readTask = reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token).AsTask();
        await stream.WaitUntilChunkRequestedAsync(1, deadline.Token);
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.False(readTask.IsCompleted);
        stream.ReleaseChunk(1);

        await stream.WaitUntilChunkRequestedAsync(2, deadline.Token);
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.False(readTask.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    private sealed class ToggleThrowStream : Stream
    {
        public bool ShouldThrow { get; set; } = true;
        public int WriteCalls { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (ShouldThrow) return ValueTask.FromException(new IOException("injected write failure"));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualChunkStream(byte[][] chunks) : Stream
    {
        private readonly TaskCompletionSource[] requested = Enumerable.Range(0, chunks.Length)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly TaskCompletionSource[] gates = Enumerable.Range(0, chunks.Length)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private int index;
        private int offset;
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

        public Task WaitUntilChunkRequestedAsync(int chunkIndex, CancellationToken cancellationToken) =>
            requested[chunkIndex].Task.WaitAsync(cancellationToken);

        public void ReleaseChunk(int chunkIndex) => gates[chunkIndex].TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (index >= chunks.Length) return 0;
            if (index > 0 && offset == 0)
            {
                requested[index].TrySetResult();
                await gates[index].Task.WaitAsync(cancellationToken);
            }
            var chunk = chunks[index];
            var available = chunk.Length - offset;
            var take = Math.Min(available, buffer.Length);
            chunk.AsSpan(offset, take).CopyTo(buffer.Span);
            offset += take;
            if (offset == chunk.Length) { index++; offset = 0; }
            return take;
        }
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
