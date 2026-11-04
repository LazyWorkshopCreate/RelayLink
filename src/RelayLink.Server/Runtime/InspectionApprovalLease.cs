namespace RelayLink.Server.Runtime;

internal sealed class InspectionApprovalLease(Action? release = null) : IDisposable
{
    private Action? release = release;
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}
