namespace Bridge;

internal sealed class CoalescingRenderSignal : IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private int pending;

    public void Request()
    {
        if (Interlocked.Exchange(ref pending, 1) != 0)
        {
            return;
        }

        try
        {
            signal.Release();
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref pending, 0);
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await signal.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref pending, 0);
    }

    public bool TryConsume()
    {
        if (signal.Wait(0) is false)
        {
            return false;
        }

        Interlocked.Exchange(ref pending, 0);

        return true;
    }

    public void Dispose() => signal.Dispose();
}
