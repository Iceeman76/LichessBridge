using DotNetLab.LichessBridge.Diagnostics;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Lichess;

namespace DotNetLab.LichessBridge.Tests;

public sealed class ServerClockTrackerTests
{
    [TestCase(ChessColor.White, 0, 950, 50)]
    [TestCase(ChessColor.Black, 2000, 2950, 50)]
    [TestCase(ChessColor.White, 2000, 3100, -100)]
    public void CalculatesServerConsumptionIncludingIncrementWithoutClamping(ChessColor color, int increment, int remaining, long expected)
    {
        var tracker = new ServerClockTracker();
        var before = State(color is ChessColor.White ? [] : ["e2e4"], 1000, 1000);
        var move = new ChessMove(color is ChessColor.White ? "e2e4" : "e7e5");
        tracker.Track(before, color, move, increment, "attempt");
        var after = new GameState([.. before.Moves, move], color is ChessColor.White ? remaining : 1000, color is ChessColor.Black ? remaining : 1000, "started", null);

        var measurement = tracker.Observe(after).Single();

        Assert.Multiple(() =>
        {
            Assert.That(measurement.ConsumedMilliseconds, Is.EqualTo(expected));
            Assert.That(measurement.CorrelationId, Is.EqualTo("attempt"));
            Assert.That(measurement.Ply, Is.EqualTo(before.Moves.Count + 1));
            Assert.That(measurement.Move, Is.EqualTo(move));
            Assert.That(tracker.Observe(after), Is.Empty);
        });
    }

    [Test]
    public void IgnoresOldSnapshotsAndDuplicateTrackingAcrossReconnect()
    {
        var tracker = new ServerClockTracker();
        var before = State(["e2e4"], 900, 1000);
        tracker.Track(before, ChessColor.Black, new ChessMove("e7e5"), 100, "original");
        tracker.Track(before, ChessColor.Black, new ChessMove("e7e5"), 100, "retry");

        Assert.That(tracker.Observe(before), Is.Empty);
        var measurement = tracker.Observe(State(["e2e4", "e7e5", "g1f3"], 800, 1050)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(measurement.CorrelationId, Is.EqualTo("original"));
            Assert.That(measurement.ConsumedMilliseconds, Is.EqualTo(50));
            Assert.That(tracker.Observe(before), Is.Empty);
            Assert.That(tracker.Observe(State(["e2e4", "e7e5"], 900, 1050)), Is.Empty);
        });
    }

    [TestCase("d2d4")]
    [TestCase("e2e4 e7e5 g1f3")]
    public void ReportsUnavailableWhenHistoryDiffersOrAnotherOwnMoveWasMissed(string moves)
    {
        var tracker = new ServerClockTracker();
        tracker.Track(State([], 1000, 1000), ChessColor.White, new ChessMove("e2e4"), 0, "attempt");

        var measurement = tracker.Observe(State(moves.Split(' '), 800, 900)).Single();

        Assert.That(measurement.ConsumedMilliseconds, Is.Null);
    }

    [Test]
    public void KeepsGamesIndependent()
    {
        var firstGame = new ServerClockTracker();
        var secondGame = new ServerClockTracker();
        firstGame.Track(State([], 1000, 1000), ChessColor.White, new ChessMove("e2e4"), 0, "first");
        secondGame.Track(State([], 2000, 2000), ChessColor.White, new ChessMove("e2e4"), 0, "second");

        Assert.Multiple(() =>
        {
            Assert.That(firstGame.Observe(State(["e2e4"], 950, 1000)).Single().ConsumedMilliseconds, Is.EqualTo(50));
            Assert.That(secondGame.Observe(State(["e2e4"], 1950, 2000)).Single().CorrelationId, Is.EqualTo("second"));
        });
    }

    private static GameState State(string[] moves, int white, int black) => new(moves.Select(move => new ChessMove(move)).ToArray(), white, black, "started", null);
}
