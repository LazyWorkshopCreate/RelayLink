using System.Buffers.Binary;
using RelayLink.Protocol;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class PeerApplicationStreamTests
{
    [Fact]
    public async Task Large_write_roundtrips_through_bounded_records_partial_reads_and_fin()
    {
        using var storage = new MemoryStream();
        var stream = new PeerApplicationStream(storage);
        var payload = new byte[100_000]; Random.Shared.NextBytes(payload);
        await stream.WriteAsync(payload);
        storage.Write(new byte[4]); storage.Position = 0;
        var received = new byte[payload.Length];
        var offset = 0;
        while (offset < received.Length)
            offset += await stream.ReadAsync(received.AsMemory(offset, Math.Min(997, received.Length - offset)));
        Assert.Equal(payload, received);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(ProtocolConstants.MaxDataPayloadLength + 1)]
    public async Task Invalid_record_length_is_rejected_before_allocation(int length)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        using var stream = new PeerApplicationStream(new MemoryStream(bytes));
        await Assert.ThrowsAsync<ProtocolException>(async () => { _ = await stream.ReadAsync(new byte[1]); });
    }

    [Fact]
    public async Task Truncated_record_is_not_treated_as_graceful_fin()
    {
        using var stream = new PeerApplicationStream(new MemoryStream(new byte[] { 0, 0, 0, 1 }));
        await Assert.ThrowsAsync<IOException>(async () => { _ = await stream.ReadAsync(new byte[1]); });
    }

    [Fact]
    public async Task CompleteWritesAsync_is_idempotent_writes_zero_header_and_blocks_future_writes()
    {
        await using var wire = new MemoryStream();
        await using var stream = new PeerApplicationStream(wire, leaveOpen: true);
        await stream.WriteAsync(new byte[] { 10, 20, 30 });
        await stream.CompleteWritesAsync();
        await stream.CompleteWritesAsync();

        wire.Position = 0;
        var reader = new PeerApplicationStream(wire, leaveOpen: true);
        var buf = new byte[16];
        Assert.Equal(3, await reader.ReadAsync(buf));
        Assert.Equal(0, await reader.ReadAsync(buf));
        Assert.Equal(4 + 3 + 4, wire.Length);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.WriteAsync(new byte[] { 1 }));
    }
}
