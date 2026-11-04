using System.Security.Cryptography;
using System.Text;

namespace RelayLink.Server.Runtime;

public sealed record CredentialMutationResult(CredentialOperation Operation, bool AuditPending);

// Callers serialize configuration membership checks and this operation under ConfigurationWriteLock.
public sealed class CredentialMutationService(CredentialStore store, AuditService audit, ILogger<CredentialMutationService> logger)
{
    public async Task<CredentialMutationResult> ApplyAsync(Guid operationId, string clientId, string channelId, string credentialId,
        CredentialWriteRequest? request, ReadOnlyMemory<byte> secret, CancellationToken token)
    {
        if (operationId == Guid.Empty) throw new CredentialStoreException("Operation ID cannot be empty.");
        CredentialStore.ValidateMutation(clientId, channelId, credentialId, request, secret.Span);
        var action = request is null ? "delete" : "save";
        var hash = CredentialStore.RequestHash(clientId, channelId, credentialId, action, request, secret);
        var operation = await store.FindOperationAsync(operationId, hash, token);
        if (operation is null)
        {
            var auditId = Guid.NewGuid();
            await audit.RecordAsync(new AuditEvent("credential_change_requested", "pending")
            {
                EventId = EventId(auditId, "requested"), ClientId = clientId, ChannelId = channelId,
                ReasonCode = $"{operationId:N} {action} {credentialId}"
            }, token);
            try { operation = await store.ApplyOperationAsync(operationId, hash, clientId, channelId, credentialId, request, secret, token, auditId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CredentialStoreException or OperationCanceledException)
            {
                // A filesystem permission/reporting error after atomic replacement must not say "not applied".
                try { operation = await store.FindOperationAsync(operationId, hash, CancellationToken.None); }
                catch (Exception failure) when (failure is CredentialStoreException or IOException or UnauthorizedAccessException) { }
                if (operation is null) throw;
            }
        }
        if (operation.AuditRecorded) return new(operation, false);
        var recorded = await TryRecordResultAsync(operation, CancellationToken.None);
        return new(operation, !recorded);
    }

    public async Task<bool> TryRecordResultAsync(CredentialOperation operation, CancellationToken token)
    {
        try
        {
            await audit.RecordAsync(new AuditEvent(operation.Action == "save" ? "credential_saved" : "credential_deleted", operation.Applied ? "success" : "not_found")
            {
                EventId = EventId(operation.AuditId == Guid.Empty ? operation.OperationId : operation.AuditId, "result"), ClientId = operation.ClientId, ChannelId = operation.ChannelId,
                ReasonCode = $"{operation.OperationId:N} {operation.CredentialId}"
            }, token);
            await store.MarkOperationAuditedAsync(operation.OperationId, token);
            return true;
        }
        catch (Exception exception) when (exception is AuditUnavailableException or CredentialStoreException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            logger.LogWarning("Credential change {OperationId} was persisted; result audit remains pending.", operation.OperationId);
            return false;
        }
    }

    private static Guid EventId(Guid operationId, string phase) => new(SHA256.HashData(Encoding.ASCII.GetBytes(operationId.ToString("N") + phase)).AsSpan(0, 16));
}

public sealed class CredentialRecoveryService(CredentialStore store, CredentialMutationService mutations,
    ConfigurationWriteLock writeLock, ServerRuntime runtime, ILogger<CredentialRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await writeLock.Gate.WaitAsync(stoppingToken);
                try
                {
                    foreach (var deletion in await store.PendingDeletionsAsync(stoppingToken))
                    {
                        var existing = runtime.Configuration.Clients.GetValueOrDefault(deletion.ClientId);
                        if (existing is null || (deletion.ChannelId is not null && !existing.Channels.Any(channel => channel.ChannelId == deletion.ChannelId)))
                            await store.CompleteDeletionAsync(deletion.ClientId, deletion.ChannelId, stoppingToken);
                    }
                }
                finally { writeLock.Gate.Release(); }
                foreach (var operation in (await store.PendingAuditsAsync(stoppingToken)).Take(64))
                    if (!await mutations.TryRecordResultAsync(operation, stoppingToken)) break;
            }
            catch (Exception exception) when (exception is CredentialStoreException or IOException or UnauthorizedAccessException)
            { logger.LogWarning("Credential recovery is unavailable; inspection channels remain closed until the store is repaired."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
