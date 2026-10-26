namespace RelayLink.Transport;

// A non-blocking permit counter whose maximum can change while permits are held.
public sealed class AdjustableConnectionLimit
{
    private readonly object gate = new();
    private int maximum;
    private int held;

    public AdjustableConnectionLimit(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        this.maximum = maximum;
    }

    public int Maximum
    {
        get { lock (gate) return maximum; }
    }

    public int Held
    {
        get { lock (gate) return held; }
    }

    public bool TryAcquire()
    {
        lock (gate)
        {
            if (held >= maximum) return false;
            held++;
            return true;
        }
    }

    public void Release()
    {
        lock (gate)
        {
            if (held == 0) throw new InvalidOperationException("Connection limit released more times than it was acquired.");
            held--;
        }
    }

    public void SetMaximum(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        lock (gate) this.maximum = maximum;
    }
}
