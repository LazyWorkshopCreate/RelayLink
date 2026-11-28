using RelayLink.Protocol;

namespace RelayLink.Server.Runtime;

// The control reader supplies the authenticated sender. A role in a message cannot select a session.
internal sealed class PeerRelaySetup(PeerRelayBinding binding, TimeSpan timeout, Func<bool> authorized)
{
    private readonly object sync = new();
    private readonly TaskCompletionSource<string> authenticated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource targetReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? callerHash;
    private string? targetHash;
    private Guid? preparationId;
    private bool rejected;

    public PeerRelayBinding Binding { get; } = binding;
    public TimeSpan Timeout { get; } = timeout;
    public bool IsAuthorized() => authorized();

    public bool Report(bool fromTarget, FrameType type, PeerRelaySignal signal)
    {
        var hash = signal.HandshakeId;
        lock (sync)
        {
            if (rejected) return false;
            if (type == FrameType.PeerRelayAuthenticated && signal.PreparationId is null && hash is { Length: 64 } && hash.All(char.IsAsciiHexDigitUpper))
            {
                ref var previous = ref (fromTarget ? ref targetHash : ref callerHash);
                previous ??= hash;
                if (previous == hash && (callerHash is null || targetHash is null || callerHash == targetHash))
                {
                    if (callerHash is not null && targetHash is not null) authenticated.TrySetResult(hash);
                    return true;
                }
            }
            else if (type == FrameType.PeerRelayTargetReady && fromTarget && preparationId.HasValue &&
                signal.PreparationId == preparationId && hash == targetHash)
            {
                targetReady.TrySetResult();
                return true;
            }
            rejected = true;
            return false;
        }
    }

    public async Task ConfirmAsync(Session caller, Session target, CancellationToken token)
    {
        var hash = await authenticated.Task.WaitAsync(token);
        var signal = new PeerRelaySignal(Binding, hash);
        PeerRelaySignal prepare;
        lock (sync)
        {
            CheckAuthorization(token);
            preparationId = Guid.NewGuid();
            prepare = signal with { PreparationId = preparationId };
        }
        await target.SendPeerControlAsync(new Frame(FrameType.PeerRelayPrepare, JsonProtocolSerializer.Serialize(prepare)), token);
        await targetReady.Task.WaitAsync(token);
        lock (sync) CheckAuthorization(token);
        await caller.SendPeerControlAsync(new Frame(FrameType.PeerRelayTargetReady, JsonProtocolSerializer.Serialize(signal)), token);
        lock (sync) CheckAuthorization(token);
    }

    private void CheckAuthorization(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (rejected || !authorized()) throw new ProtocolException("Relay setup authorization is no longer current.");
    }
}
