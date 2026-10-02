using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Agent;

namespace RelayLink.UnitTests;

public sealed class AgentProcessRuntimeTests
{
    [Fact]
    public async Task Failed_configuration_commit_keeps_runtime_and_allows_retry_without_state_confirmation()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-runtime-fault-");
        var path = Path.Combine(directory.FullName, "agent.json");
        var initial = new AgentProcessConfiguration { DashboardPort = 0, Servers = [Profile("east")] };
        File.WriteAllText(path, JsonSerializer.Serialize(initial, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var runtime = new AgentProcessRuntime(initial, new AgentConfigurationPath(path), NullLoggerFactory.Instance);
        try
        {
            await runtime.StartAsync(CancellationToken.None);
            var original = File.ReadAllBytes(path);
            var version = runtime.ConfigurationVersion;
            runtime.BeforeConfigurationCommit = () => throw new IOException("Injected commit failure.");
            await Assert.ThrowsAsync<IOException>(() => runtime.AddAsync(Profile("west"), version, false, CancellationToken.None));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(version, runtime.ConfigurationVersion);
            Assert.Equal("east", Assert.Single(runtime.Snapshot).ProfileId);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "state", "west", "identity.pfx")));

            runtime.BeforeConfigurationCommit = null;
            version = await runtime.AddAsync(Profile("west"), version, false, CancellationToken.None);
            Assert.Equal(2, runtime.Snapshot.Count);
            var beforeRemove = File.ReadAllBytes(path);
            runtime.BeforeConfigurationCommit = () => throw new IOException("Injected commit failure.");
            await Assert.ThrowsAsync<IOException>(() => runtime.RemoveAsync("east", version, CancellationToken.None));
            Assert.Equal(beforeRemove, File.ReadAllBytes(path));
            Assert.Equal(["east", "west"], runtime.Snapshot.Select(item => item.ProfileId));
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_writes_with_one_version_commit_only_one_change()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-runtime-race-");
        var path = Path.Combine(directory.FullName, "agent.json");
        var initial = new AgentProcessConfiguration { DashboardPort = 0, Servers = [] };
        File.WriteAllText(path, JsonSerializer.Serialize(initial, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var runtime = new AgentProcessRuntime(initial, new AgentConfigurationPath(path), NullLoggerFactory.Instance);
        try
        {
            await runtime.StartAsync(CancellationToken.None);
            var version = runtime.ConfigurationVersion;
            var attempts = await Task.WhenAll(new[] { Profile("east"), Profile("west") }.Select(async profile =>
            {
                try { await runtime.AddAsync(profile, version, false, CancellationToken.None); return true; }
                catch (AgentProfileMutationException exception) when (exception.Reason == AgentProfileMutationError.Conflict) { return false; }
            }));
            Assert.Equal(1, attempts.Count(success => success));
            Assert.Equal(Assert.Single(runtime.Snapshot).ProfileId,
                Assert.Single(AgentProcessConfigurationLoader.Load(path).Servers).ProfileId);
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Imported_update_preserves_identity_and_other_profile_and_failed_commit_keeps_old_runtime()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-runtime-update-");
        var path = Path.Combine(directory.FullName, "agent.json");
        var initial = new AgentProcessConfiguration { DashboardPort = 0, Servers = [Profile("east"), Profile("west")] };
        File.WriteAllText(path, JsonSerializer.Serialize(initial, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var runtime = new AgentProcessRuntime(initial, new AgentConfigurationPath(path), NullLoggerFactory.Instance);
        try
        {
            await runtime.StartAsync(CancellationToken.None);
            var identity = File.ReadAllBytes(Path.Combine(directory.FullName, "state", "east", "identity.pfx"));
            var before = File.ReadAllBytes(path);
            var version = runtime.ConfigurationVersion;
            var imported = initial.Servers[0] with { ServerHost = "updated.example", Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            runtime.BeforeConfigurationCommit = () => throw new IOException("Injected commit failure.");
            await Assert.ThrowsAsync<IOException>(() => runtime.UpdateAsync("east", imported, version, CancellationToken.None));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(version, runtime.ConfigurationVersion);
            Assert.Equal("127.0.0.1", runtime.Snapshot[0].ServerHost);
            runtime.BeforeConfigurationCommit = null;
            version = await runtime.UpdateAsync("east", imported, version, CancellationToken.None);
            Assert.Equal("updated.example", runtime.Snapshot[0].ServerHost);
            Assert.Equal("127.0.0.1", runtime.Snapshot[1].ServerHost);
            Assert.Equal(identity, File.ReadAllBytes(Path.Combine(directory.FullName, "state", "east", "identity.pfx")));
            Assert.Equal(imported.Secret, AgentProcessConfigurationLoader.Load(path).Servers[0].Secret);
            await Assert.ThrowsAsync<AgentProfileMutationException>(() => runtime.UpdateAsync("east", imported with { ClientId = "another" }, version, CancellationToken.None));
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Disable_persists_across_restart_and_imported_update_does_not_reenable()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-runtime-disabled-");
        var path = Path.Combine(directory.FullName, "agent.json");
        var initial = new AgentProcessConfiguration { DashboardPort = 0, Servers = [Profile("east"), Profile("west")] };
        File.WriteAllText(path, JsonSerializer.Serialize(initial, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var runtime = new AgentProcessRuntime(initial, new AgentConfigurationPath(path), NullLoggerFactory.Instance);
        try
        {
            await runtime.StartAsync(CancellationToken.None);
            var identityPath = Path.Combine(directory.FullName, "state", "east", "identity.pfx");
            var identity = File.ReadAllBytes(identityPath);
            var original = File.ReadAllBytes(path);
            var version = runtime.ConfigurationVersion;
            runtime.BeforeConfigurationCommit = () => throw new IOException("Injected commit failure.");
            await Assert.ThrowsAsync<IOException>(() => runtime.SetEnabledAsync("east", false, version, CancellationToken.None));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(runtime.Snapshot[0].Enabled);

            runtime.BeforeConfigurationCommit = null;
            version = await runtime.SetEnabledAsync("east", false, version, CancellationToken.None);
            Assert.False(runtime.Snapshot[0].Enabled);
            Assert.False(runtime.Snapshot[0].Status.Online);
            Assert.True(runtime.Snapshot[1].Enabled);
            Assert.False(AgentProcessConfigurationLoader.Load(path).Servers[0].Enabled);
            Assert.Equal(identity, File.ReadAllBytes(identityPath));

            var imported = initial.Servers[0] with { ServerHost = "updated.example", Enabled = true };
            version = await runtime.UpdateAsync("east", imported, version, CancellationToken.None);
            Assert.False(runtime.Snapshot[0].Enabled);
            Assert.Equal("updated.example", runtime.Snapshot[0].ServerHost);
            Assert.False(AgentProcessConfigurationLoader.Load(path).Servers[0].Enabled);
            await Assert.ThrowsAsync<AgentProfileMutationException>(() => runtime.SetEnabledAsync("east", false, version, CancellationToken.None));
            await runtime.StopAsync(CancellationToken.None);

            var reloaded = AgentProcessConfigurationLoader.Load(path);
            var restarted = new AgentProcessRuntime(reloaded, new AgentConfigurationPath(path), NullLoggerFactory.Instance);
            try
            {
                await restarted.StartAsync(CancellationToken.None);
                Assert.False(restarted.Snapshot[0].Enabled);
                Assert.False(restarted.Snapshot[0].Status.Online);
                Assert.Equal(identity, File.ReadAllBytes(identityPath));
                await restarted.SetEnabledAsync("east", true, restarted.ConfigurationVersion, CancellationToken.None);
                Assert.True(restarted.Snapshot[0].Enabled);
                Assert.True(AgentProcessConfigurationLoader.Load(path).Servers[0].Enabled);
                Assert.Equal(identity, File.ReadAllBytes(identityPath));
            }
            finally { await restarted.StopAsync(CancellationToken.None); }
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Add_remove_last_profile_and_restart_preserve_other_contexts()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-runtime-");
        var path = Path.Combine(directory.FullName, "agent.json");
        var original = new AgentProcessConfiguration { DashboardPort = 0, Servers = [Profile("east")] };
        File.WriteAllText(path, JsonSerializer.Serialize(original, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }));
        var runtime = new AgentProcessRuntime(original, new AgentConfigurationPath(path), NullLoggerFactory.Instance);
        try
        {
            await runtime.StartAsync(CancellationToken.None);
            var initialVersion = runtime.ConfigurationVersion;
            var eastIdentity = File.ReadAllBytes(Path.Combine(directory.FullName, "state", "east", "identity.pfx"));

            var addedVersion = await runtime.AddAsync(Profile("west"), initialVersion, false, CancellationToken.None);

            Assert.Equal(["east", "west"], runtime.Snapshot.Select(server => server.ProfileId));
            Assert.Equal(2, AgentProcessConfigurationLoader.Load(path).Servers.Count);
            Assert.Throws<AgentProfileMutationException>(() => runtime.AddAsync(Profile("north"), initialVersion, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(eastIdentity, File.ReadAllBytes(Path.Combine(directory.FullName, "state", "east", "identity.pfx")));

            var removedVersion = await runtime.RemoveAsync("east", addedVersion, CancellationToken.None);
            Assert.Equal("west", Assert.Single(runtime.Snapshot).ProfileId);
            Assert.True(File.Exists(Path.Combine(directory.FullName, "state", "east", "identity.pfx")));
            await runtime.RemoveAsync("west", removedVersion, CancellationToken.None);
            Assert.Empty(runtime.Snapshot);
            Assert.Empty(AgentProcessConfigurationLoader.Load(path).Servers);
            await runtime.StopAsync(CancellationToken.None);

            var reloaded = AgentProcessConfigurationLoader.Load(path);
            var restarted = new AgentProcessRuntime(reloaded, new AgentConfigurationPath(path), NullLoggerFactory.Instance);
            await restarted.StartAsync(CancellationToken.None);
            Assert.Empty(restarted.Snapshot);
            await restarted.StopAsync(CancellationToken.None);
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    private static AgentServerProfile Profile(string profileId) => new()
    {
        ProfileId = profileId, ServerHost = "127.0.0.1", ServerPort = 7443, DataPort = 7444,
        ClientId = "same", Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        Reconnect = new ReconnectConfiguration(1, 2, 3)
    };
}
