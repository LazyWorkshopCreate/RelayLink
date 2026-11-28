using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using RelayLink.Agent;
using RelayLink.Protocol;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class PeerRelayControlTests
{
    private static PeerRelayBinding Binding() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "caller", "target", "mapping", "channel", "tcp", false);
    private const string Hash = "1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF";

    [Fact]
    public async Task Canceling_an_attempt_during_a_shared_write_preserves_the_next_frame()
    {
        using var session = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var attempt = new CancellationTokenSource();
        using var stream = new PausedWriteStream();
        var writer = new FrameWriter(stream);
        var prepare = new Frame(FrameType.PeerRelayPrepare, new byte[] { 10, 20 });
        var writing = writer.WriteControlAsync(prepare, attempt.Token, session.Token).AsTask();
        await stream.HeaderWritten.Task.WaitAsync(session.Token);
        var queued = writer.WriteControlAsync(prepare, attempt.Token, session.Token).AsTask();
        attempt.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        stream.Resume.TrySetResult();
        await writer.WriteAsync(new Frame(FrameType.Ping, new byte[] { 3 }), session.Token);
        stream.Position = 0;
        var reader = new FrameReader(stream);
        Assert.Equal(prepare.Payload.ToArray(), (await reader.ReadAsync(1024, session.Token))!.Payload.ToArray());
        Assert.Equal(FrameType.Ping, (await reader.ReadAsync(1024, session.Token))?.Type);
        Assert.Null(await reader.ReadAsync(1024, session.Token));
    }

    [Fact]
    public async Task Setup_expires_while_a_final_ready_frame_is_still_being_written()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        using var blocked = new PausedWriteStream();
        var binding = Binding();
        var caller = new Session(binding.CallerSessionId, "caller", DateTimeOffset.UtcNow);
        var target = new Session(binding.TargetSessionId, "target", DateTimeOffset.UtcNow);
        var toTarget = new Pipe();
        caller.AttachControlWriter(new FrameWriter(blocked));
        target.AttachControlWriter(new FrameWriter(toTarget.Writer.AsStream()));
        var setup = new PeerRelaySetup(binding, TimeSpan.FromSeconds(5), () => true);
        var report = new PeerRelaySignal(binding, Hash);
        var completing = setup.ConfirmAsync(caller, target, attempt.Token);
        Assert.True(setup.Report(false, FrameType.PeerRelayAuthenticated, report));
        Assert.True(setup.Report(true, FrameType.PeerRelayAuthenticated, report));
        var prepared = await new FrameReader(toTarget.Reader.AsStream()).ReadAsync(4096, limit.Token);
        Assert.True(setup.Report(true, FrameType.PeerRelayTargetReady, JsonProtocolSerializer.Deserialize<PeerRelaySignal>(prepared!.Payload.Span)));
        await blocked.HeaderWritten.Task.WaitAsync(limit.Token);
        attempt.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completing.WaitAsync(limit.Token));
        Assert.False(limit.IsCancellationRequested);
        blocked.Resume.TrySetResult();
        await caller.SendAsync(new Frame(FrameType.Ping, new byte[] { 1 }), limit.Token);
        blocked.Position = 0;
        var reader = new FrameReader(blocked);
        Assert.Equal(FrameType.PeerRelayTargetReady, (await reader.ReadAsync(4096, limit.Token))?.Type);
        Assert.Equal(FrameType.Ping, (await reader.ReadAsync(4096, limit.Token))?.Type);
    }

    [Fact]
    public async Task Ready_before_receiving_the_prepare_permit_cannot_release_the_caller()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var blocked = new PausedWriteStream();
        using var toCaller = new MemoryStream();
        var binding = Binding();
        var caller = new Session(binding.CallerSessionId, "caller", DateTimeOffset.UtcNow);
        var target = new Session(binding.TargetSessionId, "target", DateTimeOffset.UtcNow);
        caller.AttachControlWriter(new FrameWriter(toCaller));
        target.AttachControlWriter(new FrameWriter(blocked));
        var setup = new PeerRelaySetup(binding, TimeSpan.FromSeconds(5), () => true);
        var report = new PeerRelaySignal(binding, Hash);
        var completing = setup.ConfirmAsync(caller, target, deadline.Token);
        Assert.True(setup.Report(false, FrameType.PeerRelayAuthenticated, report));
        Assert.True(setup.Report(true, FrameType.PeerRelayAuthenticated, report));
        await blocked.HeaderWritten.Task.WaitAsync(deadline.Token);
        Assert.False(setup.Report(true, FrameType.PeerRelayTargetReady, report));
        deadline.Cancel();
        blocked.Resume.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completing);
        Assert.Equal(0, toCaller.Length);
    }

    [Fact]
    public async Task Both_authenticated_roles_and_current_target_are_required_in_order()
    {
        var binding = Binding();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var caller = new Session(binding.CallerSessionId, "caller", DateTimeOffset.UtcNow);
        var target = new Session(binding.TargetSessionId, "target", DateTimeOffset.UtcNow);
        var toCaller = new Pipe();
        var toTarget = new Pipe();
        caller.AttachControlWriter(new FrameWriter(toCaller.Writer.AsStream()));
        target.AttachControlWriter(new FrameWriter(toTarget.Writer.AsStream()));
        var current = true;
        var setup = new PeerRelaySetup(binding, TimeSpan.FromSeconds(5), () => current);
        var report = new PeerRelaySignal(binding, Hash);
        var completing = setup.ConfirmAsync(caller, target, deadline.Token);
        Assert.True(setup.Report(false, FrameType.PeerRelayAuthenticated, report));
        Assert.True(setup.Report(false, FrameType.PeerRelayAuthenticated, report));
        Assert.False(toTarget.Reader.TryRead(out _));
        Assert.True(setup.Report(true, FrameType.PeerRelayAuthenticated, report));
        var prepare = await new FrameReader(toTarget.Reader.AsStream()).ReadAsync(4096, deadline.Token);
        Assert.Equal(FrameType.PeerRelayPrepare, prepare?.Type);
        var permission = JsonProtocolSerializer.Deserialize<PeerRelaySignal>(prepare!.Payload.Span);
        Assert.NotNull(permission.PreparationId);
        Assert.NotEqual(Guid.Empty, permission.PreparationId);
        Assert.Equal(report, permission with { PreparationId = null });
        Assert.False(toCaller.Reader.TryRead(out _));
        current = false;
        Assert.True(setup.Report(true, FrameType.PeerRelayTargetReady, permission));
        await Assert.ThrowsAsync<ProtocolException>(() => completing);
        Assert.False(toCaller.Reader.TryRead(out _));
    }

    [Fact]
    public void Conflicting_transcripts_and_out_of_order_readiness_end_the_attempt()
    {
        var report = new PeerRelaySignal(Binding(), Hash);
        var setup = new PeerRelaySetup(report.Binding, TimeSpan.FromSeconds(5), () => true);
        Assert.True(setup.Report(false, FrameType.PeerRelayAuthenticated, report));
        Assert.False(setup.Report(true, FrameType.PeerRelayAuthenticated, report with { HandshakeId = new string('A', 64) }));
        Assert.False(setup.Report(true, FrameType.PeerRelayAuthenticated, report));
        var premature = new PeerRelaySetup(report.Binding, TimeSpan.FromSeconds(5), () => true);
        Assert.False(premature.Report(false, FrameType.PeerRelayTargetReady, report));
        Assert.False(premature.Report(true, FrameType.PeerRelayTargetReady, report));
    }

    [Fact]
    public async Task Data_byte_and_stale_attempt_cannot_confirm_a_caller()
    {
        var binding = Binding();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var source = new TcpClient();
        await source.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var controls = new Pipe();
        await using var confirmation = new PeerRelayConfirmation(binding, false, new FrameWriter(controls.Writer.AsStream()), deadline, deadline.Token, _ => true, () => { });
        var waiting = confirmation.AuthenticateAsync(source.GetStream(), Convert.ToBase64String(new byte[32]));
        await peer.GetStream().WriteAsync(new byte[32], deadline.Token);
        await peer.GetStream().ReadExactlyAsync(new byte[64], deadline.Token);
        await peer.GetStream().WriteAsync(new byte[] { 1 }, deadline.Token);
        var report = await new FrameReader(controls.Reader.AsStream()).ReadAsync(4096, deadline.Token);
        Assert.Equal(FrameType.PeerRelayAuthenticated, report?.Type);
        var signal = JsonProtocolSerializer.Deserialize<PeerRelaySignal>(report!.Payload.Span);
        confirmation.Receive(FrameType.PeerRelayTargetReady, signal with { Binding = binding with { AttemptId = Guid.NewGuid() } });
        Assert.False(waiting.IsCompleted);
        confirmation.Receive(FrameType.PeerRelayTargetReady, signal);
        await waiting.WaitAsync(deadline.Token);
        var first = new byte[1];
        await source.GetStream().ReadExactlyAsync(first, deadline.Token);
        Assert.Equal(1, first[0]);
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void Legacy_wire_omits_relay_capabilities_and_attempts(byte version)
    {
        var register = new RegisterMessage("caller", "unused", "test") { RelayControlReady = true };
        var open = new PeerOpenMessage(Guid.NewGuid(), Guid.NewGuid(), "caller", "channel", "unused") { RelayBinding = Binding() };
        Assert.False(JsonProtocolSerializer.Deserialize<RegisterMessage>(JsonProtocolSerializer.Serialize(register, version)).RelayControlReady);
        Assert.Null(JsonProtocolSerializer.Deserialize<PeerOpenMessage>(JsonProtocolSerializer.Serialize(open, version)).RelayBinding);
    }

    private sealed class PausedWriteStream : MemoryStream
    {
        public TaskCompletionSource HeaderWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            await base.WriteAsync(bytes, token);
            if (HeaderWritten.TrySetResult()) await Resume.Task.WaitAsync(token);
        }
    }
}
