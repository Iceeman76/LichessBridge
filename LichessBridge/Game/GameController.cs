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
    Task<GameCompletion> PlayAsync(string gameId, LichessAccount account, CancellationToken cancellationToken);
    bool NotifyGameFinished(string gameId);
    Task<ResignationRequestResult> RequestResignationAsync(CancellationToken cancellationToken);
}

public sealed record GameCompletion(int? OpponentFirstMoveTimeoutSeconds = null);

public enum ResignationRequestResult
{
    NoActiveGame,
    ConfirmationRequested,
    Resigned,
    Failed
}

public sealed class GameController(ILichessClient lichess, IChessEngineFactory engineFactory, IOptions<GamesOptions> gamesOptions, IOptions<ChatOptions> chatOptions, IStatusDisplay statusDisplay, ILogger<GameController> logger) : IGameController
{
    private const int MaximumEngineRestarts = 3;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> finishSignals = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim pgnFileLock = new(1, 1);
    private int resignationConfirmationRequested;

    public async Task<GameCompletion> PlayAsync(string gameId, LichessAccount account, CancellationToken cancellationToken)
    {
        using var finishSignal = new CancellationTokenSource();

        if (finishSignals.TryAdd(gameId, finishSignal) is false)
        {
            throw new InvalidOperationException($"Game '{gameId}' is already active.");
        }

        try
        {
            return await PlayGameAsync(gameId, account, finishSignal.Token, cancellationToken);
        }
        finally
        {
            finishSignals.TryRemove(gameId, out _);
            Interlocked.Exchange(ref resignationConfirmationRequested, 0);
        }
    }

    public async Task<ResignationRequestResult> RequestResignationAsync(CancellationToken cancellationToken)
    {
        var gameId = finishSignals.Keys.FirstOrDefault();

        if (gameId is null)
        {
            Interlocked.Exchange(ref resignationConfirmationRequested, 0);

            return ResignationRequestResult.NoActiveGame;
        }

        if (Interlocked.Exchange(ref resignationConfirmationRequested, 1) is 0)
        {
            return ResignationRequestResult.ConfirmationRequested;
        }

        Interlocked.Exchange(ref resignationConfirmationRequested, 0);
        try
        {
            await lichess.ResignGameAsync(gameId, cancellationToken);
            logger.LogInformation("Resigned game {GameId} by console request", gameId);

            return ResignationRequestResult.Resigned;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not resign game {GameId}", gameId);

            return ResignationRequestResult.Failed;
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

    private async Task<GameCompletion> PlayGameAsync(string gameId, LichessAccount account, CancellationToken gameFinishedToken, CancellationToken cancellationToken)
    {
        var session = new GameSession();
        var restartCount = 0;

        while (session.Result is null)
        {
            await using var engine = engineFactory.Create();
            engine.AnalysisUpdated += statusDisplay.ShowAnalysis;
            engine.SearchStatusUpdated += statusDisplay.ShowSearchStatus;
            engine.HintUpdated += statusDisplay.ShowHint;

            try
            {
                await engine.StartAsync(cancellationToken);

                if (restartCount > 0)
                {
                    logger.LogInformation("Chess engine restarted successfully on attempt {Attempt}; resuming game {GameId}", restartCount, gameId);
                }

                await ProcessGameStreamAsync(gameId, account, engine, session, gameFinishedToken, cancellationToken);

                if (session.Result is not null)
                {
                    await CompleteGameAsync(gameId, engine, session.Result, cancellationToken);
                }

                return new GameCompletion(session.OpponentFirstMoveTimedOut ? gamesOptions.Value.AbortIfNoMoveSeconds : null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && engine.IsRunning is false && restartCount < MaximumEngineRestarts)
            {
                restartCount++;
                session.LastActedPly = -1;
                session.LastPonderedPly = -1;
                statusDisplay.ShowAnalysis(null);
                statusDisplay.ShowSearchStatus(null);
                statusDisplay.ShowHint(null);
                var delay = TimeSpan.FromSeconds(Math.Pow(2, restartCount - 1));
                logger.LogWarning(exception, "Chess engine stopped during game {GameId}; restart {Attempt}/{Maximum} in {Delay}", gameId, restartCount, MaximumEngineRestarts, delay);
                statusDisplay.Show($"Engine stopped. Restarting ({restartCount}/{MaximumEngineRestarts})...");
                await Task.Delay(delay, cancellationToken);
            }
        }

        return new GameCompletion(session.OpponentFirstMoveTimedOut ? gamesOptions.Value.AbortIfNoMoveSeconds : null);
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
            var moveNext = events.MoveNextAsync().AsTask();

            if (session.FirstMoveDeadline is { } deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;

                if (remaining <= TimeSpan.Zero || ReferenceEquals(await Task.WhenAny(moveNext, Task.Delay(remaining, cancellationToken)), moveNext) is false)
                {
                    session.FirstMoveDeadline = null;
                    session.OpponentFirstMoveTimedOut = true;
                    logger.LogInformation("Aborting game {GameId} because the opponent did not make the first move within {Timeout} seconds", gameId, gamesOptions.Value.AbortIfNoMoveSeconds);
                    statusDisplay.Show("Opponent did not move; aborting game...");
                    await lichess.AbortGameAsync(gameId, cancellationToken);
                }
            }

            if (await moveNext is false)
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
        if (HasOpponentMoved(gameState.Moves, botColor))
        {
            session.FirstMoveDeadline = null;
        }

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

        if (botToMove is false)
        {
            var opponent = botColor == ChessColor.White ? game.BlackName : game.WhiteName;
            StartOpponentFirstMoveTimeout(session, gameState.Moves, botColor);
            statusDisplay.Show($"Waiting for {opponent}...");

            if (session.LastPonderedPly != gameState.Moves.Count)
            {
                await engine.SetPositionAsync(gameState.Moves, game.InitialSeconds, game.IncrementSeconds, cancellationToken);
                await engine.StartPonderingAsync(cancellationToken);
                session.LastPonderedPly = gameState.Moves.Count;
            }

            return;
        }

        if (session.LastActedPly == gameState.Moves.Count)
        {
            var opponent = botColor == ChessColor.White ? game.BlackName : game.WhiteName;
            StartOpponentFirstMoveTimeout(session, gameState.Moves, botColor);
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
            StartOpponentFirstMoveTimeout(session, gameState.Moves.Append(move).ToArray(), botColor);
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
            var path = Path.GetFullPath(gamesOptions.Value.PgnPath);
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The PGN directory could not be determined.");
            Directory.CreateDirectory(directory);
            var entry = $"{pgn.TrimEnd()}{Environment.NewLine}{Environment.NewLine}";
            await pgnFileLock.WaitAsync(cancellationToken);
            try
            {
                await File.AppendAllTextAsync(path, entry, cancellationToken);
            }
            finally
            {
                pgnFileLock.Release();
            }

            logger.LogInformation("Appended game {GameId} to PGN file {Path}", gameId, path);
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

    private void StartOpponentFirstMoveTimeout(GameSession session, IReadOnlyList<ChessMove> moves, ChessColor botColor)
    {
        if (gamesOptions.Value.AbortIfNoMoveSeconds == 0 || HasOpponentMoved(moves, botColor))
        {
            return;
        }

        var opponentToMove = (moves.Count % 2 == (botColor == ChessColor.White ? 0 : 1)) is false;

        if (opponentToMove)
        {
            session.FirstMoveDeadline ??= DateTimeOffset.UtcNow.AddSeconds(gamesOptions.Value.AbortIfNoMoveSeconds);
        }
    }

    private static bool HasOpponentMoved(IReadOnlyList<ChessMove> moves, ChessColor botColor) => botColor == ChessColor.White ? moves.Count >= 2 : moves.Count >= 1;

    private static void ValidateGame(GameFullEvent game)
    {
        if (string.Equals(game.InitialFen, "startpos", StringComparison.OrdinalIgnoreCase) is false)
        {
            throw new NotSupportedException("The MVP supports only the standard starting position.");
        }
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

        var activeColor = state.IsFinished ? (ChessColor?)null : state.Moves.Count % 2 is 0 ? ChessColor.White : ChessColor.Black;

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
        public int LastPonderedPly { get; set; } = -1;
        public int LastSeenPly { get; set; }
        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
        public bool StartMessageSent { get; set; }
        public DateTimeOffset? FirstMoveDeadline { get; set; }
        public bool OpponentFirstMoveTimedOut { get; set; }
    }
}
