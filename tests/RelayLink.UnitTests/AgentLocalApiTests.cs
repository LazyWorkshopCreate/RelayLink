using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using RelayLink.Agent;
using RelayLink.Protocol;

namespace RelayLink.UnitTests;

public sealed class AgentLocalApiTests
{
    [Fact]
    public async Task Read_only_v1_api_returns_channels_and_mappings_without_secrets()
    {
        var port = GetFreePort();
        var configuration = new AgentConfiguration(
            "127.0.0.1", 7443, "local-agent", Convert.ToBase64String(new byte[32]), false, null,
            new ReconnectConfiguration(1, 2, 3))
        {
            DashboardPort = port
        };
        var status = new AgentStatus(configuration);
        status.SetOnline(new ClientConfigSnapshot(
            "local-agent", "Local Agent", 10, 5,
            [new ChannelSnapshot("database", "Database", true, "127.0.0.1", 5432, 5, 5)
            {
                AuthorizedClientsOnly = true,
                EndToEndEncryptionEnabled = false,
                AccessSecret = "channel-secret-must-not-leak"
            }])
        {
            OutboundMappings =
            [
                new OutboundMappingSnapshot(
                    "remote-api", true, "127.0.0.1", "remote-agent", "api",
                    "mapping-secret-must-not-leak", new string('A', 64))
            ]
        }, [new PeerMappingAddress("remote-api", "127.0.0.1", 23145)]);

        using var dashboard = new AgentLocalDashboard(configuration, status);
        await dashboard.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitUntilReadyAsync(http);

            using var channelsResponse = await http.GetAsync("/api/v1/channels");
            Assert.Equal(HttpStatusCode.OK, channelsResponse.StatusCode);
            Assert.Equal("no-store", channelsResponse.Headers.CacheControl?.ToString());
            Assert.Equal("application/json", channelsResponse.Content.Headers.ContentType?.MediaType);
            var channelsText = await channelsResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("channel-secret-must-not-leak", channelsText, StringComparison.Ordinal);
            using var channels = JsonDocument.Parse(channelsText);
            Assert.Equal("local-agent", channels.RootElement.GetProperty("clientId").GetString());
            Assert.True(channels.RootElement.GetProperty("online").GetBoolean());
            var channel = Assert.Single(channels.RootElement.GetProperty("channels").EnumerateArray());
            Assert.Equal("database", channel.GetProperty("channelId").GetString());
            Assert.Equal(5432, channel.GetProperty("targetPort").GetInt32());
            Assert.False(channel.GetProperty("endToEndEncryptionEnabled").GetBoolean());

            var page = await http.GetStringAsync("/");
            Assert.Contains("仅授权客户端（明文）", page, StringComparison.Ordinal);

            using var mappingsResponse = await http.GetAsync("/api/v1/mappings");
            Assert.Equal(HttpStatusCode.OK, mappingsResponse.StatusCode);
            var mappingsText = await mappingsResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("mapping-secret-must-not-leak", mappingsText, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('A', 64), mappingsText, StringComparison.Ordinal);
            using var mappings = JsonDocument.Parse(mappingsText);
            var mapping = Assert.Single(mappings.RootElement.GetProperty("mappings").EnumerateArray());
            Assert.Equal("remote-api", mapping.GetProperty("mappingId").GetString());
            Assert.Equal("127.0.0.1:23145", mapping.GetProperty("localAddress").GetString());

            using var aggregate = await http.GetFromJsonAsync<JsonDocument>("/api/v1/status");
            Assert.Single(aggregate!.RootElement.GetProperty("channels").EnumerateArray());
            Assert.Single(aggregate.RootElement.GetProperty("outboundMappings").EnumerateArray());

            using var writeAttempt = await http.PostAsJsonAsync("/api/v1/channels", new { });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, writeAttempt.StatusCode);

            status.SetOffline();
            using var offline = await http.GetFromJsonAsync<JsonDocument>("/api/v1/status");
            Assert.False(offline!.RootElement.GetProperty("online").GetBoolean());
            Assert.Empty(offline.RootElement.GetProperty("channels").EnumerateArray());
            Assert.Empty(offline.RootElement.GetProperty("outboundMappings").EnumerateArray());
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dashboard.StopAsync(deadline.Token);
        }
    }

    private static async Task WaitUntilReadyAsync(HttpClient http)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                using var response = await http.GetAsync("/api/v1/status", deadline.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) when (!deadline.IsCancellationRequested) { }

            await Task.Delay(50, deadline.Token);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
