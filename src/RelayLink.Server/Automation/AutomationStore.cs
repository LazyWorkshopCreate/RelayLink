using Microsoft.Data.Sqlite;
using RelayLink.Server.Runtime;

namespace RelayLink.Server.Automation;

public enum PairingState { Pending, Approved, Denied, Expired, Completed, Locked }

public sealed record PairingRequest(
    Guid RequestId, string Subject, string Platform, string CliVersion, string[] Scopes,
    string DeviceThumbprint, string DevicePublicKeyJwk, string UserCodeDigest, string UserCodeDisplay,
    PairingState State, int FailedAttempts, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? DecidedAtUtc, string? DecidedBy, int? LifetimeMinutes, string[]? ApprovedScopes,
    DateTimeOffset? TokenIssuedAtUtc, string? TokenHash, DateTimeOffset? TokenExpiresAtUtc, Guid? GrantId);

public sealed record AutomationGrant(
    Guid GrantId, string Subject, string Platform, string DeviceThumbprint, string[] Scopes,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RevokedAtUtc, string? RevokedBy);

public enum ChangeState { Pending, Approved, Applying, Applied, Rejected, Cancelled, Expired }

public sealed record ChangeRequest(
    Guid ChangeRequestId, string Type, string TargetKey, string PayloadJson, string? Reason, string RequestedBy,
    ChangeState State, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? DecidedAtUtc,
    string? DecidedBy, string? FailureReason, string IdempotencyKey);

/// <summary>
/// 自动化状态库。存放配对请求、授权记录、撤销标记、变更申请与持久化实例标识。
/// 选 SQLite 的理由：既有审计与流量历史已是同栈依赖；这里需要按到期时间扫描与并发撤销；
/// 且授权状态不能塞进受保留期裁剪的审计表。
/// </summary>
public sealed class AutomationStore : IAsyncDisposable
{
    private readonly string databasePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? connectionString;
    private bool initialized;

    public AutomationStore(string serverConfigurationPath)
    {
        DatabasePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(serverConfigurationPath))!, "automation", "state.db");
        databasePath = DatabasePath;
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            var directory = Path.GetDirectoryName(databasePath)!;
            Directory.CreateDirectory(directory);
            SecretFilePermissions.PrepareDatabase(databasePath);
            connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS automation_instance (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    instance_id TEXT NOT NULL,
                    created_at_utc INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS pairing_request (
                    request_id TEXT PRIMARY KEY, subject TEXT NOT NULL, platform TEXT NOT NULL, cli_version TEXT NOT NULL,
                    scopes TEXT NOT NULL, device_thumbprint TEXT NOT NULL, device_public_key TEXT NOT NULL,
                    user_code_digest TEXT NOT NULL, user_code_display TEXT NOT NULL, state TEXT NOT NULL,
                    failed_attempts INTEGER NOT NULL, created_at_utc INTEGER NOT NULL, expires_at_utc INTEGER NOT NULL,
                    decided_at_utc INTEGER, decided_by TEXT, lifetime_minutes INTEGER, approved_scopes TEXT,
                    token_issued_at_utc INTEGER, token_hash TEXT, token_expires_at_utc INTEGER, grant_id TEXT);
                CREATE INDEX IF NOT EXISTS ix_pairing_state ON pairing_request (state, expires_at_utc);
                CREATE TABLE IF NOT EXISTS automation_grant (
                    grant_id TEXT PRIMARY KEY, subject TEXT NOT NULL, platform TEXT NOT NULL,
                    device_thumbprint TEXT NOT NULL, scopes TEXT NOT NULL, created_at_utc INTEGER NOT NULL,
                    expires_at_utc INTEGER NOT NULL, revoked_at_utc INTEGER, revoked_by TEXT);
                CREATE TABLE IF NOT EXISTS change_request (
                    change_request_id TEXT PRIMARY KEY, type TEXT NOT NULL, target_key TEXT NOT NULL,
                    payload_json TEXT NOT NULL, reason TEXT, requested_by TEXT NOT NULL, state TEXT NOT NULL,
                    created_at_utc INTEGER NOT NULL, expires_at_utc INTEGER NOT NULL, decided_at_utc INTEGER,
                    decided_by TEXT, failure_reason TEXT, idempotency_key TEXT NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_change_idempotency ON change_request (idempotency_key);
                CREATE INDEX IF NOT EXISTS ix_change_open_target ON change_request (type, target_key, state);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            SecretFilePermissions.RestrictDatabase(databasePath);
            initialized = true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 持久化实例标识。首次调用生成并落库，之后永不改变。
    /// 不得复用 ServerRuntime.InstanceId：那个值是每进程 Guid.NewGuid()，重启即变，
    /// 会让令牌证明与 CLI 固定校验在每次重启后失效。
    /// </summary>
    public async Task<Guid> GetOrCreateInstanceIdAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO automation_instance (id, instance_id, created_at_utc) VALUES (1, $id, $now)
                    ON CONFLICT (id) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT instance_id FROM automation_instance WHERE id = 1;";
            var value = await read.ExecuteScalarAsync(cancellationToken);
            return Guid.TryParse(value as string, out var instanceId) ? instanceId : throw new InvalidOperationException("Automation instance id could not be persisted.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString ?? throw new InvalidOperationException("Automation store is not initialized."));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    // ---------------- 配对 ----------------

    public async Task CreatePairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO pairing_request (request_id, subject, platform, cli_version, scopes, device_thumbprint,
                    device_public_key, user_code_digest, user_code_display, state, failed_attempts, created_at_utc, expires_at_utc)
                VALUES ($id, $subject, $platform, $cli, $scopes, $thumb, $key, $digest, $display, $state, $attempts, $created, $expires);
                """;
            command.Parameters.AddWithValue("$id", request.RequestId.ToString());
            command.Parameters.AddWithValue("$subject", request.Subject);
            command.Parameters.AddWithValue("$platform", request.Platform);
            command.Parameters.AddWithValue("$cli", request.CliVersion);
            command.Parameters.AddWithValue("$scopes", string.Join(',', request.Scopes));
            command.Parameters.AddWithValue("$thumb", request.DeviceThumbprint);
            command.Parameters.AddWithValue("$key", request.DevicePublicKeyJwk);
            command.Parameters.AddWithValue("$digest", request.UserCodeDigest);
            command.Parameters.AddWithValue("$display", request.UserCodeDisplay);
            command.Parameters.AddWithValue("$state", request.State.ToString());
            command.Parameters.AddWithValue("$attempts", request.FailedAttempts);
            command.Parameters.AddWithValue("$created", request.CreatedAtUtc.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$expires", request.ExpiresAtUtc.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<PairingRequest?> GetPairingAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = SelectPairingSql + " WHERE request_id = $id;";
            command.Parameters.AddWithValue("$id", requestId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadPairing(reader) : null;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<PairingRequest>> ListPairingsAsync(PairingState? state, int limit, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = state is null
                ? SelectPairingSql + " ORDER BY created_at_utc DESC LIMIT $limit;"
                : SelectPairingSql + " WHERE state = $state ORDER BY created_at_utc DESC LIMIT $limit;";
            if (state is not null) command.Parameters.AddWithValue("$state", state.Value.ToString());
            command.Parameters.AddWithValue("$limit", limit);
            var results = new List<PairingRequest>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) results.Add(ReadPairing(reader));
            return results;
        }
        finally { gate.Release(); }
    }

    public async Task<int> CountPairingsAsync(PairingState state, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pairing_request WHERE state = $state;";
            command.Parameters.AddWithValue("$state", state.ToString());
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        finally { gate.Release(); }
    }

    public async Task<int> CountActiveGrantsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM automation_grant WHERE revoked_at_utc IS NULL AND expires_at_utc > $now;";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// 记录一次用户码错误。返回提交后的失败次数；达到阈值时把请求置为 Locked。
    /// </summary>
    public async Task<(int Attempts, PairingState State)> RegisterUserCodeFailureAsync(Guid requestId, int lockAfter, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE pairing_request SET failed_attempts = failed_attempts + 1 WHERE request_id = $id;
                """;
            update.Parameters.AddWithValue("$id", requestId.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken);

            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT failed_attempts, state FROM pairing_request WHERE request_id = $id;";
            read.Parameters.AddWithValue("$id", requestId.ToString());
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return (0, PairingState.Expired);
            var attempts = reader.GetInt32(0);
            var state = Enum.Parse<PairingState>(reader.GetString(1));
            await reader.DisposeAsync();
            // Pending 与 Approved 都计锁：Approved 未换取的请求若不锁，
            // 持有设备私钥者（私钥被窃但无用户码）可无上限地猜用户码。
            if (attempts >= lockAfter && state is PairingState.Pending or PairingState.Approved)
            {
                await using var lockCommand = connection.CreateCommand();
                lockCommand.CommandText = "UPDATE pairing_request SET state = $state WHERE request_id = $id AND state = $from;";
                lockCommand.Parameters.AddWithValue("$state", PairingState.Locked.ToString());
                lockCommand.Parameters.AddWithValue("$from", state.ToString());
                lockCommand.Parameters.AddWithValue("$id", requestId.ToString());
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
                state = PairingState.Locked;
            }

            return (attempts, state);
        }
        finally { gate.Release(); }
    }

    /// <summary>状态迁移。带状态守卫：仅当当前状态为 allowedFrom 之一时生效，防止批准/取消竞态覆盖。</summary>
    public async Task<bool> SetPairingStateAsync(Guid requestId, PairingState state, DateTimeOffset nowUtc, PairingState[]? allowedFrom = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            if (allowedFrom is { Length: > 0 })
            {
                var placeholders = string.Join(',', allowedFrom.Select((_, index) => "$from" + index));
                command.CommandText = $"UPDATE pairing_request SET state = $state WHERE request_id = $id AND state IN ({placeholders});";
                for (var index = 0; index < allowedFrom.Length; index++)
                    command.Parameters.AddWithValue("$from" + index, allowedFrom[index].ToString());
            }
            else
            {
                command.CommandText = "UPDATE pairing_request SET state = $state WHERE request_id = $id;";
            }

            command.Parameters.AddWithValue("$state", state.ToString());
            command.Parameters.AddWithValue("$id", requestId.ToString());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { gate.Release(); }
    }

    /// <summary>管理页面批准：只能缩小 scope 与有效期。</summary>
    public async Task<bool> ApprovePairingAsync(Guid requestId, string[] approvedScopes, int lifetimeMinutes, string admin, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE pairing_request SET state = $approved, decided_at_utc = $now, decided_by = $admin,
                    approved_scopes = $scopes, lifetime_minutes = $lifetime
                WHERE request_id = $id AND state = $pending;
                """;
            command.Parameters.AddWithValue("$approved", PairingState.Approved.ToString());
            command.Parameters.AddWithValue("$pending", PairingState.Pending.ToString());
            command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$admin", admin);
            command.Parameters.AddWithValue("$scopes", string.Join(',', approvedScopes));
            command.Parameters.AddWithValue("$lifetime", lifetimeMinutes);
            command.Parameters.AddWithValue("$id", requestId.ToString());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DenyPairingAsync(Guid requestId, string admin, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE pairing_request SET state = $denied, decided_at_utc = $now, decided_by = $admin
                WHERE request_id = $id AND state = $pending;
                """;
            command.Parameters.AddWithValue("$denied", PairingState.Denied.ToString());
            command.Parameters.AddWithValue("$pending", PairingState.Pending.ToString());
            command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$admin", admin);
            command.Parameters.AddWithValue("$id", requestId.ToString());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// 在单个事务内完成「查状态 → 作废请求 → 签发授权」三步。
    /// 这保证两个不同 jti 的并发证明不会各自签发一枚令牌。
    /// </summary>
    public async Task<AutomationGrant?> CompletePairingAsync(Guid requestId, string tokenHash,
        DateTimeOffset nowUtc, string[]? requestedScopes, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            PairingRequest? current;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = SelectPairingSql + " WHERE request_id = $id;";
                read.Parameters.AddWithValue("$id", requestId.ToString());
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                current = await reader.ReadAsync(cancellationToken) ? ReadPairing(reader) : null;
            }

            if (current is null || current.State != PairingState.Approved || current.TokenHash is not null ||
                current.ExpiresAtUtc <= nowUtc)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var scopes = current.ApprovedScopes is { Length: > 0 } ? current.ApprovedScopes : current.Scopes;
            var lifetime = current.LifetimeMinutes ?? 120;
            var grant = new AutomationGrant(Guid.NewGuid(), current.Subject, current.Platform, current.DeviceThumbprint,
                scopes, nowUtc, nowUtc.AddMinutes(lifetime), null, null);

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO automation_grant (grant_id, subject, platform, device_thumbprint, scopes, created_at_utc, expires_at_utc)
                    VALUES ($grant, $subject, $platform, $thumb, $scopes, $created, $expires);
                    """;
                insert.Parameters.AddWithValue("$grant", grant.GrantId.ToString());
                insert.Parameters.AddWithValue("$subject", grant.Subject);
                insert.Parameters.AddWithValue("$platform", grant.Platform);
                insert.Parameters.AddWithValue("$thumb", grant.DeviceThumbprint);
                insert.Parameters.AddWithValue("$scopes", string.Join(',', grant.Scopes));
                insert.Parameters.AddWithValue("$created", grant.CreatedAtUtc.ToUnixTimeSeconds());
                insert.Parameters.AddWithValue("$expires", grant.ExpiresAtUtc.ToUnixTimeSeconds());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE pairing_request SET state = $completed, token_issued_at_utc = $now, token_hash = $hash,
                        token_expires_at_utc = $expires, grant_id = $grant
                    WHERE request_id = $id AND state = $approved AND token_hash IS NULL;
                    """;
                update.Parameters.AddWithValue("$completed", PairingState.Completed.ToString());
                update.Parameters.AddWithValue("$approved", PairingState.Approved.ToString());
                update.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
                update.Parameters.AddWithValue("$hash", tokenHash);
                update.Parameters.AddWithValue("$expires", grant.ExpiresAtUtc.ToUnixTimeSeconds());
                update.Parameters.AddWithValue("$grant", grant.GrantId.ToString());
                update.Parameters.AddWithValue("$id", requestId.ToString());
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return grant;
        }
        finally { gate.Release(); }
    }

    private const string SelectPairingSql = """
        SELECT request_id, subject, platform, cli_version, scopes, device_thumbprint, device_public_key,
            user_code_digest, user_code_display, state, failed_attempts, created_at_utc, expires_at_utc,
            decided_at_utc, decided_by, lifetime_minutes, approved_scopes, token_issued_at_utc, token_hash,
            token_expires_at_utc, grant_id
        FROM pairing_request
        """;

    private static PairingRequest ReadPairing(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        Split(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
        Enum.Parse<PairingState>(reader.GetString(9)), reader.GetInt32(10),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(11)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(12)),
        reader.IsDBNull(13) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(13)),
        reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetInt32(15),
        reader.IsDBNull(16) ? null : Split(reader.GetString(16)),
        reader.IsDBNull(17) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(17)),
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.IsDBNull(19) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(19)),
        reader.IsDBNull(20) ? null : Guid.Parse(reader.GetString(20)));

    private static string[] Split(string value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    // ---------------- 授权 ----------------

    public async Task<AutomationGrant?> FindGrantByTokenHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT g.grant_id, g.subject, g.platform, g.device_thumbprint, g.scopes, g.created_at_utc,
                    g.expires_at_utc, g.revoked_at_utc, g.revoked_by
                FROM automation_grant g
                JOIN pairing_request p ON p.grant_id = g.grant_id
                WHERE p.token_hash = $hash;
                """;
            command.Parameters.AddWithValue("$hash", tokenHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new AutomationGrant(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), Split(reader.GetString(4)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8));
        }
        finally { gate.Release(); }
    }

    public async Task<DateTimeOffset?> GetTokenExpiryAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT token_expires_at_utc FROM pairing_request WHERE token_hash = $hash;";
            command.Parameters.AddWithValue("$hash", tokenHash);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value));
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// 取令牌绑定的设备公钥 JWK。公钥来自配对时登记的记录，必须持久读取：
    /// 存在进程内存里会在重启后丢失，导致所有未过期令牌无法通过证明校验。
    /// </summary>
    public async Task<string?> GetDevicePublicKeyAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT device_public_key FROM pairing_request WHERE token_hash = $hash;";
            command.Parameters.AddWithValue("$hash", tokenHash);
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<AutomationGrant>> ListGrantsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT grant_id, subject, platform, device_thumbprint, scopes, created_at_utc, expires_at_utc,
                    revoked_at_utc, revoked_by FROM automation_grant ORDER BY created_at_utc DESC;
                """;
            var results = new List<AutomationGrant>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new AutomationGrant(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), Split(reader.GetString(4)),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                    reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            return results;
        }
        finally { gate.Release(); }
    }

    /// <summary>撤销先落库再确认，避免「提示撤销但重启后复活」。</summary>
    public async Task<bool> RevokeGrantAsync(Guid grantId, string admin, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE automation_grant SET revoked_at_utc = $now, revoked_by = $admin
                WHERE grant_id = $id AND revoked_at_utc IS NULL;
                """;
            command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$admin", admin);
            command.Parameters.AddWithValue("$id", grantId.ToString());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { gate.Release(); }
    }

    // ---------------- 变更申请 ----------------

    public async Task<ChangeRequest?> FindChangeByIdempotencyAsync(string key, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = SelectChangeSql + " WHERE idempotency_key = $key;";
            command.Parameters.AddWithValue("$key", key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadChange(reader) : null;
        }
        finally { gate.Release(); }
    }

    public async Task<ChangeRequest?> FindOpenChangeAsync(string type, string targetKey, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = SelectChangeSql +
                " WHERE type = $type AND target_key = $target AND state IN ('Pending','Approved','Applying') LIMIT 1;";
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$target", targetKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadChange(reader) : null;
        }
        finally { gate.Release(); }
    }

    public async Task CreateChangeAsync(ChangeRequest request, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO change_request (change_request_id, type, target_key, payload_json, reason, requested_by,
                    state, created_at_utc, expires_at_utc, idempotency_key)
                VALUES ($id, $type, $target, $payload, $reason, $by, $state, $created, $expires, $key);
                """;
            command.Parameters.AddWithValue("$id", request.ChangeRequestId.ToString());
            command.Parameters.AddWithValue("$type", request.Type);
            command.Parameters.AddWithValue("$target", request.TargetKey);
            command.Parameters.AddWithValue("$payload", request.PayloadJson);
            command.Parameters.AddWithValue("$reason", (object?)request.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$by", request.RequestedBy);
            command.Parameters.AddWithValue("$state", request.State.ToString());
            command.Parameters.AddWithValue("$created", request.CreatedAtUtc.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$expires", request.ExpiresAtUtc.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$key", request.IdempotencyKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<ChangeRequest?> GetChangeAsync(Guid changeRequestId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = SelectChangeSql + " WHERE change_request_id = $id;";
            command.Parameters.AddWithValue("$id", changeRequestId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadChange(reader) : null;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<ChangeRequest>> ListChangesAsync(ChangeState? state, int limit, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = state is null
                ? SelectChangeSql + " ORDER BY created_at_utc DESC LIMIT $limit;"
                : SelectChangeSql + " WHERE state = $state ORDER BY created_at_utc DESC LIMIT $limit;";
            if (state is not null) command.Parameters.AddWithValue("$state", state.Value.ToString());
            command.Parameters.AddWithValue("$limit", limit);
            var results = new List<ChangeRequest>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) results.Add(ReadChange(reader));
            return results;
        }
        finally { gate.Release(); }
    }

    /// <summary>状态迁移。仅当当前状态在 allowedFrom 中时生效，返回是否成功。</summary>
    public async Task<bool> TransitionChangeAsync(Guid changeRequestId, ChangeState to, ChangeState[] allowedFrom,
        string? admin, string? failureReason, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var placeholders = string.Join(',', allowedFrom.Select((_, index) => "$from" + index));
            command.CommandText = $"""
                UPDATE change_request SET state = $to, decided_at_utc = COALESCE(decided_at_utc, $now),
                    decided_by = COALESCE($admin, decided_by), failure_reason = $failure
                WHERE change_request_id = $id AND state IN ({placeholders});
                """;
            command.Parameters.AddWithValue("$to", to.ToString());
            command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$admin", (object?)admin ?? DBNull.Value);
            command.Parameters.AddWithValue("$failure", (object?)failureReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", changeRequestId.ToString());
            for (var index = 0; index < allowedFrom.Length; index++)
                command.Parameters.AddWithValue("$from" + index, allowedFrom[index].ToString());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { gate.Release(); }
    }

    /// <summary>启动对账：取所有处于 Applying 的申请，交由上层按 (type,target) 复查目标状态。</summary>
    public async Task<IReadOnlyList<ChangeRequest>> ListApplyingChangesAsync(CancellationToken cancellationToken) =>
        await ListChangesAsync(ChangeState.Applying, 256, cancellationToken);

    private const string SelectChangeSql = """
        SELECT change_request_id, type, target_key, payload_json, reason, requested_by, state, created_at_utc,
            expires_at_utc, decided_at_utc, decided_by, failure_reason, idempotency_key
        FROM change_request
        """;

    private static ChangeRequest ReadChange(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), Enum.Parse<ChangeState>(reader.GetString(6)),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)),
        reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9)),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetString(12));

    public ValueTask DisposeAsync()
    {
        gate.Dispose();
        return ValueTask.CompletedTask;
    }
}