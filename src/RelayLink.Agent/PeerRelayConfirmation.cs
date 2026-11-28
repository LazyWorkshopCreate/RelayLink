using System.Security.Authentication;
using System.Security.Cryptography;
using RelayLink.Protocol;
using RelayLink.Transport;

namespace RelayLink.Agent;

internal sealed class PeerRelayConfirmation(PeerRelayBinding binding, bool target, FrameWriter control,
    CancellationTokenSource deadline, CancellationToken sessionToken, Func<ClientConfigSnapshot, bool> authorized, Action unregister) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly TaskCompletionSource approved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PeerRelaySignal? authenticated;
    private PeerRelaySignal? prepare;
    private bool remoteAborted;
    private bool confirmed;
    private bool disposed;

    public void CheckConfiguration(ClientConfigSnapshot configuration)
    {
        lock (sync)
        {
            if (!disposed && !confirmed && !authorized(configuration)) deadline.Cancel();
        }
    }

    public async Task AuthenticateAsync(Stream stream, string accessSecret)
    {
        var token = deadline.Token;
        var targetNonce = new byte[32];
        var response = new byte[64]; // caller nonce followed by the access proof
        if (target)
        {
            RandomNumberGenerator.Fill(targetNonce);
            await stream.WriteAsync(targetNonce, token);
            await stream.ReadExactlyAsync(response, token);
        }
        else
        {
            await stream.ReadExactlyAsync(targetNonce, token);
            RandomNumberGenerator.Fill(response.AsSpan(0, 32));
        }
        var key = Convert.FromBase64String(accessSecret);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        CryptographicOperations.ZeroMemory(key);
        hmac.AppendData("RelayLink relay control proof v1\0"u8);
        hmac.AppendData(JsonProtocolSerializer.Serialize(binding));
        hmac.AppendData(targetNonce);
        hmac.AppendData(response.AsSpan(0, 32));
        var proof = hmac.GetHashAndReset();
        if (target)
        {
            if (!CryptographicOperations.FixedTimeEquals(proof, response.AsSpan(32)))
                throw new AuthenticationException("Relay access proof was invalid.");
        }
        else
        {
            proof.CopyTo(response, 32);
            await stream.WriteAsync(response, token);
        }
        var signal = new PeerRelaySignal(binding, Convert.ToHexString(SHA256.HashData(proof)));
        lock (sync) authenticated = signal;
        await SendAsync(FrameType.PeerRelayAuthenticated, signal, token);
        await approved.Task.WaitAsync(token);
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (!target) confirmed = true;
        }
    }

    public void Receive(FrameType type, PeerRelaySignal signal)
    {
        lock (sync)
        {
            if (disposed || signal.Binding != binding) return;
            if (target && type == FrameType.PeerRelayPrepare && authenticated is { } reported && reported.HandshakeId == signal.HandshakeId &&
                signal.PreparationId is { } id && id != Guid.Empty && (prepare is null || prepare == signal))
            {
                prepare = signal;
                approved.TrySetResult();
            }
            else if (!target && type == FrameType.PeerRelayTargetReady && signal == authenticated)
                approved.TrySetResult();
            else
            {
                remoteAborted = true;
                deadline.Cancel();
            }
        }
    }

    public async Task TargetReadyAsync()
    {
        deadline.Token.ThrowIfCancellationRequested();
        await SendAsync(FrameType.PeerRelayTargetReady, prepare!, deadline.Token);
        lock (sync)
        {
            deadline.Token.ThrowIfCancellationRequested();
            confirmed = true;
        }
    }

    private ValueTask SendAsync(FrameType type, PeerRelaySignal signal, CancellationToken token) =>
        control.WriteControlAsync(new Frame(type, JsonProtocolSerializer.Serialize(signal)), token, sessionToken);

    public async ValueTask DisposeAsync()
    {
        bool reportAbort;
        lock (sync)
        {
            disposed = true;
            reportAbort = !confirmed && !remoteAborted && !sessionToken.IsCancellationRequested;
        }
        unregister();
        if (!reportAbort) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { await SendAsync(FrameType.PeerRelayAborted, new PeerRelaySignal(binding, ""), timeout.Token); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }
}
