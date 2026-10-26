#if LEGACY_V4_AGENT_TESTS
using System.Net;
using System.Net.Sockets;
using RelayLink.Protocol;

namespace RelayLink.IntegrationTests;

public sealed class HttpProxyLegacyAgentTests
{
    private static string Legacy => Environment.GetEnvironmentVariable("RELAYLINK_LEGACY_V4_AGENT_ASSEMBLY")
        ?? throw new InvalidOperationException("Set RELAYLINK_LEGACY_V4_AGENT_ASSEMBLY to the checksum-verified unmodified rc.9 Agent DLL.");

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Real_v4_raw_peer_tls_and_half_close_remain_compatible(bool oldCaller, bool oldTarget)
    {
        using var fixture = new PeerConnectionTests.Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.StartAsync(callerAssembly: oldCaller ? Legacy : null, targetAssembly: oldTarget ? Legacy : null);
        await fixture.WaitOnlineAsync(deadline.Token);
        using var socket = await fixture.ConnectLocalAsync(deadline.Token);
        var payload = new byte[100_000]; Random.Shared.NextBytes(payload);
        var stream = socket.GetStream();
        await stream.WriteAsync(payload, deadline.Token);
        socket.Client.Shutdown(SocketShutdown.Send);
        var received = new byte[payload.Length];
        await stream.ReadExactlyAsync(received, deadline.Token);
        Assert.Equal(payload, received);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_v4_http_is_filtered_or_rejected_and_raw_mapping_stays_online(bool oldCaller)
    {
        using var fixture = new PeerConnectionTests.Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new HttpProxyOptions { PublicEntryScheme = "http", PublicAllowedAuthorities = [$"127.0.0.1:{fixture.ProxyPort}"], PublicAllowedOrigins = [$"http://127.0.0.1:{fixture.ProxyPort}"], UpstreamAuthority = "upstream.test" };
        await fixture.StartAsync(callerAssembly: oldCaller ? Legacy : null, targetAssembly: oldCaller ? null : Legacy,
            httpOptions: options, httpTargetPort: fixture.TunnelPort);
        await fixture.WaitOnlineAsync(deadline.Token, requireMappings: false);
        var rawPort = await fixture.WaitPortAsync("to-visited-b", deadline.Token);
        using var raw = await fixture.ConnectLocalAsync(rawPort, deadline.Token);
        var rawStream = raw.GetStream();
        await rawStream.WriteAsync(new byte[] { 1, 2, 3 }, deadline.Token);
        raw.Client.Shutdown(SocketShutdown.Send);
        var received = new byte[4]; await rawStream.ReadExactlyAsync(received, deadline.Token);
        Assert.Equal(new byte[] { (byte)'B', 1, 2, 3 }, received);
        if (oldCaller) Assert.Throws<InvalidOperationException>(() => fixture.LocalPort);
        else
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{fixture.ProxyPort}/", deadline.Token));
        }
    }
}
#endif
