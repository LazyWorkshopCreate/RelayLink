using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RelayLink.Agent;
using RelayLink.Testing;
using RelayLink.Transport;

namespace RelayLink.IntegrationTests;

public sealed class PeerQuicPathTests
{
    // This fixture validates socket ownership only. Its two external endpoints are nominated by the test.
    // All sockets bind to loopback. No discovery, candidate registration or NAT success is implied.
    [QuicFact]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task Nominated_udp_sockets_carry_quic_without_rebinding()
    {
        using var directory = new TestDirectory("relaylink-quic-path-");
        using var identity = AgentIdentity.LoadOrCreate(Path.Combine(directory.Path, "target.pfx"), "target");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var callerPath = LoopbackSocket();
        using var targetPath = LoopbackSocket();
        var callerEndpoint = Local(callerPath);
        var targetEndpoint = Local(targetPath);
        callerPath.Connect(targetEndpoint);
        targetPath.Connect(callerEndpoint);
        var challenge = RandomNumberGenerator.GetBytes(32);
        await callerPath.SendAsync(challenge, token);
        var probe = await targetPath.ReceiveAsync(token);
        Assert.Equal(callerEndpoint, probe.RemoteEndPoint);
        await targetPath.SendAsync(probe.Buffer, token);
        var reply = await callerPath.ReceiveAsync(token);
        Assert.Equal(targetEndpoint, reply.RemoteEndPoint);
        Assert.Equal(challenge, reply.Buffer);

        var alpn = new SslApplicationProtocol("relaylink-path-test");
        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = [alpn],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 1, DefaultCloseErrorCode = 1, MaxInboundBidirectionalStreams = 1,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions { ApplicationProtocols = [alpn], ServerCertificate = identity.Certificate }
            })
        }, token);
        using var callerAdapter = LoopbackSocket();
        using var targetAdapter = LoopbackSocket();
        targetAdapter.Connect(listener.LocalEndPoint);
        var connecting = QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0), RemoteEndPoint = Local(callerAdapter),
            DefaultStreamErrorCode = 1, DefaultCloseErrorCode = 1,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "target", ApplicationProtocols = [alpn],
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == identity.Fingerprint
            }
        }, token).AsTask();
        var first = await callerAdapter.ReceiveAsync(token);
        Assert.True(IPAddress.IsLoopback(first.RemoteEndPoint.Address));
        callerAdapter.Connect(first.RemoteEndPoint);
        await callerPath.SendAsync(first.Buffer, token);
        using var forwarding = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pumps = Task.WhenAll(CopyAsync(callerAdapter, callerPath), CopyAsync(callerPath, callerAdapter),
            CopyAsync(targetPath, targetAdapter), CopyAsync(targetAdapter, targetPath));
        try
        {
            await using var server = await listener.AcceptConnectionAsync(token);
            await using var client = await connecting;
            Assert.Equal(first.RemoteEndPoint, client.LocalEndPoint);
            await using var outgoing = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
            var data = RandomNumberGenerator.GetBytes(65507);
            await DatagramRelay.WriteAsync(outgoing, ReadOnlyMemory<byte>.Empty, token);
            await using var incoming = await server.AcceptInboundStreamAsync(token);
            await DatagramRelay.WriteAsync(outgoing, data, token);
            outgoing.CompleteWrites();
            Assert.Empty(await DatagramRelay.ReadAsync(incoming, token));
            Assert.Equal(data, await DatagramRelay.ReadAsync(incoming, token));
            Assert.Equal(0, await incoming.ReadAsync(new byte[1], token));
            await incoming.WriteAsync(data, token);
            incoming.CompleteWrites();
            var received = new byte[data.Length];
            await outgoing.ReadExactlyAsync(received, token);
            Assert.Equal(data, received);
            Assert.Equal(0, await outgoing.ReadAsync(new byte[1], token));
            Assert.Equal(callerEndpoint, Local(callerPath));
            Assert.Equal(targetEndpoint, Local(targetPath));
        }
        finally
        {
            forwarding.Cancel();
            await pumps;
        }

        async Task CopyAsync(UdpClient source, UdpClient destination)
        {
            var stop = forwarding.Token;
            try
            {
                while (true)
                {
                    var packet = await source.ReceiveAsync(stop);
                    await destination.SendAsync(packet.Buffer, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static UdpClient LoopbackSocket() => new(new IPEndPoint(IPAddress.Loopback, 0));
    private static IPEndPoint Local(UdpClient socket) => (IPEndPoint)socket.Client.LocalEndPoint!;

    private sealed class QuicFactAttribute : FactAttribute
    {
        public QuicFactAttribute()
        {
            if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !QuicListener.IsSupported || !QuicConnection.IsSupported)
                Skip = "Requires a native QUIC provider.";
        }
    }
}
