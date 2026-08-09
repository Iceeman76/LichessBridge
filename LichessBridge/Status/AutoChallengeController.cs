namespace LichessBridge.Status;

public interface IAutoChallengeController
{
    bool IsEnabled { get; }
    event Action<bool>? Changed;
    void Toggle();
}

public sealed class AutoChallengeController(bool enabled) : IAutoChallengeController
{
    private int state = enabled ? 1 : 0;

    public bool IsEnabled => Volatile.Read(ref state) is 1;
    public event Action<bool>? Changed;

    public void Toggle()
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            var updated = current is 0 ? 1 : 0;

            if (Interlocked.CompareExchange(ref state, updated, current) == current)
            {
                Changed?.Invoke(updated is 1);

                return;
            }
        }
    }
}
