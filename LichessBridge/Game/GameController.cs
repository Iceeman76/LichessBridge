using System.Collections.Concurrent;
using System.Globalization;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Engine;
using LichessBridge.Lichess;
using LichessBridge.Status;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LichessBridge.Game;

public interface IGameController
{
    Task PlayAsync(string gameId, LichessAccount account, CancellationToken cancellationToken);
    bool NotifyGameFinished(string gameId);
}

public sealed class GameController(ILichessClient lichess, IChessEngineFactory engineFactory, IOptions<GamesOptions> gamesOptions, IOptions<ChatOptions> chatOptions, IStatusDisplay statusDisplay, ILogger<GameController> logger) : IGameController
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> finishSignals = new(StringComparer.OrdinalIgnoreCase);

    public async Task PlayAsync(string gameId, LichessAccount account, CancellationToken cancellationToken)
    {
        using var finishSignal = new CancellationTokenSource();

        if (finishSignals.TryAdd(gameId, finishSignal) is false)
        {
            throw new InvalidOperationException($"Game '{gameId}' is already active.");
        }

        try
        {
            await PlayGameAsync(gameId, account, finishSignal.Token, cancellationToken);
        }
        finally
        {
            finishSignals.TryRemove(gameId, out _);
        }
    }

    public bool NotifyGameFinished(string gameId)
    {
        if (finishSignals.TryGetValue(gameId, out var finishSignal) is false)
        {
            return false;
        }

        try
        {
            finishSignal.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    private async Task PlayGameAsync(string gameId, LichessAccount account, CancellationToken gameFinishedToken, CancellationToken cancellationToken)
    {
        await using var engine = engineFactory.Create();
        engine.AnalysisUpdated += statusDisplay.ShowAnalysis;
        engine.SearchStatusUpdated += statusDisplay.ShowSearchStatus;
        engine.HintUpdated += statusDisplay.ShowHint;
        await engine.StartAsync(cancellationToken);
        var session = new GameSession();

        await ProcessGameStreamAsync(gameId, account, engine, session, gameFinishedToken, cancellationToken);

        if (session.Result is null)
        {
            return;
        }

        await CompleteGameAsync(gameId, engine, session.Result, cancellationToken);
    }

    private async Task ProcessGameStreamAsync(string gameId, LichessAccount account, IChessEngine engine, GameSession session, CancellationToken gameFinishedToken, CancellationToken cancellationToken)
    {
        while (session.Result is null && cancellationToken.IsCancellationRequested is false)
        {
            await ProcessConnectionAsync(gameId, account, engine, session, gameFinishedToken, cancellationToken);
        }
    }

    private async Task ProcessConnectionAsync(string gameId, LichessAccount account, IChessEngine engine, GameSession session, CancellationToken gameFinishedToken, CancellationToken cancellationToken)
    {
        await using var events = lichess.StreamGameAsync(gameId, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (session.Result is null && cancellationToken.IsCancellationRequested is false)
        {
            var streamEvent = await ReadEventAsync(events, gameId, session, cancellationToken);

            if (streamEvent is null)
            {
                return;
            }

            await ProcessEventAsync(gameId, account, engine, session, streamEvent, gameFinishedToken, cancellationToken);
        }
    }

    private async Task<GameStreamEvent?> ReadEventAsync(IAsyncEnumerator<GameStreamEvent> events, string gameId, GameSession session, CancellationToken cancellationToken)
    {
        try
        {
            if (await events.MoveNextAsync() is false)
            {
                throw new EndOfStreamException("Lichess game stream ended before the game finished.");
            }

            session.ReconnectDelay = TimeSpan.FromSeconds(1);

            return events.Current;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Game stream {GameId} disconnected; reconnecting in {Delay}", gameId, session.ReconnectDelay);
            await Task.Delay(session.ReconnectDelay, cancellationToken);
            session.ReconnectDelay = TimeSpan.FromSeconds(Math.Min(session.ReconnectDelay.TotalSeconds * 2, 30));

            return null;
        }
    }

    private async Task ProcessEventAsync(string gameId, LichessAccount account, IChessEngine engine, GameSession session, GameStreamEvent streamEvent, CancellationToken gameFinishedToken, CancellationToken cancellationToken)
    {
        if (streamEvent is GameFullEvent game)
        {
            StartGame(gameId, account, session, game);
            await SendChatMessageAsync(gameId, chatOptions.Value.StartMessage, session.StartMessageSent, cancellationToken);
            session.StartMessageSent = true;
        }

        if (session.Game is null)
        {
            return;
        }

        var gameState = GetGameState(streamEvent);

        if (gameState is null)
        {
            return;
        }

        var botColor = GetBotColor(session.Game, account.Id);
        LogNewMoves(gameState.Moves, session.LastSeenPly, botColor);
        session.LastSeenPly = Math.Max(session.LastSeenPly, gameState.Moves.Count);
        statusDisplay.ShowGame(CreateGameStatus(session.Game, gameState, account, botColor));

        if (gameState.IsFinished)
        {
            session.Result = CreateResult(gameState);
            statusDisplay.Show($"Game finished: {session.Result.Result} ({session.Result.Reason})");

            return;
        }

        await PlayMoveIfRequiredAsync(gameId, account, engine, session, gameState, botColor, gameFinishedToken, cancellationToken);
    }

    private void StartGame(string gameId, LichessAccount account, GameSession session, GameFullEvent game)
    {
        ValidateGame(game);
        session.Game = game;
        var botColor = GetBotColor(game, account.Id);
        var opponent = botColor == ChessColor.White ? game.BlackName : game.WhiteName;
        logger.LogInformation("Game {GameId} started: {Bot} plays {Color} against {Opponent}", gameId, account.Username, botColor, opponent);
        statusDisplay.Show($"Starting game against {opponent}...");
    }

    private async Task PlayMoveIfRequiredAsync(string gameId, LichessAccount account, IChessEngine engine, GameSession session, GameState gameState, ChessColor botColor, CancellationToken gameFinishedToken, CancellationToken cancellationToken)
    {
        var game = session.Game ?? throw new InvalidOperationException("The game metadata has not been loaded.");
        var botToMove = gameState.Moves.Count % 2 == (botColor == ChessColor.White ? 0 : 1);

        if (botToMove is false || session.LastActedPly == gameState.Moves.Count)
        {
            var opponent = botColor == ChessColor.White ? game.BlackName : game.WhiteName;
            statusDisplay.Show($"Waiting for {opponent}...");

            return;
        }

        session.LastActedPly = gameState.Moves.Count;
        statusDisplay.ShowHint(null);
        statusDisplay.ShowSearchStatus(null);
        statusDisplay.Show("Thinking...");
        using var moveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, gameFinishedToken);
        try
        {
            await engine.SetPositionAsync(gameState.Moves, game.InitialSeconds, game.IncrementSeconds, moveCancellation.Token);
            var clock = new ChessClock(gameState.WhiteTimeMilliseconds, gameState.BlackTimeMilliseconds);
            var move = await engine.GetMoveAsync(botColor, clock, moveCancellation.Token);
            logger.LogInformation("Game {GameId}: {Engine} plays {Move}", gameId, account.Username, move);
            await lichess.PlayMoveAsync(gameId, move, moveCancellation.Token);
            var opponentName = botColor == ChessColor.White ? game.BlackName : game.WhiteName;
            statusDisplay.Show($"Waiting for {opponentName}...");
        }
        catch (OperationCanceledException) when (gameFinishedToken.IsCancellationRequested && cancellationToken.IsCancellationRequested is false)
        {
            logger.LogDebug("Stopped calculating a move because game {GameId} finished", gameId);
        }
    }

    private async Task CompleteGameAsync(string gameId, IChessEngine engine, GameResult result, CancellationToken cancellationToken)
    {
        await ReportResultAsync(engine, result, cancellationToken);
        logger.LogInformation("Game {GameId} finished: {Result}. Reason: {Reason}", gameId, result.Result, result.Reason);
        await SendChatMessageAsync(gameId, chatOptions.Value.EndMessage, false, cancellationToken);
        await SavePgnAsync(gameId, cancellationToken);
        await engine.StopAsync(cancellationToken);
    }

    private async Task ReportResultAsync(IChessEngine engine, GameResult result, CancellationToken cancellationToken)
    {
        try
        {
            await engine.SendResultAsync(result, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not report game result to the engine");
        }
    }

    private async Task SavePgnAsync(string gameId, CancellationToken cancellationToken)
    {
        try
        {
            var pgn = await lichess.ExportPgnAsync(gameId, cancellationToken);
            var directory = Path.GetFullPath(gamesOptions.Value.PgnDirectory);
            Directory.CreateDirectory(directory);
            var path = CreatePgnPath(directory, gameId);
            await File.WriteAllTextAsync(path, pgn, cancellationToken);
            logger.LogInformation("Saved PGN to {Path}", path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not save PGN for game {GameId}", gameId);
        }
    }

    private async Task SendChatMessageAsync(string gameId, string message, bool alreadySent, CancellationToken cancellationToken)
    {
        if (alreadySent || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            await lichess.SendChatMessageAsync(gameId, message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not send chat message for game {GameId}", gameId);
        }
    }

    private void LogNewMoves(IReadOnlyList<ChessMove> moves, int firstPly, ChessColor botColor)
    {
        for (var ply = firstPly; ply < moves.Count; ply++)
        {
            var color = ply % 2 == 0 ? ChessColor.White : ChessColor.Black;
            var player = color == botColor ? "Engine" : "Opponent";
            logger.LogInformation("{Player}: {Move}", player, moves[ply]);
        }
    }

    private static GameState? GetGameState(GameStreamEvent streamEvent) => streamEvent switch
    {
        GameFullEvent game => game.State,
        GameStateEvent update => update.State,
        _ => null
    };

    private static ChessColor GetBotColor(GameFullEvent game, string accountId)
    {
        if (string.Equals(game.WhiteId, accountId, StringComparison.OrdinalIgnoreCase))
        {
            return ChessColor.White;
        }

        if (string.Equals(game.BlackId, accountId, StringComparison.OrdinalIgnoreCase))
        {
            return ChessColor.Black;
        }

        throw new InvalidDataException($"Bot account '{accountId}' is not a player in game '{game.GameId}'.");
    }

    private static void ValidateGame(GameFullEvent game)
    {
        if (string.Equals(game.InitialFen, "startpos", StringComparison.OrdinalIgnoreCase) is false)
        {
            throw new NotSupportedException("The MVP supports only the standard starting position.");
        }
    }

    private static string CreatePgnPath(string directory, string gameId)
    {
        var safeGameId = string.Concat(gameId.Where(char.IsLetterOrDigit));

        if (safeGameId.Length == 0)
        {
            throw new InvalidDataException("Game id cannot be used as a PGN filename.");
        }

        return Path.Combine(directory, $"{safeGameId}.pgn");
    }

    private static GameResult CreateResult(GameState state)
    {
        var result = state.Winner switch
        {
            ChessColor.White => "1-0",
            ChessColor.Black => "0-1",
            _ => "1/2-1/2"
        };

        return new GameResult(result, state.Status);
    }

    internal static string FormatTimeControl(int initialSeconds, int incrementSeconds)
    {
        var initial = initialSeconds % 60 == 0
            ? (initialSeconds / 60).ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{initialSeconds / 60}:{initialSeconds % 60:00}");

        return string.Create(CultureInfo.InvariantCulture, $"{initial}+{incrementSeconds}");
    }

    private static GameStatus CreateGameStatus(GameFullEvent game, GameState state, LichessAccount account, ChessColor botColor)
    {
        var accountRating = account.Ratings.FirstOrDefault(rating => rating.Speed.Equals(game.Speed, StringComparison.OrdinalIgnoreCase))?.Rating;
        var whiteRating = botColor == ChessColor.White ? game.WhiteRating ?? accountRating : game.WhiteRating;
        var blackRating = botColor == ChessColor.Black ? game.BlackRating ?? accountRating : game.BlackRating;

        ChessColor? activeColor = state.IsFinished ? null : state.Moves.Count % 2 == 0 ? ChessColor.White : ChessColor.Black;

        return new GameStatus(game.GameId, game.Rated ? "rated" : "casual", game.Speed, FormatTimeControl(game.InitialSeconds, game.IncrementSeconds), game.WhiteName, game.WhiteTitle, whiteRating, game.BlackName, game.BlackTitle, blackRating, FormatLastMove(state.Moves), state.WhiteTimeMilliseconds, state.BlackTimeMilliseconds, activeColor);
    }

    internal static string FormatLastMove(IReadOnlyList<ChessMove> moves)
    {
        if (moves.Count == 0)
        {
            return "-";
        }

        var moveNumber = (moves.Count + 1) / 2;
        var separator = moves.Count % 2 == 0 ? " ..." : " ";
        var san = SanMoveConverter.ConvertLastMoveToSan(moves);

        return $"{moveNumber}.{separator}{san}";
    }

    private sealed class GameSession
    {
        public GameFullEvent? Game { get; set; }
        public GameResult? Result { get; set; }
        public int LastActedPly { get; set; } = -1;
        public int LastSeenPly { get; set; }
        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
        public bool StartMessageSent { get; set; }
    }
}
