using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelayLink.Server.Runtime;

public sealed record CredentialOperation(Guid OperationId, string ClientId, string ChannelId, string CredentialId,
    string Action, string RequestHash, bool Applied, CredentialMetadata? Credential, DateTimeOffset CreatedUtc)
{
    public Guid AuditId { get; init; }
    public bool AuditRecorded { get; set; }
    public bool Retired { get; set; }
}

public sealed record CredentialDeletion(string ClientId, string? ChannelId);

public sealed partial class CredentialStore
{
    public const int MaxOperationReceipts = 1024;
    public static readonly TimeSpan OperationRetention = TimeSpan.FromHours(24);

    internal static void ValidateMutation(string clientId, string channelId, string credentialId, CredentialWriteRequest? request, ReadOnlySpan<byte> secret)
    {
        if (request is not null) Validate(clientId, channelId, credentialId, request.Username, request.Purpose, request.Type, secret);
        else if (!Identifier.IsMatch(clientId) || !Identifier.IsMatch(channelId) || !Identifier.IsMatch(credentialId))
            throw new CredentialStoreException("Invalid credential identifier.");
    }

    internal static string RequestHash(string clientId, string channelId, string credentialId, string action, CredentialWriteRequest? request, ReadOnlyMemory<byte> secret)
    {
        var material = JsonSerializer.SerializeToUtf8Bytes(new
        {
            clientId, channelId, credentialId, action, request?.Username, request?.Purpose, request?.Type,
            secretHash = Convert.ToHexString(SHA256.HashData(secret.Span))
        });
        try { return Convert.ToHexString(SHA256.HashData(material)); }
        finally { CryptographicOperations.ZeroMemory(material); }
    }

    public async Task<CredentialOperation?> FindOperationAsync(Guid operationId, string requestHash, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var document = Read();
            var operation = document.Operations.SingleOrDefault(item => item.OperationId == operationId);
            if (operation is null) return null;
            if (operation.RequestHash != requestHash) throw new CredentialStoreException("Operation ID was already used for a different request.");
            EnsureNotDeleting(document, operation.ClientId, operation.ChannelId);
            if (operation.Retired) throw new CredentialStoreException("The operation belongs to a deleted channel.");
            return operation;
        }
        finally { gate.Release(); }
    }

    internal async Task<CredentialOperation> ApplyOperationAsync(Guid operationId, string requestHash, string clientId, string channelId,
        string credentialId, CredentialWriteRequest? request, ReadOnlyMemory<byte> secret, CancellationToken token, Guid? auditId = null)
    {
        if (operationId == Guid.Empty) throw new CredentialStoreException("Operation ID cannot be empty.");
        ValidateMutation(clientId, channelId, credentialId, request, secret.Span);
        await gate.WaitAsync(token);
        try
        {
            var document = Read();
            EnsureNotDeleting(document, clientId, channelId);
            var replay = document.Operations.SingleOrDefault(item => item.OperationId == operationId);
            if (replay is not null)
            {
                if (replay.RequestHash != requestHash || replay.Retired) throw new CredentialStoreException("Operation ID is not valid for this request.");
                return replay;
            }
            document.Operations.RemoveAll(item => item.AuditRecorded && item.CreatedUtc < DateTimeOffset.UtcNow - OperationRetention);
            if (document.Operations.Count >= MaxOperationReceipts) throw new CredentialStoreException("Credential operation receipt capacity is full; retry after audit recovery or retention expiry.");
            CredentialMetadata? metadata = null;
            bool applied;
            if (request is null)
                applied = document.Credentials.RemoveAll(item => item.ClientId == clientId && item.ChannelId == channelId && item.CredentialId == credentialId) > 0;
            else
            {
                var existing = document.Credentials.FindIndex(item => item.ClientId == clientId && item.ChannelId == channelId && item.CredentialId == credentialId);
                if (existing < 0 && document.Credentials.Count(item => item.ClientId == clientId && item.ChannelId == channelId) >= MaxPerChannel)
                    throw new CredentialStoreException("Credential capacity for the channel is full.");
                var stored = new StoredCredential
                {
                    ClientId = clientId, ChannelId = channelId, CredentialId = credentialId, Username = request.Username,
                    Purpose = request.Purpose, Type = request.Type, Secret = Convert.ToBase64String(secret.Span), UpdatedUtc = DateTimeOffset.UtcNow
                };
                if (existing < 0) document.Credentials.Add(stored); else document.Credentials[existing] = stored;
                metadata = Metadata(stored);
                applied = true;
            }
            var result = new CredentialOperation(operationId, clientId, channelId, credentialId, request is null ? "delete" : "save", requestHash, applied, metadata, DateTimeOffset.UtcNow)
            { AuditId = auditId ?? Guid.NewGuid() };
            document.Operations.Add(result);
            await WriteAsync(document, token);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task MarkOperationAuditedAsync(Guid operationId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var document = Read();
            var operation = document.Operations.SingleOrDefault(item => item.OperationId == operationId);
            if (operation is null || operation.AuditRecorded) return;
            operation.AuditRecorded = true;
            await WriteAsync(document, token);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<CredentialOperation>> PendingAuditsAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return Read().Operations.Where(item => !item.AuditRecorded).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task EnsureChannelAvailableAsync(string clientId, string channelId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { EnsureNotDeleting(Read(), clientId, channelId); }
        finally { gate.Release(); }
    }

    public async Task BeginDeletionAsync(string clientId, string? channelId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var document = Read();
            var marker = new CredentialDeletion(clientId, channelId);
            if (document.Deletions.Contains(marker)) return;
            if (document.Deletions.Count >= 1024) throw new CredentialStoreException("Credential deletion recovery capacity is full.");
            document.Deletions.Add(marker);
            await WriteAsync(document, token);
        }
        finally { gate.Release(); }
    }

    public async Task CompleteDeletionAsync(string clientId, string? channelId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var document = Read();
            if (!document.Deletions.Contains(new CredentialDeletion(clientId, channelId))) throw new CredentialStoreException("Deletion was not prepared.");
            document.Credentials.RemoveAll(item => item.ClientId == clientId && (channelId is null || item.ChannelId == channelId));
            document.HostKeys.RemoveAll(item => item.ClientId == clientId && (channelId is null || item.ChannelId == channelId));
            foreach (var operation in document.Operations.Where(item => item.ClientId == clientId && (channelId is null || item.ChannelId == channelId))) operation.Retired = true;
            document.Deletions.RemoveAll(item => item.ClientId == clientId && (channelId is null || item.ChannelId == channelId));
            await WriteAsync(document, token);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<CredentialDeletion>> PendingDeletionsAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return Read().Deletions.ToArray(); }
        finally { gate.Release(); }
    }

    // Configuration removal has already committed. The durable marker makes failure
    // safe to retry and prevents returning a misleading "configuration unchanged" error.
    internal async Task<bool> TryCompleteDeletionAsync(string clientId, string? channelId)
    {
        try { await CompleteDeletionAsync(clientId, channelId, CancellationToken.None); return true; }
        catch (Exception exception) when (exception is CredentialStoreException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static void EnsureNotDeleting(Document document, string clientId, string channelId)
    {
        if (document.Deletions.Any(item => item.ClientId == clientId && (item.ChannelId is null || item.ChannelId == channelId)))
            throw new CredentialStoreException("Credential cleanup is pending for this channel or client; retry deletion before reuse.");
    }
}
