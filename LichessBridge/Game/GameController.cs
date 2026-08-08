using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Engine;
using LichessBridge.Lichess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LichessBridge.Game;

public interface IGameController
{
    Task PlayAsync(string gameId, LichessAccount account, CancellationToken cancellationToken);
}

public sealed class GameController(
    ILichessClient lichess,
    IChessEngineFactory engineFactory,
    IOptions<GamesOptions> gamesOptions,
    ILogger<GameController> logger) : IGameController
{
    public async Task PlayAsync(string gameId, LichessAccount account, CancellationToken cancellationToken)
    {
        await using var engine = engineFactory.Create();
        await engine.StartAsync(cancellationToken);

        GameFullEvent? game = null;
        var lastActedPly = -1;
        var lastSeenPly = 0;
        var retry = TimeSpan.FromSeconds(1);
        GameResult? finalResult = null;

        while (finalResult is null && !cancellationToken.IsCancellationRequested)
        {
            await using var enumerator = lichess.StreamGameAsync(gameId, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (finalResult is null && !cancellationToken.IsCancellationRequested)
            {
                GameStreamEvent streamEvent;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        throw new EndOfStreamException("Lichess game stream ended before the game finished.");
                    streamEvent = enumerator.Current;
                    retry = TimeSpan.FromSeconds(1);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Game stream {GameId} disconnected; reconnecting in {Delay}", gameId, retry);
                    await Task.Delay(retry, cancellationToken);
                    retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, 30));
                    break;
                }

                if (streamEvent is GameFullEvent full)
                {
                    ValidateGame(full);
                    game = full;
                    var color = GetBotColor(full, account.Id);
                    logger.LogInformation(
                        "Game {GameId} started: {Bot} plays {Color} against {Opponent}",
                        gameId,
                        account.Username,
                        color,
                        color == ChessColor.White ? full.BlackName : full.WhiteName);
                }

                if (game is null)
                    continue;

                var state = streamEvent switch
                {
                    GameFullEvent initial => initial.State,
                    GameStateEvent update => update.State,
                    _ => null
                };
                if (state is null)
                    continue;

                var botColor = GetBotColor(game, account.Id);
                LogNewMoves(state.Moves, lastSeenPly, botColor);
                lastSeenPly = Math.Max(lastSeenPly, state.Moves.Count);

                if (state.IsFinished)
                {
                    finalResult = ToResult(state);
                    break;
                }

                var botToMove = state.Moves.Count % 2 == (botColor == ChessColor.White ? 0 : 1);
                if (!botToMove || lastActedPly == state.Moves.Count)
                    continue;

                lastActedPly = state.Moves.Count;
                await engine.SetPositionAsync(
                    state.Moves,
                    game.InitialSeconds,
                    game.IncrementSeconds,
                    cancellationToken);
                var move = await engine.GetMoveAsync(
                    botColor,
                    new ChessClock(state.WhiteTimeMilliseconds, state.BlackTimeMilliseconds),
                    cancellationToken);
                logger.LogInformation("Game {GameId}: {Engine} plays {Move}", gameId, account.Username, move);
                await lichess.PlayMoveAsync(gameId, move, cancellationToken);
            }
        }

        if (finalResult is null)
            return;

        try
        {
            await engine.SendResultAsync(finalResult, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not report game result to the engine");
        }
        logger.LogInformation(
            "Game {GameId} finished: {Result}. Reason: {Reason}",
            gameId,
            finalResult.Result,
            finalResult.Reason);
        await SavePgnAsync(gameId, cancellationToken);
        await engine.StopAsync(cancellationToken);
    }

    private async Task SavePgnAsync(string gameId, CancellationToken cancellationToken)
    {
        try
        {
            var pgn = await lichess.ExportPgnAsync(gameId, cancellationToken);
            var directory = Path.GetFullPath(gamesOptions.Value.PgnDirectory);
            Directory.CreateDirectory(directory);
            var safeId = string.Concat(gameId.Where(char.IsLetterOrDigit));
            if (safeId.Length == 0)
                throw new InvalidDataException("Game id cannot be used as a PGN filename.");
            var path = Path.Combine(directory, $"{safeId}.pgn");
            await File.WriteAllTextAsync(path, pgn, cancellationToken);
            logger.LogInformation("Saved PGN to {Path}", path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not save PGN for game {GameId}", gameId);
        }
    }

    private void LogNewMoves(IReadOnlyList<ChessMove> moves, int fromPly, ChessColor botColor)
    {
        for (var ply = fromPly; ply < moves.Count; ply++)
        {
            var color = ply % 2 == 0 ? ChessColor.White : ChessColor.Black;
            var player = color == botColor ? "Engine" : "Opponent";
            logger.LogInformation("{Player}: {Move}", player, moves[ply]);
        }
    }

    private static ChessColor GetBotColor(GameFullEvent game, string accountId)
    {
        if (string.Equals(game.WhiteId, accountId, StringComparison.OrdinalIgnoreCase))
            return ChessColor.White;
        if (string.Equals(game.BlackId, accountId, StringComparison.OrdinalIgnoreCase))
            return ChessColor.Black;
        throw new InvalidDataException($"Bot account '{accountId}' is not a player in game '{game.GameId}'.");
    }

    private static void ValidateGame(GameFullEvent game)
    {
        if (!string.Equals(game.InitialFen, "startpos", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The MVP supports only the standard starting position.");
    }

    private static GameResult ToResult(GameState state)
    {
        var result = state.Winner switch
        {
            ChessColor.White => "1-0",
            ChessColor.Black => "0-1",
            _ => "1/2-1/2"
        };
        return new GameResult(result, state.Status);
    }
}
