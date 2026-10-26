using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using RelayLink.Agent;
using RelayLink.Protocol;

namespace RelayLink.UnitTests;

public sealed class HttpProxyRuntimeTests
{
    [Fact]
    public void Http_access_proof_binds_mode_policy_mapping_scheme_and_confirmed_authority()
    {
        var secret = Convert.ToBase64String(new byte[32]);
        var id = Guid.NewGuid();
        var challenge = new byte[32];
        var ingress = new HttpIngressContext("peer", "http", CallerClientId: "caller", MappingId: "mapping", EntryAuthority: "localhost:30000", PolicyHash: new string('a', 64));
        byte[] Proof(HttpIngressContext? context) => PeerSessionCoordinator.ComputeProof(secret, "caller", "web", id, true, challenge, context);
        var expected = Proof(ingress);
        Assert.NotEqual(expected, Proof(null));
        foreach (var changed in new[] { ingress with { Scheme = "https" }, ingress with { MappingId = "other" },
            ingress with { PolicyHash = new string('b', 64) }, ingress with { EntryAuthority = "localhost:30001" } })
            Assert.NotEqual(expected, Proof(changed));
    }

    private static AgentConfiguration Agent => new("127.0.0.1", 7443, "agent", Convert.ToBase64String(new byte[32]), true, null,
        new ReconnectConfiguration(1, 2, 3)) { E2eIdentityPath = Path.Combine(Path.GetTempPath(), "relaylink-unused-" + Guid.NewGuid().ToString("N"), "identity.pfx") };
    private static ChannelSnapshot Channel => new("web", "Web", true, "127.0.0.1", 8080, 3, 5)
    {
        ApplicationProtocol = "http", Http = new HttpProxyOptions { PublicEntryScheme = "http", PublicAllowedAuthorities = ["panel.test"], PublicAllowedOrigins = ["http://panel.test"], UpstreamAuthority = "upstream.test" }
    };
    private static ClientConfigSnapshot Snapshot(ChannelSnapshot channel) => new("agent", "Agent", 10, 5, [channel]);

    [Fact]
    public async Task Changed_or_revoked_policy_and_new_channel_require_process_restart()
    {
        await using var runtime = new HttpProxyRuntime(Agent);
        var channel = Channel;
        await runtime.PrepareAsync(Snapshot(channel), CancellationToken.None);
        Assert.True(runtime.IsReady(channel));
        Assert.Equal("ready", Assert.Single(runtime.States).State);
        var changed = channel with { TargetPort = 8081 };
        await runtime.PrepareAsync(Snapshot(changed), CancellationToken.None);
        Assert.Equal("restart_required", Assert.Single(runtime.States).State);
        Assert.False(runtime.IsReady(channel));
        await runtime.PrepareAsync(Snapshot(channel), CancellationToken.None);
        Assert.Equal("restart_required", Assert.Single(runtime.States).State);
        await runtime.PrepareAsync(Snapshot(channel with { ChannelId = "new" }), CancellationToken.None);
        Assert.Equal("restart_required", Assert.Single(runtime.States).State);
    }

    [Fact]
    public async Task Missing_certificate_isolated_and_unavailable_without_reloading()
    {
        await using var runtime = new HttpProxyRuntime(Agent);
        var channel = Channel with { Http = Channel.Http! with { PublicEntryScheme = "https", CertificateRef = "missing" } };
        await runtime.PrepareAsync(Snapshot(channel), CancellationToken.None);
        Assert.Equal("resource_unavailable", Assert.Single(runtime.States).State);
        Assert.False(runtime.IsReady(channel));
        await runtime.PrepareAsync(Snapshot(channel), CancellationToken.None);
        Assert.Equal("resource_unavailable", Assert.Single(runtime.States).State);
    }

    [Fact]
    public async Task Caller_mapping_change_is_disabled_until_restart_and_raw_mapping_stays_enabled()
    {
        await using var runtime = new HttpProxyRuntime(Agent);
        var mapping = new OutboundMappingSnapshot("web-entry", true, "127.0.0.1", "target", "web", Convert.ToBase64String(new byte[32]), new string('a', 64))
        { ApplicationProtocol = "http", Http = new HttpPeerOptions("http", "localhost", new string('b', 64)) };
        var raw = mapping with { MappingId = "raw-entry", ApplicationProtocol = "raw", Http = null };
        var snapshot = new ClientConfigSnapshot("agent", "Agent", 10, 5, []) { OutboundMappings = [mapping, raw] };
        await runtime.PrepareAsync(snapshot, CancellationToken.None);
        Assert.All(runtime.EffectivePeerConfiguration(snapshot).OutboundMappings, item => Assert.True(item.Enabled));
        var updated = snapshot with { OutboundMappings = [mapping with { Http = mapping.Http! with { Scheme = "https" } }, raw] };
        await runtime.PrepareAsync(updated, CancellationToken.None);
        Assert.False(runtime.EffectivePeerConfiguration(updated).OutboundMappings[0].Enabled);
        Assert.True(runtime.EffectivePeerConfiguration(updated).OutboundMappings[1].Enabled);
        await runtime.PrepareAsync(snapshot, CancellationToken.None);
        Assert.False(runtime.EffectivePeerConfiguration(snapshot).OutboundMappings[0].Enabled);
    }

    [Fact]
    public async Task Revoked_or_removed_host_is_disposed_and_dropped()
    {
        var agent = Agent;
        var directory = Path.GetDirectoryName(agent.E2eIdentityPath)!;
        WriteCertificate(Path.Combine(directory, "http-resources"));
        await using var runtime = new HttpProxyRuntime(agent);
        var channel = Channel with { Http = Channel.Http! with { PublicEntryScheme = "https", CertificateRef = "test" } };
        await runtime.PrepareAsync(Snapshot(channel), CancellationToken.None);
        var host = Host(runtime, channel.ChannelId);
        Assert.NotNull(host);
        Assert.Equal(1, runtime.ActiveHostCount);
        var certificate = (X509Certificate2)typeof(HttpProxyHost).GetField("certificate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var client = (HttpMessageInvoker)typeof(HttpProxyHost).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var app = (WebApplication)typeof(HttpProxyHost).GetField("app", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var stopped = app.Lifetime.ApplicationStopped;

        await runtime.PrepareAsync(Snapshot(channel with { TargetPort = 8081 }), CancellationToken.None);
        Assert.Equal("restart_required", Assert.Single(runtime.States).State);
        Assert.Equal(0, runtime.ActiveHostCount);
        Assert.Null(Host(runtime, channel.ChannelId));
        Assert.True(host.IsDisposed);
        Assert.True(stopped.IsCancellationRequested);
        Assert.True(Record.Exception(() => _ = certificate.Thumbprint) is ObjectDisposedException or CryptographicException);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/"), CancellationToken.None));
        var ingress = new HttpIngressContext("public", "https", "192.0.2.10", PolicyHash: HttpProxyPolicy.Hash(channel));
        await Assert.ThrowsAsync<ProtocolException>(() => runtime.ServeAsync(channel, Stream.Null, ingress, CancellationToken.None));

        await runtime.PrepareAsync(new ClientConfigSnapshot("agent", "Agent", 10, 5, []), CancellationToken.None);
        Assert.Empty(runtime.States);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public async Task Oversized_websocket_cap_stays_unavailable_without_retaining_a_host()
    {
        await using var runtime = new HttpProxyRuntime(Agent);
        var channel = Channel with { Http = Channel.Http! with { MaxWebSocketConnections = 50 } };
        await runtime.PrepareAsync(Snapshot(channel), CancellationToken.None);
        Assert.Equal("resource_unavailable", Assert.Single(runtime.States).State);
        Assert.Equal(0, runtime.ActiveHostCount);
    }

    [Fact]
    public async Task Concurrent_prepare_and_serve_do_not_surface_object_disposed()
    {
        await using var runtime = new HttpProxyRuntime(Agent);
        var channel = Channel;
        await runtime.PrepareAsync(Snapshot(channel), CancellationToken.None);
        var ingress = new HttpIngressContext("public", "http", "192.0.2.10", PolicyHash: HttpProxyPolicy.Hash(channel));
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serves = Enumerable.Range(0, 8).Select(async _ =>
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
                var accepted = await listener.AcceptTcpClientAsync();
                try
                {
                    await runtime.ServeAsync(channel, accepted.GetStream(), ingress, CancellationToken.None);
                }
                catch (Exception exception) when (exception is ProtocolException or IOException or OperationCanceledException or SocketException)
                { }
                catch (Exception exception) { errors.Enqueue(exception); }
                finally { accepted.Dispose(); }
            }
            finally { client.Dispose(); }
        }).ToArray();
        await Task.Delay(200);
        var preparing = Task.Run(async () =>
        {
            await runtime.PrepareAsync(Snapshot(channel with { TargetPort = 9 }), CancellationToken.None);
            await runtime.PrepareAsync(Snapshot(channel), CancellationToken.None);
        });
        await Task.WhenAll(serves.Concat([preparing]));
        Assert.Empty(errors);
        Assert.Equal(0, runtime.ActiveHostCount);
        await Assert.ThrowsAsync<ProtocolException>(() => runtime.ServeAsync(channel, Stream.Null, ingress, CancellationToken.None));
    }

    private static HttpProxyHost? Host(HttpProxyRuntime runtime, string id)
    {
        var hosts = (Dictionary<string, (string Hash, HttpProxyHost? Host)>)typeof(HttpProxyRuntime)
            .GetField("hosts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        return hosts.TryGetValue(id, out var value) ? value.Host : null;
    }

    private static void WriteCertificate(string resources)
    {
        Directory.CreateDirectory(resources);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=panel.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("panel.test");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllText(Path.Combine(resources, "test.crt.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(resources, "test.key.pem"), key.ExportPkcs8PrivateKeyPem());
    }
}
