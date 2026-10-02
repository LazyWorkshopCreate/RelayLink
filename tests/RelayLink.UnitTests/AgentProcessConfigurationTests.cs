using System.Security.Cryptography;
using System.Text.Json;
using RelayLink.Agent;

namespace RelayLink.UnitTests;

public sealed class AgentProcessConfigurationTests
{
    [Fact]
    public void Two_profiles_with_same_client_id_remain_distinct()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var document = JsonDocument.Parse("""
            {"maxConnections":200,"servers":[
              {"profileId":"east","serverHost":"east.example.com","serverPort":7443,"dataPort":7444,"clientId":"same","secret":"SECRET","maxConnections":100,"reconnect":{"initialDelaySeconds":1,"maxDelaySeconds":30,"permanentErrorDelaySeconds":60}},
              {"profileId":"west","serverHost":"west.example.com","serverPort":7443,"dataPort":7444,"clientId":"same","secret":"SECRET","maxConnections":100,"reconnect":{"initialDelaySeconds":1,"maxDelaySeconds":30,"permanentErrorDelaySeconds":60}}
            ]}
            """.Replace("SECRET", secret, StringComparison.Ordinal));

        var configuration = AgentProcessConfigurationLoader.Parse(document.RootElement);

        Assert.Equal(["east", "west"], configuration.Servers.Select(server => server.ProfileId));
        Assert.All(configuration.Servers, server => Assert.Equal("same", server.ClientId));
    }

    [Fact]
    public void Empty_servers_keep_dashboard_available()
    {
        using var document = JsonDocument.Parse("""{"servers":[]}""");
        Assert.Equal(18081, AgentProcessConfigurationLoader.Parse(document.RootElement).DashboardPort);
    }

    [Theory]
    [InlineData("""{"servers":[],"serverHost":"legacy.example.com"}""")]
    [InlineData("""{"servers":[],"servers":[]}""")]
    [InlineData("""{"servers":[{"profileId":"east","serverHost":"a","serverPort":7443,"dataPort":7444,"clientId":"agent","secret":"invalid","reconnect":{"initialDelaySeconds":1,"maxDelaySeconds":30,"permanentErrorDelaySeconds":60}}]}""")]
    [InlineData("""{"servers":[{"profileId":"east","serverHost":"a","serverPort":7443,"dataPort":7444,"clientId":"agent","secret":"invalid","channels":[],"reconnect":{"initialDelaySeconds":1,"maxDelaySeconds":30,"permanentErrorDelaySeconds":60}}]}""")]
    public void Invalid_or_legacy_fields_are_rejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<AgentConfigurationException>(() => AgentProcessConfigurationLoader.Parse(document.RootElement));
    }

    [Fact]
    public void Duplicate_profile_and_excess_quota_are_rejected()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string Profile(string id) => """{"profileId":"PROFILE","serverHost":"example.com","serverPort":7443,"dataPort":7444,"clientId":"agent","secret":"SECRET","maxConnections":100,"reconnect":{"initialDelaySeconds":1,"maxDelaySeconds":30,"permanentErrorDelaySeconds":60}}"""
            .Replace("PROFILE", id, StringComparison.Ordinal).Replace("SECRET", secret, StringComparison.Ordinal);
        using var duplicate = JsonDocument.Parse("{" + "\"servers\":[" + Profile("same") + "," + Profile("same") + "]}");
        using var excess = JsonDocument.Parse("{" + "\"maxConnections\":150,\"servers\":[" + Profile("east") + "," + Profile("west") + "]}");

        Assert.Throws<AgentConfigurationException>(() => AgentProcessConfigurationLoader.Parse(duplicate.RootElement));
        Assert.Throws<AgentConfigurationException>(() => AgentProcessConfigurationLoader.Parse(excess.RootElement));
    }
}
