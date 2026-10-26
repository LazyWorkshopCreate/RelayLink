using System.Text;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class HttpProxyPolicyTests
{
    private static HttpProxyOptions Options => new()
    {
        PublicEntryScheme = "http", PublicAllowedAuthorities = ["panel.example.test:8080"],
        PublicAllowedOrigins = ["http://panel.example.test:8080"], UpstreamAuthority = "127.0.0.1:18789"
    };

    [Theory]
    [InlineData("null")]
    [InlineData("http://site.test:0")]
    [InlineData("https://*.example.test")]
    [InlineData("https://site.test/path")]
    [InlineData("https://site.test/")]
    [InlineData("https://user@site.test")]
    [InlineData("https://site.test,https://other.test")]
    [InlineData("https://site.test\r\nHeader: x")]
    public void Rejects_unsafe_origins(string origin) => Assert.Throws<ArgumentException>(() => HttpProxyPolicy.NormalizeOrigin(origin));

    [Fact]
    public void Normalizes_default_port_and_case() => Assert.Equal("https://site.test", HttpProxyPolicy.NormalizeOrigin("https://SITE.test:443"));

    [Fact]
    public void Rejects_rewrite_without_allowlist_and_peer_downgrade()
    {
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "tcp", false, true, Options with { OriginMode = "fixed", OriginValue = "http://localhost", PublicAllowedOrigins = [] }));
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "tcp", true, false, Options));
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "udp", false, true, Options));
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("raw", "tcp", false, true, Options));
    }

    [Fact]
    public void Rejects_tls_identity_mismatch_and_resource_path()
    {
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "tcp", false, true, Options with { UpstreamScheme = "https", HostMode = "fixed", HostValue = "different.test" }));
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "tcp", false, true, Options with { CertificateRef = "../private" }));
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "tcp", false, true, Options with { PublicEntryScheme = "https" }));
    }

    [Theory]
    [InlineData("websocket", true)]
    [InlineData("WebSocket", true)]
    [InlineData(" websocket ", true)]
    [InlineData("websocket, foo", true)]
    [InlineData("foo, websocket", true)]
    [InlineData("foo", false)]
    [InlineData("websockets", false)]
    public void Detects_websocket_upgrade_tokens(string header, bool expected) =>
        Assert.Equal(expected, HttpProxyPolicy.IsWebSocketUpgrade([header]));

    [Fact]
    public void Detects_websocket_across_multiple_upgrade_values() =>
        Assert.True(HttpProxyPolicy.IsWebSocketUpgrade(["foo", "websocket"]));

    [Fact]
    public void Empty_allowlist_rejects_websocket_origins_and_public_configuration()
    {
        Assert.False(HttpProxyPolicy.OriginPermitted(false, [], "http://evil.test", "http://panel.test", webSocket: true));
        Assert.False(HttpProxyPolicy.OriginPermitted(false, [], "http://panel.test", "http://panel.test", webSocket: true));
        Assert.True(HttpProxyPolicy.OriginPermitted(false, [], "http://evil.test", "http://panel.test", webSocket: false));
        Assert.True(HttpProxyPolicy.OriginPermitted(true, ["entry"], "http://localhost:20000", "http://localhost:20000", webSocket: true));
        Assert.False(HttpProxyPolicy.OriginPermitted(true, ["entry"], "http://evil.test", "http://localhost:20000", webSocket: true));
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "tcp", false, true, Options with { PublicAllowedOrigins = [] }));
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "tcp", false, true, Options with { WebSocketActivityTimeoutSeconds = 0 }));
        HttpProxyPolicy.Validate("http", "tcp", true, true, Options with { PublicAllowedOrigins = [], WebSocketActivityTimeoutSeconds = 0 });
        Assert.Equal(HttpProxyPolicy.DefaultWebSocketActivityTimeoutSeconds, new HttpProxyOptions().WebSocketActivityTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(HttpProxyPolicy.DefaultWebSocketActivityTimeoutSeconds), HttpProxyPolicy.ActivityTimeout(false, true, 120, 0));
        Assert.Equal(Timeout.InfiniteTimeSpan, HttpProxyPolicy.ActivityTimeout(true, true, 120, 0));
        Assert.Equal(TimeSpan.FromSeconds(120), HttpProxyPolicy.ActivityTimeout(false, false, 120, 0));
        Assert.Equal(10, HttpProxyPolicy.ResolveWebSocketConnectionCap(10, 0));
        Assert.Equal(2, HttpProxyPolicy.ResolveWebSocketConnectionCap(10, 2));
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.ResolveWebSocketConnectionCap(10, 11));
        var parsed = System.Text.Json.JsonSerializer.Deserialize<HttpProxyOptions>(
            """{"publicEntryScheme":"http","publicAllowedAuthorities":["panel.test"],"publicAllowedOrigins":["http://panel.test"],"upstreamAuthority":"upstream.test"}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Equal(300, parsed!.WebSocketActivityTimeoutSeconds);
        Assert.Contains("X-Real-IP", parsed.StripRequestHeaders);
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "tcp", false, true, Options with { StripRequestHeaders = ["bad name"] }));
        var nullHeader = System.Text.Json.JsonSerializer.Deserialize<HttpProxyOptions>(
            """{"publicEntryScheme":"http","publicAllowedAuthorities":["panel.test"],"publicAllowedOrigins":["http://panel.test"],"upstreamAuthority":"upstream.test","stripRequestHeaders":[null]}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Throws<ArgumentException>(() => HttpProxyPolicy.Validate("http", "tcp", false, true, nullHeader));
    }

    [Fact]
    public void Old_snapshots_filter_http_and_preserve_old_json_shape()
    {
        var raw = new ChannelConfiguration("raw", "raw", true, "127.0.0.1", 19001, "localhost", 80, 10, 5);
        var http = raw with { ChannelId = "web", ListenPort = 19002, ApplicationProtocol = "http", Http = Options };
        var client = new ClientConfiguration(1, "target", "target", true, "unused", 10, 10, [raw, http]);
        foreach (byte version in new byte[] { 3, 4 })
        {
            var snapshot = ConfigurationSnapshotFactory.Create(client, protocolVersion: version).Snapshot;
            Assert.Single(snapshot.Channels);
            var json = Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(snapshot, version));
            Assert.DoesNotContain("applicationProtocol", json);
            Assert.DoesNotContain("\"http\"", json);
            Assert.Equal(version == 4, json.Contains("transportProtocol"));
        }
        Assert.Equal(2, ConfigurationSnapshotFactory.Create(client).Snapshot.Channels.Count);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Known_versions_round_trip_without_switching(byte version)
    {
        using var stream = new MemoryStream();
        await new FrameWriter(stream, protocolVersion: version).WriteAsync(new Frame(FrameType.Ping, new byte[] { 1 }), CancellationToken.None);
        stream.Position = 0;
        var reader = new FrameReader(stream);
        Assert.NotNull(await reader.ReadAsync(1024, CancellationToken.None));
        Assert.Equal(version, reader.ProtocolVersion);
    }
}
