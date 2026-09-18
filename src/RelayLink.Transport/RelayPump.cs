using System.Net.Sockets;

namespace RelayLink.Transport;

public static class RelayPump
{
    public static async Task CompleteBidirectionalAsync(Task firstDirection, Task secondDirection, Action abort, TimeSpan? halfCloseDrainTimeout = null)
    {
        var firstCompleted = await Task.WhenAny(firstDirection, secondDirection);
        if (firstCompleted.IsFaulted || firstCompleted.IsCanceled)
        {
            abort();
        }
        else if (halfCloseDrainTimeout is { } timeout)
        {
            var remaining = ReferenceEquals(firstCompleted, firstDirection) ? secondDirection : firstDirection;
            using var timerCancellation = new CancellationTokenSource();
            var drainDeadline = Task.Delay(timeout, timerCancellation.Token);
            if (await Task.WhenAny(remaining, drainDeadline) != remaining) abort();
            timerCancellation.Cancel();
        }

        await Task.WhenAll(firstDirection, secondDirection);
    }

    public static async Task CopySocketToSocketAsync(Socket source, Socket destination, CancellationToken cancellationToken, Action<int>? bytesWritten = null, TimeSpan? blockedWriteTimeout = null)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            if (read == 0)
            {
                destination.Shutdown(SocketShutdown.Send);
                return;
            }

            var remaining = buffer.AsMemory(0, read);
            while (!remaining.IsEmpty)
            {
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeDeadline.CancelAfter(blockedWriteTimeout ?? TimeSpan.FromSeconds(120));
                var sent = await destination.SendAsync(remaining, SocketFlags.None, writeDeadline.Token);
                if (sent == 0) throw new IOException("Socket closed during relay send.");
                remaining = remaining[sent..];
                bytesWritten?.Invoke(sent);
            }
        }
    }

}
