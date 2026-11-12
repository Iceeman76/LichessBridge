using DotNetLab.LichessBridge.Engine;

namespace DotNetLab.LichessBridge.Status;

public sealed class GameStatusFormatter
{
    private SanMoveConverter converter = new();
    private GameStatus? previous;
    private GameStatus? formatted;

    public GameStatus Format(GameStatus game)
    {
        if (ReferenceEquals(game, previous))
        {
            return formatted!;
        }

        if (game.MoveHistory is not { } moves)
        {
            return game;
        }

        if (previous?.GameId is { } previousGameId && previousGameId.Equals(game.GameId, StringComparison.Ordinal) is false)
        {
            converter = new SanMoveConverter();
        }

        var san = converter.ConvertLastMoveToSan(moves);
        var moveNumber = (moves.Count + 1) / 2;
        var separator = moves.Count % 2 is 0 ? " ..." : " ";
        formatted = game with { LastMove = moves.Count is 0 ? "-" : $"{moveNumber}.{separator}{san}" };
        previous = game;

        return formatted;
    }
}
