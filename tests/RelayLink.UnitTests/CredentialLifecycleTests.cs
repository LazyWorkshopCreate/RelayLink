using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class CredentialLifecycleTests
{
    [Fact]
    public async Task Missing_key_does_not_abort_service_initialization_or_replace_existing_store()
    {
        using var workspace = new Workspace();
        await workspace.Store.SaveAsync("target", "ssh", "login", "user", "target", "password", "test-secret"u8.ToArray(), default);
        var path = Path.Combine(CredentialStore.DirectoryFor(workspace.ConfigurationPath), "store");
        var original = File.ReadAllBytes(path);
        File.Delete(Path.Combine(CredentialStore.DirectoryFor(workspace.ConfigurationPath), "key"));
        var unavailable = CredentialStore.OpenForService(workspace.ConfigurationPath);
        await Assert.ThrowsAsync<CredentialStoreException>(() => unavailable.CheckAvailableAsync(default));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(CredentialStore.DirectoryFor(workspace.ConfigurationPath), "key")));
    }

    [Fact]
    public async Task A_disappearing_store_is_not_interpreted_as_an_empty_store()
    {
        using var workspace = new Workspace();
        await workspace.Store.SaveAsync("target", "ssh", "login", "user", "target", "password", "test-secret"u8.ToArray(), default);
        File.Delete(Path.Combine(CredentialStore.DirectoryFor(workspace.ConfigurationPath), "store"));
        await Assert.ThrowsAsync<CredentialStoreException>(() => workspace.Store.ListAsync("target", "ssh", default));
        await Assert.ThrowsAsync<CredentialStoreException>(() => workspace.Store.SaveAsync("target", "ssh", "new", "user", "target", "password", "test"u8.ToArray(), default));
        var restarted = CredentialStore.OpenForService(workspace.ConfigurationPath);
        await Assert.ThrowsAsync<CredentialStoreException>(() => restarted.CheckAvailableAsync(default));
        Assert.False(File.Exists(Path.Combine(CredentialStore.DirectoryFor(workspace.ConfigurationPath), "store")));
    }

    [Fact]
    public async Task Audit_failure_before_mutation_leaves_credentials_unchanged()
    {
        using var workspace = new Workspace();
        var semaphore = Capacity(workspace.Audit);
        for (var i = 0; i < 1024; i++) Assert.True(semaphore.Wait(0));
        try
        {
            await Assert.ThrowsAsync<AuditUnavailableException>(() => workspace.Mutations.ApplyAsync(Guid.NewGuid(), "target", "ssh", "login",
                new("user", "target", "password", null), "test-secret"u8.ToArray(), default));
            Assert.Empty(await workspace.Store.ListAsync("target", "ssh", default));
        }
        finally { semaphore.Release(1024); }
    }

    [Fact]
    public async Task Applied_operation_survives_lost_result_audit_restart_and_idempotent_retry()
    {
        using var workspace = new Workspace();
        await workspace.Audit.StartAsync(default);
        var id = Guid.NewGuid();
        var request = new CredentialWriteRequest("user", "target", "password", null);
        var secret = "test-secret"u8.ToArray();
        var hash = CredentialStore.RequestHash("target", "ssh", "login", "save", request, secret);
        // Simulate process termination between atomic store replacement and result audit.
        await workspace.Store.ApplyOperationAsync(id, hash, "target", "ssh", "login", request, secret, default);
        var reopened = CredentialStore.Open(workspace.ConfigurationPath);
        var mutations = new CredentialMutationService(reopened, workspace.Audit, NullLogger<CredentialMutationService>.Instance);
        var semaphore = Capacity(workspace.Audit);
        for (var i = 0; i < 1024; i++) Assert.True(semaphore.Wait(0));
        try
        {
            var result = await mutations.ApplyAsync(id, "target", "ssh", "login", request, secret, default);
            Assert.True(result.Operation.Applied);
            Assert.True(result.AuditPending);
        }
        finally { semaphore.Release(1024); }
        var recorded = await mutations.ApplyAsync(id, "target", "ssh", "login", request, secret, default);
        Assert.False(recorded.AuditPending);
        Assert.False((await mutations.ApplyAsync(id, "target", "ssh", "login", request, secret, default)).AuditPending);
        var page = await workspace.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), "credential_saved", null, 1, 10, default);
        Assert.Equal(1, page.Total);
        await Assert.ThrowsAsync<CredentialStoreException>(() => mutations.ApplyAsync(id, "target", "ssh", "login", request, "different"u8.ToArray(), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_deletion_blocks_reuse_and_cleanup_removes_credentials_and_host_keys(bool wholeClient)
    {
        using var workspace = new Workspace();
        await workspace.Store.SaveAsync("target", "ssh", "login", "user", "target", "password", "test-secret"u8.ToArray(), default);
        var original = await workspace.Store.GetOrCreateHostKeyAsync("target", "ssh", () => [1, 2, 3], default);
        await workspace.Store.BeginDeletionAsync("target", wholeClient ? null : "ssh", default);
        var reopened = CredentialStore.Open(workspace.ConfigurationPath);
        await Assert.ThrowsAsync<CredentialStoreException>(() => reopened.ListAsync("target", "ssh", default));
        await Assert.ThrowsAsync<CredentialStoreException>(() => reopened.GetOrCreateHostKeyAsync("target", "ssh", () => [4, 5, 6], default));
        await reopened.CompleteDeletionAsync("target", wholeClient ? null : "ssh", default);
        Assert.Empty(await reopened.ListAsync("target", "ssh", default));
        Assert.NotEqual(original, await reopened.GetOrCreateHostKeyAsync("target", "ssh", () => [4, 5, 6], default));
        Assert.Empty(await reopened.PendingDeletionsAsync(default));
    }

    [Fact]
    public async Task Repeated_delete_reports_the_original_applied_result()
    {
        using var workspace = new Workspace();
        await workspace.Audit.StartAsync(default);
        await workspace.Store.SaveAsync("target", "ssh", "login", "user", "target", "password", "test-secret"u8.ToArray(), default);
        var id = Guid.NewGuid();
        var first = await workspace.Mutations.ApplyAsync(id, "target", "ssh", "login", null, ReadOnlyMemory<byte>.Empty, default);
        var retry = await workspace.Mutations.ApplyAsync(id, "target", "ssh", "login", null, ReadOnlyMemory<byte>.Empty, default);
        Assert.True(first.Operation.Applied);
        Assert.True(retry.Operation.Applied);
        var page = await workspace.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), "credential_deleted", null, 1, 10, default);
        Assert.Equal(1, page.Total);
    }

    private static SemaphoreSlim Capacity(AuditService audit) => (SemaphoreSlim)typeof(AuditService).GetField("capacity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(audit)!;

    [Fact]
    public async Task Reused_id_after_receipt_expiry_gets_a_new_audit_identity()
    {
        using var workspace = new Workspace();
        await workspace.Audit.StartAsync(default);
        var id = Guid.NewGuid();
        var request = new CredentialWriteRequest("user", "target", "password", null);
        var original = await workspace.Mutations.ApplyAsync(id, "target", "ssh", "login", request, "first"u8.ToArray(), default);
        // Age only receipt metadata; retain encryption and actual persistence code.
        var read = typeof(CredentialStore).GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var document = read.Invoke(workspace.Store, null)!;
        var operations = (List<CredentialOperation>)document.GetType().GetProperty("Operations")!.GetValue(document)!;
        operations[0] = operations[0] with { CreatedUtc = DateTimeOffset.UtcNow.AddHours(-25) };
        await (Task)typeof(CredentialStore).GetMethod("WriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace.Store, [document, CancellationToken.None])!;
        await workspace.Mutations.ApplyAsync(Guid.NewGuid(), "target", "ssh", "another", request, "trigger-prune"u8.ToArray(), default);
        var reused = await workspace.Mutations.ApplyAsync(id, "target", "ssh", "login", request, "second"u8.ToArray(), default);
        Assert.NotEqual(original.Operation.AuditId, reused.Operation.AuditId);
        var page = await workspace.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), "credential_saved", null, 1, 10, default);
        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task Failed_atomic_replacement_records_no_success_and_same_id_can_retry()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows share modes provide a deterministic replace denial.
        using var workspace = new Workspace();
        await workspace.Audit.StartAsync(default);
        await workspace.Store.SaveAsync("target", "ssh", "login", "original", "target", "password", "original"u8.ToArray(), default);
        var storePath = Path.Combine(CredentialStore.DirectoryFor(workspace.ConfigurationPath), "store");
        var original = File.ReadAllBytes(storePath);
        var id = Guid.NewGuid();
        using (var locked = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => workspace.Mutations.ApplyAsync(id, "target", "ssh", "login", new("new", "target", "password", null), "new-secret"u8.ToArray(), default));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(original, File.ReadAllBytes(storePath));
            Assert.Equal("original", Assert.Single(await workspace.Store.ListAsync("target", "ssh", default)).Username);
        }
        var audit = await workspace.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), "credential_saved", null, 1, 10, default);
        Assert.Equal(0, audit.Total);
        var retried = await workspace.Mutations.ApplyAsync(id, "target", "ssh", "login", new("new", "target", "password", null), "new-secret"u8.ToArray(), default);
        Assert.True(retried.Operation.Applied);
        Assert.False(retried.AuditPending);
    }

    [Fact]
    public async Task Cleanup_failure_keeps_its_marker_and_a_backup_restore_finishes_without_old_keys()
    {
        using var workspace = new Workspace();
        await workspace.Store.SaveAsync("target", "ssh", "login", "user", "target", "password", "test-secret"u8.ToArray(), default);
        await workspace.Store.GetOrCreateHostKeyAsync("target", "ssh", () => [1, 2, 3], default);
        await workspace.Store.BeginDeletionAsync("target", "ssh", default);
        var directory = CredentialStore.DirectoryFor(workspace.ConfigurationPath);
        var key = File.ReadAllBytes(Path.Combine(directory, "key"));
        var store = File.ReadAllBytes(Path.Combine(directory, "store"));
        File.Delete(Path.Combine(directory, "key"));
        Assert.False(await workspace.Store.TryCompleteDeletionAsync("target", "ssh"));
        Assert.Equal(store, File.ReadAllBytes(Path.Combine(directory, "store")));
        await SecretFilePermissions.WriteTextAsync(Path.Combine(directory, "key"), System.Text.Encoding.UTF8.GetString(key), false, default);
        var restored = CredentialStore.Open(workspace.ConfigurationPath);
        Assert.Single(await restored.PendingDeletionsAsync(default));
        Assert.True(await restored.TryCompleteDeletionAsync("target", "ssh"));
        Assert.Empty(await restored.ListAsync("target", "ssh", default));
        Assert.Equal(new byte[] { 4, 5, 6 }, await restored.GetOrCreateHostKeyAsync("target", "ssh", () => [4, 5, 6], default));
        Assert.Empty(SecretFilePermissions.FindLoose([Path.Combine(directory, "key"), Path.Combine(directory, "store")]));
    }

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    public async Task Recovery_service_records_each_pending_result_audit_once_and_clears_the_backlog(string action)
    {
        using var workspace = new Workspace();
        await workspace.Audit.StartAsync(default);
        const string clientId = "target";
        const string channelId = "ssh";
        const string credentialId = "login";
        var request = new CredentialWriteRequest("user", "target", "password", null);
        var secret = "test-secret"u8.ToArray();
        if (action == "delete")
            await workspace.Store.SaveAsync(clientId, channelId, credentialId, request.Username, request.Purpose, request.Type, secret, default);
        CredentialWriteRequest? storedRequest = action == "save" ? request : null;
        ReadOnlyMemory<byte> storedSecret = action == "save" ? secret : ReadOnlyMemory<byte>.Empty;
        var id = Guid.NewGuid();
        var hash = CredentialStore.RequestHash(clientId, channelId, credentialId, action, storedRequest, storedSecret);
        // Simulate process termination between the atomic store replacement and the result audit.
        var pendingOperation = await workspace.Store.ApplyOperationAsync(id, hash, clientId, channelId, credentialId, storedRequest, storedSecret, default);
        var reopened = CredentialStore.Open(workspace.ConfigurationPath);
        Assert.Equal(pendingOperation.OperationId, Assert.Single(await reopened.PendingAuditsAsync(default)).OperationId);
        var mutations = new CredentialMutationService(reopened, workspace.Audit, NullLogger<CredentialMutationService>.Instance);
        using var recovery = new CredentialRecoveryService(reopened, mutations, new ConfigurationWriteLock(), workspace.Runtime,
            NullLogger<CredentialRecoveryService>.Instance);

        await recovery.StartAsync(default);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((await reopened.PendingAuditsAsync(default)).Count > 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "The recovery service did not reconcile the pending credential result audit.");
                await Task.Delay(50);
            }
        }
        finally { await recovery.StopAsync(CancellationToken.None); }

        Assert.Empty(await reopened.PendingAuditsAsync(default));
        var expected = action == "save" ? "credential_saved" : "credential_deleted";
        var page = await workspace.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), expected, null, 1, 10, default);
        var recorded = Assert.Single(page.Events);
        Assert.Equal("success", recorded.Outcome);
        Assert.Contains(id.ToString("N"), Assert.IsType<string>(recorded.ReasonCode), StringComparison.Ordinal);
        // Retrying reconciliation for the same receipt must not append a second result event.
        Assert.True(await mutations.TryRecordResultAsync(pendingOperation, default));
        Assert.Equal(1, (await workspace.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), expected, null, 1, 10, default)).Total);
        if (action == "save") Assert.Single(await reopened.ListAsync(clientId, channelId, default));
        else Assert.Empty(await reopened.ListAsync(clientId, channelId, default));
    }

    [Fact]
    public async Task Permission_denied_credential_store_fails_closed()
    {
        using var workspace = new Workspace();
        await workspace.Store.SaveAsync("target", "ssh", "login", "user", "target", "password", "test-secret"u8.ToArray(), default);
        var storePath = Path.Combine(CredentialStore.DirectoryFor(workspace.ConfigurationPath), "store");

        async Task AssertUnavailableAsync()
        {
            var denied = CredentialStore.OpenForService(workspace.ConfigurationPath);
            await Assert.ThrowsAsync<CredentialStoreException>(() => denied.CheckAvailableAsync(default));
            await Assert.ThrowsAsync<CredentialStoreException>(() => denied.ListAsync("target", "ssh", default));
        }

        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current Windows identity has no SID.");
            var file = new FileInfo(storePath);
            var deny = new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny);
            var security = file.GetAccessControl();
            security.AddAccessRule(deny);
            file.SetAccessControl(security);
            try
            {
                // If this identity can still read the denied file (for example an elevated token
                // using backup semantics), the denial is not observable and the body is skipped.
                if (!IsStoreReadDenied(storePath)) return;
                await AssertUnavailableAsync();
            }
            finally
            {
                security = file.GetAccessControl();
                security.RemoveAccessRule(deny);
                file.SetAccessControl(security);
            }
            Assert.Single(await CredentialStore.Open(workspace.ConfigurationPath).ListAsync("target", "ssh", default));
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var original = File.GetUnixFileMode(storePath);
            File.SetUnixFileMode(storePath, UnixFileMode.None);
            try
            {
                // A root process bypasses file mode bits; the scenario cannot be simulated there.
                if (!IsStoreReadDenied(storePath)) return;
                await AssertUnavailableAsync();
            }
            finally { File.SetUnixFileMode(storePath, original); }
            Assert.Single(await CredentialStore.Open(workspace.ConfigurationPath).ListAsync("target", "ssh", default));
            return;
        }

        throw new PlatformNotSupportedException("The credential store permission regression test requires a POSIX or Windows filesystem.");
    }

    private static bool IsStoreReadDenied(string path)
    {
        try { _ = File.ReadAllText(path); return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("relaylink-credential-lifecycle-").FullName;
        public string ConfigurationPath => Path.Combine(directory, "server.json");
        public CredentialStore Store { get; }
        public AuditService Audit { get; }
        public CredentialMutationService Mutations { get; }
        public ServerRuntime Runtime { get; }
        public Workspace()
        {
            Store = CredentialStore.Open(ConfigurationPath);
            var server = new ServerConfiguration(1, new("127.0.0.1", 7443, false, "", "", 5, 5, 15),
                new("127.0.0.1", 18080, 5, new("admin", "unused", 60)), "clients", new(10, 10, 10, 10, 5, 5, 5), null)
                { Audit = new(Path.Combine(directory, "audit", "audit.db")) };
            Runtime = new(new(server, new Dictionary<string, ClientConfiguration>()), new SessionRegistry());
            Audit = new(Runtime, NullLogger<AuditService>.Instance);
            Mutations = new(Store, Audit, NullLogger<CredentialMutationService>.Instance);
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
