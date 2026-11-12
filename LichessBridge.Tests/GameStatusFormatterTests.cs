using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Status;

namespace DotNetLab.LichessBridge.Tests;

public sealed class GameStatusFormatterTests
{
    [Test]
    public void PreservesClockUpdatesWhenHistoryIsUnchanged()
    {
        var formatter = new GameStatusFormatter();
        var status = CreateStatus("game", "e2e4 e7e5");
        formatter.Format(status);

        var updated = formatter.Format(status with { BlackTimeMilliseconds = 42_000 });

        Assert.Multiple(() =>
        {
            Assert.That(updated.LastMove, Is.EqualTo("1. ...e5"));
            Assert.That(updated.BlackTimeMilliseconds, Is.EqualTo(42_000));
        });
    }

    [Test]
    public void CatchesUpAfterPausedRenderingAndHandlesNewGame()
    {
        var formatter = new GameStatusFormatter();
        formatter.Format(CreateStatus("first", "e2e4"));

        var caughtUp = formatter.Format(CreateStatus("first", "e2e4 e7e5 g1f3 b8c6 f1c4 g8f6 e1g1"));
        var nextGame = formatter.Format(CreateStatus("second", "d2d4 d7d5"));

        Assert.Multiple(() =>
        {
            Assert.That(caughtUp.LastMove, Is.EqualTo("4. O-O"));
            Assert.That(nextGame.LastMove, Is.EqualTo("1. ...d5"));
        });
    }

    private static GameStatus CreateStatus(string gameId, string history) => new(gameId, "casual", "blitz", "5+0", "Bot", null, null, "Opponent", null, null, "", 60_000, 60_000, ChessColor.White) { MoveHistory = history.Split(' ').Select(value => new ChessMove(value)).ToArray() };
}
