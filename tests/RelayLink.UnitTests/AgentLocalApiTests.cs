using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayLink.Agent;
using RelayLink.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace RelayLink.UnitTests;

public sealed class AgentLocalApiTests
{
    [Fact]
    public async Task Local_write_api_requires_session_csrf_and_origin_without_password()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-admin-api-");
        var port = GetFreePort();
        var origin = $"http://127.0.0.1:{port}";
        var configurationPath = Path.Combine(directory.FullName, "agent.json");
        var configuration = new AgentProcessConfiguration { DashboardPort = port, Servers = [] };
        File.WriteAllText(configurationPath, """{"dashboardPort":PORT,"servers":[]}""".Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var runtime = new AgentProcessRuntime(configuration, new AgentConfigurationPath(configurationPath), NullLoggerFactory.Instance);
        using var dashboard = new AgentLocalDashboard(configuration, runtime);
        try
        {
            await runtime.StartAsync(CancellationToken.None);
            await dashboard.StartAsync(CancellationToken.None);
            using var http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri(origin) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                try
                {
                    using var ready = await http.GetAsync("/api/v2/status", deadline.Token);
                    if (ready.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(50, deadline.Token);
            }
            var profileSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var profile = new AgentServerProfile
            {
                ProfileId = "east", ServerHost = "127.0.0.1", ServerPort = 7443, DataPort = 7444,
                ClientId = "same", Secret = profileSecret, Reconnect = new ReconnectConfiguration(1, 2, 3)
            };
            var version = runtime.ConfigurationVersion;
            using (var anonymous = await SendAsync(http, HttpMethod.Post, "/api/v2/admin/servers", origin, null, new { version, server = profile, confirmExistingState = false }))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var session = await http.GetAsync("/api/v2/admin/session", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            using var sessionJson = JsonDocument.Parse(await session.Content.ReadAsStringAsync(deadline.Token));
            var csrf = sessionJson.RootElement.GetProperty("csrfToken").GetString()!;
            using (var foreignSessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v2/admin/session"))
            {
                foreignSessionRequest.Headers.TryAddWithoutValidation("Origin", "https://other.example");
                using var foreignSession = await http.SendAsync(foreignSessionRequest, deadline.Token);
                Assert.Equal(HttpStatusCode.Forbidden, foreignSession.StatusCode);
            }
            using (var noCsrf = await SendAsync(http, HttpMethod.Post, "/api/v2/admin/servers", origin, null, new { version, server = profile, confirmExistingState = false }))
                Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
            using (var crossSite = await SendAsync(http, HttpMethod.Post, "/api/v2/admin/servers", "https://other.example", csrf, new { version, server = profile, confirmExistingState = false }))
                Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
            using (var added = await SendAsync(http, HttpMethod.Post, "/api/v2/admin/servers", origin, csrf, new { version, server = profile, confirmExistingState = false }))
            {
                Assert.Equal(HttpStatusCode.OK, added.StatusCode);
                Assert.DoesNotContain(profileSecret, await added.Content.ReadAsStringAsync(deadline.Token), StringComparison.Ordinal);
            }
            Assert.Single(runtime.Snapshot);
            using (var updated = await SendAsync(http, HttpMethod.Put, "/api/v2/admin/servers/east", origin, csrf,
                new { version = runtime.ConfigurationVersion, server = profile with { ServerHost = "updated.example" } }))
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Equal("updated.example", Assert.Single(runtime.Snapshot).ServerHost);
            using (var missingState = await SendAsync(http, HttpMethod.Put, "/api/v2/admin/servers/east/enabled", origin, csrf,
                new { version = runtime.ConfigurationVersion }))
                Assert.Equal(HttpStatusCode.BadRequest, missingState.StatusCode);
            using (var noStateCsrf = await SendAsync(http, HttpMethod.Put, "/api/v2/admin/servers/east/enabled", origin, null,
                new { version = runtime.ConfigurationVersion, enabled = false }))
                Assert.Equal(HttpStatusCode.Forbidden, noStateCsrf.StatusCode);
            using (var disabled = await SendAsync(http, HttpMethod.Put, "/api/v2/admin/servers/east/enabled", origin, csrf,
                new { version = runtime.ConfigurationVersion, enabled = false }))
                Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            Assert.False(Assert.Single(runtime.Snapshot).Enabled);
            using (var status = await http.GetFromJsonAsync<JsonDocument>("/api/v2/status", deadline.Token))
                Assert.False(status!.RootElement.GetProperty("servers")[0].GetProperty("enabled").GetBoolean());
            using (var enabled = await SendAsync(http, HttpMethod.Put, "/api/v2/admin/servers/east/enabled", origin, csrf,
                new { version = runtime.ConfigurationVersion, enabled = true }))
                Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.True(Assert.Single(runtime.Snapshot).Enabled);
            using (var wrongIdentity = await SendAsync(http, HttpMethod.Put, "/api/v2/admin/servers/east", origin, csrf,
                new { version = runtime.ConfigurationVersion, server = profile with { ClientId = "other" } }))
                Assert.Equal(HttpStatusCode.Conflict, wrongIdentity.StatusCode);
            using (var stale = await SendAsync(http, HttpMethod.Post, "/api/v2/admin/servers", origin, csrf, new { version, server = profile with { ProfileId = "west" }, confirmExistingState = false }))
            {
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                Assert.DoesNotContain(profileSecret, await stale.Content.ReadAsStringAsync(deadline.Token), StringComparison.Ordinal);
            }
            using (var status = await http.GetAsync("/api/v2/status", deadline.Token))
                Assert.DoesNotContain(profileSecret, await status.Content.ReadAsStringAsync(deadline.Token), StringComparison.Ordinal);
            using (var removed = await SendAsync(http, HttpMethod.Delete, "/api/v2/admin/servers/east", origin, csrf, new { version = runtime.ConfigurationVersion }))
                Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.Empty(runtime.Snapshot);
            Assert.Empty(AgentProcessConfigurationLoader.Load(configurationPath).Servers);
            using (var reused = await SendAsync(http, HttpMethod.Post, "/api/v2/admin/servers", origin, csrf, new { version = runtime.ConfigurationVersion, server = profile, confirmExistingState = false }))
            {
                Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
                Assert.DoesNotContain(profileSecret, await reused.Content.ReadAsStringAsync(deadline.Token), StringComparison.Ordinal);
            }
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dashboard.StopAsync(deadline.Token);
            await runtime.StopAsync(deadline.Token);
            directory.Delete(recursive: true);
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string url, string origin, string? csrf, object body)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        if (csrf is not null) request.Headers.TryAddWithoutValidation("X-RelayLink-CSRF", csrf);
        return http.SendAsync(request);
    }

    [Fact]
    public async Task V2_api_groups_offline_profiles_and_v1_rejects_ambiguous_state()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-multi-api-");
        var port = GetFreePort();
        var reconnect = new ReconnectConfiguration(1, 2, 3);
        var configuration = new AgentProcessConfiguration
        {
            DashboardPort = port,
            Servers =
            [
                new AgentServerProfile { ProfileId = "east", ServerHost = "127.0.0.1", ServerPort = GetFreePort(), DataPort = GetFreePort(), ClientId = "same", Secret = Convert.ToBase64String(new byte[32]), Reconnect = reconnect },
                new AgentServerProfile { ProfileId = "west", ServerHost = "127.0.0.1", ServerPort = GetFreePort(), DataPort = GetFreePort(), ClientId = "same", Secret = Convert.ToBase64String(new byte[32]), Reconnect = reconnect }
            ]
        };
        var configurationPath = Path.Combine(directory.FullName, "agent.json");
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(configuration, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        var runtime = new AgentProcessRuntime(configuration, new AgentConfigurationPath(configurationPath), NullLoggerFactory.Instance);
        using var dashboard = new AgentLocalDashboard(configuration, runtime);
        try
        {
            await runtime.StartAsync(CancellationToken.None);
            await dashboard.StartAsync(CancellationToken.None);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            HttpResponseMessage response;
            do
            {
                try { response = await http.GetAsync("/api/v2/status", deadline.Token); }
                catch (HttpRequestException) when (!deadline.IsCancellationRequested)
                {
                    await Task.Delay(50, deadline.Token);
                    continue;
                }
                break;
            } while (true);
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var json = await response.Content.ReadAsStringAsync(deadline.Token);
                Assert.DoesNotContain(configuration.Servers[0].Secret, json, StringComparison.Ordinal);
                using var body = JsonDocument.Parse(json);
                var groups = body.RootElement.GetProperty("servers").EnumerateArray().ToArray();
                Assert.Equal(["east", "west"], groups.Select(group => group.GetProperty("profileId").GetString()));
                Assert.All(groups, group =>
                {
                    Assert.False(group.GetProperty("online").GetBoolean());
                    Assert.Equal("127.0.0.1", group.GetProperty("serverHost").GetString());
                    Assert.False(group.GetProperty("useTls").GetBoolean());
                    Assert.Empty(group.GetProperty("channels").EnumerateArray());
                    Assert.Empty(group.GetProperty("mappings").EnumerateArray());
                });
            }
            using var old = await http.GetAsync("/api/v1/status", deadline.Token);
            Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
            Assert.True(File.Exists(Path.Combine(directory.FullName, "state", "east", "identity.pfx")));
            Assert.True(File.Exists(Path.Combine(directory.FullName, "state", "west", "identity.pfx")));
            Assert.NotEqual(File.ReadAllBytes(Path.Combine(directory.FullName, "state", "east", "identity.pfx")),
                File.ReadAllBytes(Path.Combine(directory.FullName, "state", "west", "identity.pfx")));
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dashboard.StopAsync(deadline.Token);
            await runtime.StopAsync(deadline.Token);
            directory.Delete(recursive: true);
        }
    }

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
            Assert.Contains("style-src 'self'", channelsResponse.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
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
            Assert.Equal(HttpStatusCode.Forbidden, writeAttempt.StatusCode);

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
