namespace DotNetLab.LichessBridge.Status;

public interface IQuitController
{
    bool IsRequested { get; }
    event Action<bool>? Changed;
    void Toggle();
}

public sealed class QuitController : IQuitController
{
    private int requested;

    public bool IsRequested => Volatile.Read(ref requested) is 1;
    public event Action<bool>? Changed;

    public void Toggle()
    {
        while (true)
        {
            var current = Volatile.Read(ref requested);
            var updated = current is 0 ? 1 : 0;

            if (Interlocked.CompareExchange(ref requested, updated, current) == current)
            {
                Changed?.Invoke(updated is 1);

                return;
            }
        }
    }
}
