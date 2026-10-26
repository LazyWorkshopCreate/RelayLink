using RelayLink.Protocol;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class SessionConfigurationTests
{
    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public async Task Consecutive_updates_accept_each_ack_in_wire_order(byte protocolVersion)
    {
        await using var stream = new MemoryStream();
        var session = new Session(Guid.NewGuid(), "agent", DateTimeOffset.UtcNow) { ProtocolVersion = protocolVersion };
        session.AttachControlWriter(new FrameWriter(stream, protocolVersion: protocolVersion));
        var snapshot = new ClientConfigSnapshot("agent", "Agent", 10, 5, []);
        await session.SendConfigurationUpdateAsync(snapshot, "first", CancellationToken.None);
        await session.SendConfigurationUpdateAsync(snapshot, "second", CancellationToken.None);
        Assert.Throws<ProtocolException>(() => session.ConfirmConfiguration("second"));
        session.ConfirmConfiguration("first");
        Assert.Equal("first", session.ConfigVersion);
        session.ConfirmConfiguration("second");
        Assert.Equal("second", session.ConfigVersion);
        Assert.Throws<ProtocolException>(() => session.ConfirmConfiguration("second"));
    }

    [Fact]
    public async Task Unacknowledged_updates_are_bounded_without_closing_the_session()
    {
        var session = new Session(Guid.NewGuid(), "agent", DateTimeOffset.UtcNow);
        session.AttachControlWriter(new FrameWriter(Stream.Null));
        session.MarkReady();
        var snapshot = new ClientConfigSnapshot("agent", "Agent", 10, 5, []);
        for (var index = 0; index < 32; index++)
            await session.SendConfigurationUpdateAsync(snapshot, index.ToString(), CancellationToken.None);
        await Assert.ThrowsAsync<ProtocolException>(async () =>
            await session.SendConfigurationUpdateAsync(snapshot, "overflow", CancellationToken.None));
        Assert.True(session.IsReady);
        Assert.False(session.LifetimeToken.IsCancellationRequested);
        session.ConfirmConfiguration("0");
        Assert.Equal("0", session.ConfigVersion);
    }

    [Fact]
    public async Task Partial_configuration_write_closes_the_session_and_drops_the_version()
    {
        var session = new Session(Guid.NewGuid(), "agent", DateTimeOffset.UtcNow);
        session.AttachControlWriter(new FrameWriter(new PartialFrameStream(), TimeSpan.FromSeconds(2)));
        session.MarkReady();
        var snapshot = new ClientConfigSnapshot("agent", "Agent", 10, 5, []);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => session.SendConfigurationUpdateAsync(snapshot, "partial", CancellationToken.None).AsTask());
        Assert.IsType<FrameWriteException>(exception);
        Assert.False(session.IsReady);
        Assert.True(session.LifetimeToken.IsCancellationRequested);
        Assert.Throws<ProtocolException>(() => session.ConfirmConfiguration("partial"));
    }

    private sealed class PartialFrameStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        private readonly MemoryStream written = new();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("partial write");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0) return ValueTask.CompletedTask;
            written.WriteByte(buffer.Span[0]);
            return ValueTask.FromException(new IOException("partial write"));
        }
    }
}
