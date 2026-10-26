using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RelayLink.Protocol;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class DatagramTests
{
    [Fact]
    public async Task Target_and_listener_sockets_have_room_for_maximum_datagram()
    {
        using var target = await DatagramRelay.ConnectTargetAsync("127.0.0.1", 1, CancellationToken.None);
        Assert.True(target.Client.SendBufferSize >= DatagramRelay.MaxPayloadLength);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listener = new DatagramListener(socket);
        Assert.True(socket.Client.SendBufferSize >= DatagramRelay.MaxPayloadLength);
        listener.Stop();
    }

    [Fact]
    public async Task Datagram_codec_preserves_boundaries_with_fragmented_reads_including_empty_payload()
    {
        using var wire = new MemoryStream();
        var packets = new[] { Array.Empty<byte>(), new byte[] { 1 }, Enumerable.Range(0, 65507).Select(i => (byte)i).ToArray() };
        foreach (var packet in packets) await DatagramRelay.WriteAsync(wire, packet, CancellationToken.None);
        using var fragmented = new FragmentedStream(wire.ToArray());
        foreach (var packet in packets) Assert.Equal(packet, await DatagramRelay.ReadAsync(fragmented, CancellationToken.None));
        await Assert.ThrowsAsync<EndOfStreamException>(() => DatagramRelay.ReadAsync(fragmented, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65508)]
    public async Task Invalid_datagram_length_is_rejected_before_payload_allocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        using var wire = new MemoryStream(header);
        await Assert.ThrowsAsync<ProtocolException>(() => DatagramRelay.ReadAsync(wire, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_datagram_is_never_delivered()
    {
        using var wire = new MemoryStream(new byte[] { 0, 0, 0, 3, 1 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => DatagramRelay.ReadAsync(wire, CancellationToken.None));
    }

    [Fact]
    public async Task Full_queue_reports_each_dropped_datagram_and_keeps_drop_newest()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listener = new DatagramListener(socket);
        var endpoint = new IPEndPoint(IPAddress.Loopback, listener.Port);
        var dropped = 0;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = listener.RunAsync(1, _ => true, session =>
        {
            opened.TrySetResult();
            return Task.Delay(Timeout.Infinite, session.Token);
        }, CancellationToken.None, null, () => Interlocked.Increment(ref dropped));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        for (var index = 0; index < DatagramRelay.QueueCapacity + 3; index++)
            await sender.SendAsync(new byte[] { (byte)index }, endpoint);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref dropped) < 3 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(3, Volatile.Read(ref dropped));
        listener.Stop();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Queue_drops_new_datagram_when_full()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var session = new DatagramSession(new IPEndPoint(IPAddress.Loopback, 1), socket);
        for (var i = 0; i < DatagramRelay.QueueCapacity; i++) Assert.True(session.Enqueue(new byte[] { (byte)i }));
        Assert.False(session.Enqueue(new byte[] { 255 }));
        var metrics = new ChannelMetrics();
        Assert.Equal(1, metrics.DatagramDropped());
        Assert.Equal(1, metrics.Snapshot().UdpDroppedTotal);
    }

    [Fact]
    public async Task Idle_expiry_and_cancellation_finish_both_directions()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var session = new DatagramSession(new IPEndPoint(IPAddress.Loopback, 1), socket);
        using var stream = new WaitingStream();
        await session.RelayAsync(stream, 1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var task = session.RelayAsync(stream, 60, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Listener_limits_sources_and_stop_cancels_owned_sessions()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listener = new DatagramListener(socket);
        var endpoint = new IPEndPoint(IPAddress.Loopback, listener.Port);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var task = listener.RunAsync(1, _ => true, async session =>
        {
            Interlocked.Increment(ref count);
            opened.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, session.Token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        }, CancellationToken.None);
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await first.SendAsync(new byte[] { 1 }, endpoint);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await second.SendAsync(new byte[] { 2 }, endpoint);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref count));
        listener.Stop();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handler_failure_removes_only_that_session(bool synchronous)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listener = new DatagramListener(socket);
        var endpoint = new IPEndPoint(IPAddress.Loopback, listener.Port);
        var failures = new List<string>();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var task = listener.RunAsync(1, _ => true, session =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                if (synchronous) throw new InvalidOperationException("sync-handler");
                return Task.FromException(new InvalidOperationException("async-handler"));
            }
            opened.TrySetResult();
            return Task.Delay(Timeout.Infinite, session.Token);
        }, CancellationToken.None, exception => failures.Add(exception.Message));
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await first.SendAsync(new byte[] { 1 }, endpoint);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (failures.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await second.SendAsync(new byte[] { 2 }, endpoint);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(synchronous ? "sync-handler" : "async-handler", Assert.Single(failures));
        Assert.False(task.IsFaulted);
        listener.Stop();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }

    private sealed class WaitingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
