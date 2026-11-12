using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Domain;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Status;

public sealed class ConsoleOutputPolicy(IOptions<ConsoleOutputOptions> options, TimeProvider timeProvider)
{
    private ClockObservation? observation;

    public bool IsQuiet
    {
        get
        {
            while (Volatile.Read(ref observation) is { } current)
            {
                if (current.Quiet || RemainingMilliseconds(current) > options.Value.ConsoleQuietBelowMilliseconds || options.Value.ConsoleQuietBelowMilliseconds is 0)
                {
                    return current.Quiet;
                }

                if (ReferenceEquals(Interlocked.CompareExchange(ref observation, current with { Quiet = true }, current), current))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Update(GameClockStatus? clock)
    {
        if (clock is null || clock.IsFinished || options.Value.ConsoleQuietBelowMilliseconds is 0)
        {
            Interlocked.Exchange(ref observation, null);

            return;
        }

        while (true)
        {
            var previous = Volatile.Read(ref observation);
            var continuingGame = previous?.Clock.GameId == clock.GameId;
            var quiet = continuingGame && (previous!.Quiet || RemainingMilliseconds(previous) <= options.Value.ConsoleQuietBelowMilliseconds);
            var current = new ClockObservation(clock, timeProvider.GetTimestamp(), quiet);
            var remaining = RemainingMilliseconds(current);
            quiet = quiet ? remaining <= options.Value.ConsoleResumeAboveMilliseconds : remaining <= options.Value.ConsoleQuietBelowMilliseconds;
            current = current with { Quiet = quiet };

            if (ReferenceEquals(Interlocked.CompareExchange(ref observation, current, previous), previous))
            {
                return;
            }
        }
    }

    private double RemainingMilliseconds(ClockObservation current)
    {
        var elapsed = current.Clock.ActiveColor == current.Clock.BotColor ? current.Clock.ReceivedAgo.TotalMilliseconds + timeProvider.GetElapsedTime(current.Timestamp).TotalMilliseconds : 0;

        return current.Clock.Clock.For(current.Clock.BotColor) - elapsed;
    }

    private sealed record ClockObservation(GameClockStatus Clock, long Timestamp, bool Quiet);
}

public sealed record GameClockStatus(string GameId, ChessColor BotColor, ChessClock Clock, ChessColor? ActiveColor, bool IsFinished)
{
    public TimeSpan ReceivedAgo { get; init; }
}
