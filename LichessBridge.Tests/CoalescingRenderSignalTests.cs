using Bridge;

namespace LichessBridge.Tests;

public sealed class CoalescingRenderSignalTests
{
    [Test]
    public async Task ConcurrentRequestsAreCoalescedWithoutOverflowing()
    {
        using var signal = new CoalescingRenderSignal();

        Assert.DoesNotThrow(() =>
            Parallel.For(0, 100_000, _ => signal.Request()));

        await signal.WaitAsync(CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await signal.WaitAsync(cancellation.Token));
    }

    [Test]
    public async Task CanBeRequestedAgainAfterSignalIsConsumed()
    {
        using var signal = new CoalescingRenderSignal();

        signal.Request();
        await signal.WaitAsync(CancellationToken.None);

        signal.Request();
        await signal.WaitAsync(CancellationToken.None);
    }
}
