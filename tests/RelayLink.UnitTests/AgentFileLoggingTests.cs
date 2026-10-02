using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayLink.Agent;
using Serilog;
using RelayLink.Logging;

namespace RelayLink.UnitTests;

public sealed class AgentFileLoggingTests
{
    [Theory]
    [InlineData("agent")]
    [InlineData("server")]
    public void Routes_levels_and_preserves_profile_scope_and_exceptions(string component)
    {
        using var files = new LogFiles(component);
        using (var sink = FileLogging.Create(files.DirectoryPath, component))
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, dispose: false)))
        {
            var logger = factory.CreateLogger("RelayLink.Agent.Test");
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["profileId"] = "east", ["clientId"] = "test-agent" });
            logger.LogDebug("debug-hidden");
            logger.LogInformation("info-record");
            logger.LogWarning("warning-record");
            logger.LogError(new IOException("test-exception"), "error-record");
            logger.LogCritical("critical-record");
        }

        var ordinary = files.Read(files.OrdinaryPattern);
        var errors = files.Read(files.ErrorPattern);
        Assert.Contains("info-record", ordinary);
        Assert.Contains("warning-record", ordinary);
        Assert.DoesNotContain("error-record", ordinary);
        Assert.DoesNotContain("debug-hidden", ordinary);
        Assert.Contains("error-record", errors);
        Assert.Contains("critical-record", errors);
        Assert.Contains("test-exception", errors);
        Assert.DoesNotContain("info-record", errors);
        foreach (var line in (ordinary + errors).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var record = JsonDocument.Parse(line);
            var properties = record.RootElement.GetProperty("Properties");
            Assert.Equal("east", properties.GetProperty("profileId").GetString());
            Assert.Equal("test-agent", properties.GetProperty("clientId").GetString());
        }
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("server")]
    public void Deletes_expired_ordinary_files_but_keeps_old_errors_and_recent_files(string component)
    {
        using var files = new LogFiles(component);
        var oldDate = DateTime.Now.AddDays(-60).ToString("yyyyMMdd");
        var recentDate = DateTime.Now.AddDays(-1).ToString("yyyyMMdd");
        var oldOrdinary = files.Seed($"{component}-{oldDate}.jsonl");
        var oldError = files.Seed($"{files.ErrorPrefix}-{oldDate}.jsonl");
        var recentOrdinary = files.Seed($"{component}-{recentDate}.jsonl");
        using (var logger = FileLogging.Create(files.DirectoryPath, component))
        {
            logger.Information("retention-trigger");
            logger.Error("error-retention-trigger");
        }
        Assert.False(File.Exists(oldOrdinary));
        Assert.True(File.Exists(oldError));
        Assert.True(File.Exists(recentOrdinary));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("server")]
    public void Size_rotation_keeps_more_than_31_error_files_and_appends_after_restart(string component)
    {
        using var files = new LogFiles(component);
        for (var run = 0; run < 2; run++)
        {
            using var logger = FileLogging.Create(files.DirectoryPath, component, fileSizeLimitBytes: 256);
            for (var index = 0; index < 40; index++)
            {
                logger.Information("ordinary-{Run}-{Index} {Padding}", run, index, new string('x', 256));
                logger.Error("failure-{Run}-{Index} {Padding}", run, index, new string('x', 256));
            }
        }
        Assert.True(files.Paths(files.OrdinaryPattern).Length > 31);
        Assert.True(files.Paths(files.ErrorPattern).Length > 31);
        Assert.Equal(80, files.Read(files.OrdinaryPattern).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(80, files.Read(files.ErrorPattern).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private sealed class LogFiles(string component) : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "RelayLink-log-tests-" + Guid.NewGuid().ToString("N"));
        public string ErrorPrefix => component == "agent" ? "error" : "server-error";
        public string OrdinaryPattern => component + "-????????*.jsonl";
        public string ErrorPattern => ErrorPrefix + "-*.jsonl";
        public string DirectoryPath => Path.Combine(root, "logs");
        public string[] Paths(string pattern) => Directory.GetFiles(DirectoryPath, pattern)
            .Where(path => pattern != OrdinaryPattern || char.IsDigit(Path.GetFileName(path)[component.Length + 1])).ToArray();
        public string Read(string pattern) => string.Concat(Paths(pattern).Select(File.ReadAllText));
        public string Seed(string name)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, name);
            File.WriteAllText(path, "seed\n");
            return path;
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
