using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Lichess;

namespace DotNetLab.LichessBridge.Diagnostics;

public sealed class ServerClockTracker
{
    private readonly Dictionary<int, PendingMove> pending = [];

    public void Track(GameState before, ChessColor color, ChessMove move, int increment, string correlationId)
    {
        var ply = before.Moves.Count + 1;
        pending.TryAdd(ply, new PendingMove([.. before.Moves, move], color, color is ChessColor.White ? before.WhiteTimeMilliseconds : before.BlackTimeMilliseconds, increment, correlationId));
    }

    public IReadOnlyList<ServerClockMeasurement> Observe(GameState state)
    {
        var measurements = new List<ServerClockMeasurement>();

        foreach (var (ply, move) in pending.ToArray())
        {
            if (state.Moves.Count < ply)
            {
                continue;
            }

            pending.Remove(ply);
            var matches = move.Moves.SequenceEqual(state.Moves.Take(ply));
            var clockAvailable = matches && state.Moves.Count <= ply + 1;
            var after = move.Color is ChessColor.White ? state.WhiteTimeMilliseconds : state.BlackTimeMilliseconds;
            var consumed = clockAvailable ? (long?)move.BeforeMilliseconds + move.IncrementMilliseconds - after : null;
            measurements.Add(new ServerClockMeasurement(ply, move.Moves[^1], move.CorrelationId, consumed, move.BeforeMilliseconds, after, move.IncrementMilliseconds, state.Moves.Count));
        }

        return measurements;
    }

    private sealed record PendingMove(IReadOnlyList<ChessMove> Moves, ChessColor Color, int BeforeMilliseconds, int IncrementMilliseconds, string CorrelationId);
}

public sealed record ServerClockMeasurement(int Ply, ChessMove Move, string CorrelationId, long? ConsumedMilliseconds, int BeforeMilliseconds, int AfterMilliseconds, int IncrementMilliseconds, int ObservedPly);
