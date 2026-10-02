using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayLink.Agent;

public sealed record AgentServerRuntimeSnapshot(string ProfileId, string ServerHost, bool UseTls, bool Enabled, AgentStatusSnapshot Status,
    int PendingConnections = 0, int ActiveConnections = 0, long CapacityRejected = 0);

public sealed class AgentProcessRuntime(AgentProcessConfiguration configuration, AgentConfigurationPath path, ILoggerFactory loggerFactory) : IHostedService
{
    private readonly Dictionary<string, Context> contexts = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private AgentProcessConfiguration current = configuration;
    private string version = ReadVersion(path.Value);
    private bool stopping;
    internal Action? BeforeConfigurationCommit { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public string ConfigurationVersion { get { lock (gate) return version; } }

    public IReadOnlyList<AgentServerRuntimeSnapshot> Snapshot
    {
        get
        {
            lock (gate)
                return contexts.Values.Select(context =>
                {
                    var usage = context.Quota.Snapshot;
                    return new AgentServerRuntimeSnapshot(context.Configuration.ProfileId, context.Configuration.ServerHost,
                        context.Configuration.UseTls, context.Enabled, context.Status.Snapshot, usage.Pending, usage.Active, usage.Rejected);
                }).ToArray();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var profile in current.Servers)
            {
                var context = CreateContext(profile);
                lock (gate) contexts.Add(profile.ProfileId, context);
                if (profile.Enabled) await context.Worker.StartAsync(cancellationToken);
            }
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        Context[] stopping;
        try
        {
            lock (gate)
            {
                this.stopping = true;
                stopping = contexts.Values.ToArray();
                contexts.Clear();
            }
            await Task.WhenAll(stopping.Select(async context =>
            {
                try { await context.Worker.StopAsync(cancellationToken); }
                finally { context.Worker.Dispose(); }
            }));
        }
        finally { mutationGate.Release(); }
    }

    public async Task<string> AddAsync(AgentServerProfile profile, string expectedVersion, bool confirmExistingState, CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureVersion(expectedVersion);
            if (current.Servers.Any(server => server.ProfileId == profile.ProfileId))
                throw new AgentProfileMutationException(AgentProfileMutationError.Duplicate, "This profileId already exists.");
            var next = current with { Servers = [.. current.Servers, profile] };
            AgentProcessConfigurationLoader.Validate(next);
            var stateDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path.Value))!, "state", profile.ProfileId);
            if (!confirmExistingState && Directory.Exists(stateDirectory) && Directory.EnumerateFileSystemEntries(stateDirectory).Any())
                throw new AgentProfileMutationException(AgentProfileMutationError.ConfirmationRequired, "Protected state already exists for this profileId; confirm it belongs to the same server identity.");
            var identityPath = Path.Combine(stateDirectory, "identity.pfx");
            var identityExisted = File.Exists(identityPath);
            Context? context = null;
            string newVersion;
            try
            {
                context = CreateContext(profile);
                newVersion = Persist(next);
            }
            catch
            {
                context?.Worker.Dispose();
                if (!identityExisted && File.Exists(identityPath)) File.Delete(identityPath);
                throw;
            }
            lock (gate)
            {
                current = next;
                version = newVersion;
                contexts.Add(profile.ProfileId, context);
            }
            if (profile.Enabled) await context.Worker.StartAsync(CancellationToken.None);
            return newVersion;
        }
        finally { mutationGate.Release(); }
    }

    public async Task<string> RemoveAsync(string profileId, string expectedVersion, CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureVersion(expectedVersion);
            if (!contexts.TryGetValue(profileId, out var context))
                throw new AgentProfileMutationException(AgentProfileMutationError.Missing, "This profileId does not exist.");
            var next = current with { Servers = current.Servers.Where(server => server.ProfileId != profileId).ToArray() };
            AgentProcessConfigurationLoader.Validate(next);
            var newVersion = Persist(next);
            lock (gate)
            {
                current = next;
                version = newVersion;
            }
            try { await context.Worker.StopAsync(CancellationToken.None); }
            finally
            {
                context.Worker.Dispose();
                lock (gate) contexts.Remove(profileId);
            }
            return newVersion;
        }
        finally { mutationGate.Release(); }
    }

    public async Task<string> UpdateAsync(string profileId, AgentServerProfile profile, string expectedVersion, CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureVersion(expectedVersion);
            if (!contexts.TryGetValue(profileId, out var previous))
                throw new AgentProfileMutationException(AgentProfileMutationError.Missing, "This profileId does not exist.");
            if (profile.ProfileId != profileId || profile.ClientId != previous.Configuration.ClientId)
                throw new AgentProfileMutationException(AgentProfileMutationError.IdentityMismatch, "Imported configuration must keep the existing profileId and clientId.");
            profile = profile with { Enabled = previous.Enabled };
            var next = current with { Servers = current.Servers.Select(server => server.ProfileId == profileId ? profile : server).ToArray() };
            AgentProcessConfigurationLoader.Validate(next);
            var replacement = CreateContext(profile);
            string newVersion;
            try { newVersion = Persist(next); }
            catch
            {
                replacement.Worker.Dispose();
                throw;
            }
            lock (gate)
            {
                current = next;
                version = newVersion;
                contexts[profileId] = replacement;
            }
            try { await previous.Worker.StopAsync(CancellationToken.None); }
            finally { previous.Worker.Dispose(); }
            if (profile.Enabled) await replacement.Worker.StartAsync(CancellationToken.None);
            return newVersion;
        }
        finally { mutationGate.Release(); }
    }

    public async Task<string> SetEnabledAsync(string profileId, bool enabled, string expectedVersion, CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureVersion(expectedVersion);
            if (!contexts.TryGetValue(profileId, out var previous))
                throw new AgentProfileMutationException(AgentProfileMutationError.Missing, "This profileId does not exist.");
            if (previous.Enabled == enabled)
                throw new AgentProfileMutationException(AgentProfileMutationError.Conflict, "This profile is already in the requested state; refresh the page.");
            var next = current with
            {
                Servers = current.Servers.Select(server => server.ProfileId == profileId ? server with { Enabled = enabled } : server).ToArray()
            };
            AgentProcessConfigurationLoader.Validate(next);
            var replacement = CreateContext(next.Servers.Single(server => server.ProfileId == profileId));
            string newVersion;
            try { newVersion = Persist(next); }
            catch
            {
                replacement.Worker.Dispose();
                throw;
            }
            lock (gate)
            {
                current = next;
                version = newVersion;
                contexts[profileId] = replacement;
            }
            try { await previous.Worker.StopAsync(CancellationToken.None); }
            finally { previous.Worker.Dispose(); }
            if (enabled) await replacement.Worker.StartAsync(CancellationToken.None);
            return newVersion;
        }
        finally { mutationGate.Release(); }
    }

    private void EnsureVersion(string expectedVersion)
    {
        if (stopping || !string.Equals(expectedVersion, version, StringComparison.Ordinal) ||
            !string.Equals(ReadVersion(path.Value), version, StringComparison.Ordinal))
            throw new AgentProfileMutationException(AgentProfileMutationError.Conflict, "Agent configuration changed; reload the page or restart after an external edit.");
    }

    private string Persist(AgentProcessConfiguration next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions);
        using (var document = JsonDocument.Parse(bytes)) AgentProcessConfigurationLoader.Parse(document.RootElement);
        var fullPath = Path.GetFullPath(path.Value);
        var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = CreatePrivateFile(temporary))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            BeforeConfigurationCommit?.Invoke();
            File.Move(temporary, fullPath, overwrite: true);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows()) return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    private static string ReadVersion(string configurationPath) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configurationPath)));

    private Context CreateContext(AgentServerProfile profile)
    {
        var server = AgentProcessConfigurationLoader.CreateRuntimeConfiguration(current, profile, path.Value);
        var stateDirectory = Path.GetDirectoryName(server.E2eIdentityPath)!;
        var stateRoot = Path.GetDirectoryName(stateDirectory)!;
        Directory.CreateDirectory(stateDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var status = new AgentStatus(server);
        var diagnostics = new AgentDiagnosticLog(server);
        var quota = new AgentConnectionQuota(profile.MaxConnections, profile.MaxPendingConnections);
        var worker = new ControlSessionWorker(server, status, diagnostics, quota, loggerFactory.CreateLogger<ControlSessionWorker>());
        return new Context(server, profile.Enabled, status, quota, worker);
    }

    private sealed record Context(AgentConfiguration Configuration, bool Enabled, AgentStatus Status, AgentConnectionQuota Quota, ControlSessionWorker Worker);
}

public sealed record AgentConfigurationPath(string Value);

public enum AgentProfileMutationError { Conflict, Duplicate, Missing, ConfirmationRequired, IdentityMismatch }
public sealed class AgentProfileMutationException(AgentProfileMutationError reason, string message) : Exception(message)
{
    public AgentProfileMutationError Reason { get; } = reason;
}
