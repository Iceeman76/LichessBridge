namespace LichessBridge.Status;

public interface IAutoChallengeController
{
    bool IsEnabled { get; }
    event Action<bool>? Changed;
    event Action? ChallengeRequested;
    void Toggle();
    void RequestChallenge();
}

public sealed class AutoChallengeController(bool enabled) : IAutoChallengeController
{
    private int state = enabled ? 1 : 0;

    public bool IsEnabled => Volatile.Read(ref state) is 1;
    public event Action<bool>? Changed;
    public event Action? ChallengeRequested;

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

    public void RequestChallenge()
    {
        if (IsEnabled)
        {
            ChallengeRequested?.Invoke();
        }
    }
}
