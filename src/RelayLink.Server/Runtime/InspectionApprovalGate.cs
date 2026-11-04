namespace RelayLink.Server.Runtime;

/// <summary>One bounded approval budget shared by all built-in inspection protocols.</summary>
public static class InspectionApprovalGate
{
    public const int Capacity = 256;
    private static readonly SemaphoreSlim Slots = new(Capacity, Capacity);

    public static IDisposable? TryEnter() => Slots.Wait(0) ? new Lease() : null;

    private sealed class Lease : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) Slots.Release(); }
    }
}
