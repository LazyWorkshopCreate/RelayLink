using System.Text.Json;
using RelayLink.Agent;

namespace RelayLink.UnitTests;

public sealed class AgentDiagnosticLogTests
{
    [Fact]
    public async Task Writes_connection_metadata_and_rotates_bounded_file()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relaylink-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new AgentConfiguration("127.0.0.1", 7443, "agent", "secret", false, null,
                new ReconnectConfiguration(1, 2, 3))
            { E2eIdentityPath = System.IO.Path.Combine(directory, "agent.e2e.pfx") };
            var log = new AgentDiagnosticLog(configuration);
            var connectionId = Guid.NewGuid();
            await log.WriteAsync(connectionId, "rdp", "relay_progress", 12, 34, 56, 78, 90);

            using (var entry = JsonDocument.Parse(await File.ReadAllTextAsync(log.FilePath)))
            {
                Assert.Equal(connectionId, entry.RootElement.GetProperty("connectionId").GetGuid());
                Assert.Equal(12, entry.RootElement.GetProperty("toServerBytes").GetInt64());
                Assert.Equal(34, entry.RootElement.GetProperty("toTargetBytes").GetInt64());
            }

            await File.WriteAllBytesAsync(log.FilePath, new byte[4 * 1024 * 1024]);
            await log.WriteAsync(connectionId, "rdp", "completed");
            Assert.True(File.Exists(log.FilePath + ".1"));
            Assert.Contains("completed", await File.ReadAllTextAsync(log.FilePath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
