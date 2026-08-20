using System.Runtime.CompilerServices;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Engine;
using LichessBridge.Game;
using LichessBridge.Lichess;
using LichessBridge.Status;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LichessBridge.Tests;

public sealed class GameControllerTests
{
    private string pgnDirectory = string.Empty;
    private string pgnPath = string.Empty;

    [SetUp]
    public void SetUp()
    {
        pgnDirectory = Path.Combine(Path.GetTempPath(), $"LichessBridge-{Guid.NewGuid():N}");
        pgnPath = Path.Combine(pgnDirectory, "games.pgn");
        Directory.CreateDirectory(pgnDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(pgnDirectory, recursive: true);
    }

    [TestCase(120, 1, "2+1")]
    [TestCase(300, 3, "5+3")]
    [TestCase(90, 1, "1:30+1")]
    public void FormatsTimeControl(int initialSeconds, int incrementSeconds, string expected)
    {
        Assert.That(GameController.FormatTimeControl(initialSeconds, incrementSeconds), Is.EqualTo(expected));
    }

    [TestCase(60_000, true, 1500, 58_500)]
    [TestCase(60_000, false, 1500, 60_000)]
    [TestCase(1000, true, 2000, 0)]
    public void AdjustsOnlyActiveClock(int milliseconds, bool active, int elapsedMilliseconds, int expected)
    {
        var adjusted = StatusFormatter.AdjustClock(milliseconds, active, TimeSpan.FromMilliseconds(elapsedMilliseconds));

        Assert.That(adjusted, Is.EqualTo(expected));
    }

    [TestCase(new string[] { }, "-")]
    [TestCase(new[] { "e2e4" }, "1. e4")]
    [TestCase(new[] { "e2e4", "e7e5" }, "1. ...e5")]
    [TestCase(new[] { "e2e4", "d7d5", "e4d5" }, "2. exd5")]
    public void FormatsLastMove(string[] moveValues, string expected)
    {
        var moves = moveValues.Select(value => new ChessMove(value)).ToArray();

        Assert.That(GameController.FormatLastMove(moves), Is.EqualTo(expected));
    }

    [Test]
    public async Task PlaysWhiteGameReportsResultAndSavesPgn()
    {
        var lichess = new FakeLichessClient(CreateWhiteGame());
        var engine = new FakeEngine([new ChessMove("e2e4"), new ChessMove("g1f3")]);
        var controller = CreateController(lichess, engine);

        await controller.PlayAsync("game1", CreateAccount(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(lichess.PlayedMoves, Is.EqualTo([new ChessMove("e2e4"), new ChessMove("g1f3")]));
            Assert.That(engine.Positions, Has.Count.EqualTo(2));
            Assert.That(engine.Result?.Result, Is.EqualTo("1-0"));
            Assert.That(engine.Stopped, Is.True);
            Assert.That(lichess.ChatMessages, Is.EqualTo(["Good luck!", "Good game!"]));
            Assert.That(File.ReadAllText(pgnPath), Is.EqualTo($"[Result \"1-0\"]{Environment.NewLine}{Environment.NewLine}"));
        });
    }

    [Test]
    public async Task PlaysAsBlackAfterOpponentsOpeningMove()
    {
        var lichess = new FakeLichessClient(CreateBlackGame());
        var engine = new FakeEngine([new ChessMove("e7e5")]);
        var controller = CreateController(lichess, engine);

        await controller.PlayAsync("game2", CreateAccount(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(lichess.PlayedMoves, Is.EqualTo([new ChessMove("e7e5")]));
            Assert.That(engine.Result?.Result, Is.EqualTo("0-1"));
        });
    }

    [Test]
    public async Task RestartsEngineAndResumesCurrentGameWhenProcessStops()
    {
        var lichess = new FakeLichessClient(CreateWhiteGame());
        var replacement = new FakeEngine([new ChessMove("e2e4"), new ChessMove("g1f3")]);
        var engines = new Queue<IChessEngine>([new StoppedEngine(), replacement]);
        var controller = new GameController(lichess, new SequenceEngineFactory(engines), Options.Create(new GamesOptions { PgnPath = pgnPath }), Options.Create(new ChatOptions()), new RecordingStatusDisplay(), NullLogger<GameController>.Instance);

        await controller.PlayAsync("game1", CreateAccount(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(engines, Is.Empty);
            Assert.That(lichess.PlayedMoves, Is.EqualTo([new ChessMove("e2e4"), new ChessMove("g1f3")]));
            Assert.That(replacement.Result?.Result, Is.EqualTo("1-0"));
        });
    }

    [Test]
    public async Task StartsPonderingWhenRestoredGameIsWaitingForOpponent()
    {
        var state = CreateState(["e2e4"], 298_000, 300_000);
        var lichess = new FakeLichessClient([
            CreateFullGame(state: state),
            new GameStateEvent(CreateState(["e2e4"], 298_000, 300_000, "draw"))
        ]);
        var engine = new FakeEngine([]);
        var controller = CreateController(lichess, engine);

        await controller.PlayAsync("restored-game", CreateAccount(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Positions, Has.Count.EqualTo(1));
            Assert.That(engine.Positions[0], Is.EqualTo([new ChessMove("e2e4")]));
            Assert.That(engine.PonderCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AbortsWhenOpponentDoesNotMakeFirstMove()
    {
        var lichess = new FakeLichessClient([CreateFullGame(whiteId: "opponent", blackId: "alarm-bot")]) { FinishAfterAbort = true };
        var controller = CreateController(lichess, new FakeEngine([]), 1);

        var completion = await controller.PlayAsync("game", CreateAccount(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(lichess.AbortedGames, Is.EqualTo(["game"]));
            Assert.That(completion.OpponentFirstMoveTimeoutSeconds, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AbortsWhenBlackOpponentDoesNotReplyToOpeningMove()
    {
        var lichess = new FakeLichessClient([CreateFullGame()]) { FinishAfterAbort = true };
        var controller = CreateController(lichess, new FakeEngine([new ChessMove("e2e4")]), 1);

        var completion = await controller.PlayAsync("game", CreateAccount(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(lichess.PlayedMoves, Is.EqualTo([new ChessMove("e2e4")]));
            Assert.That(lichess.AbortedGames, Is.EqualTo(["game"]));
            Assert.That(completion.OpponentFirstMoveTimeoutSeconds, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RequiresTwoRequestsBeforeResigningActiveGame()
    {
        var lichess = new FakeLichessClient([CreateFullGame()]) { WaitAfterEvents = true };
        var controller = CreateController(lichess, new FakeEngine([new ChessMove("e2e4")]));
        using var cancellation = new CancellationTokenSource();
        var game = controller.PlayAsync("game", CreateAccount(), cancellation.Token);

        var firstRequest = await controller.RequestResignationAsync(CancellationToken.None);
        var secondRequest = await controller.RequestResignationAsync(CancellationToken.None);
        await cancellation.CancelAsync();

        Assert.Multiple(() =>
        {
            Assert.That(firstRequest, Is.EqualTo(ResignationRequestResult.ConfirmationRequested));
            Assert.That(secondRequest, Is.EqualTo(ResignationRequestResult.Resigned));
            Assert.That(lichess.ResignedGames, Is.EqualTo(["game"]));
        });

        Assert.CatchAsync<OperationCanceledException>(async () => await game);
    }

    [Test]
    public void RejectsNonStandardStartingPosition()
    {
        var lichess = new FakeLichessClient([CreateFullGame(initialFen: "8/8/8/8/8/8/8/8 w - - 0 1")]);
        var controller = CreateController(lichess, new FakeEngine([]));

        Assert.ThrowsAsync<NotSupportedException>(() => controller.PlayAsync("game", CreateAccount(), CancellationToken.None));
    }

    [Test]
    public void RejectsGameThatDoesNotContainBotAccount()
    {
        var lichess = new FakeLichessClient([CreateFullGame(whiteId: "other-white", blackId: "other-black")]);
        var controller = CreateController(lichess, new FakeEngine([]));

        Assert.ThrowsAsync<InvalidDataException>(() => controller.PlayAsync("game", CreateAccount(), CancellationToken.None));
    }

    [Test]
    public async Task ReportsDrawAndDoesNotFailWhenPgnExportFails()
    {
        var lichess = new FakeLichessClient([CreateFullGame(state: CreateState([], 1_000, 1_000, "draw"))]) { PgnException = new HttpRequestException("export failed") };
        var engine = new FakeEngine([]);
        var controller = CreateController(lichess, engine);

        await controller.PlayAsync("game", CreateAccount(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Result?.Result, Is.EqualTo("1/2-1/2"));
            Assert.That(engine.Stopped, Is.True);
        });
    }

    [Test]
    public async Task AppendsMultipleGamesToSinglePgnFile()
    {
        var lichess = new FakeLichessClient([CreateFullGame(state: CreateState([], 1_000, 1_000, "draw"))]);
        var controller = CreateController(lichess, new FakeEngine([]));

        await controller.PlayAsync("game1", CreateAccount(), CancellationToken.None);
        await controller.PlayAsync("game2", CreateAccount(), CancellationToken.None);

        var entry = $"[Result \"1-0\"]{Environment.NewLine}{Environment.NewLine}";
        Assert.That(File.ReadAllText(pgnPath), Is.EqualTo($"{entry}{entry}"));
    }

    [Test]
    public async Task StopsMoveCalculationWhenAccountStreamReportsGameFinished()
    {
        var events = new GameStreamEvent[]
        {
            CreateFullGame(state: CreateState([], 300_000, 300_000)),
            new GameStateEvent(CreateState([], 299_000, 300_000, "draw"))
        };
        var engine = new CancelableEngine();
        var controller = CreateController(new FakeLichessClient(events), engine);
        var playing = controller.PlayAsync("game", CreateAccount(), CancellationToken.None);
        await engine.Thinking.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var notified = controller.NotifyGameFinished("game");
        await playing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(notified, Is.True);
            Assert.That(engine.Result?.Result, Is.EqualTo("1/2-1/2"));
            Assert.That(engine.Stopped, Is.True);
        });
    }

    [Test]
    public async Task IgnoresEventsUntilGameMetadataArrives()
    {
        var events = new GameStreamEvent[] { new GameStateEvent(CreateState([], 1_000, 1_000)), new UnknownGameEvent("chatLine"), CreateFullGame(state: CreateState([], 1_000, 1_000, "resign", ChessColor.Black)) };
        var engine = new FakeEngine([]);
        var controller = CreateController(new FakeLichessClient(events), engine);

        await controller.PlayAsync("game", CreateAccount(), CancellationToken.None);

        Assert.That(engine.Result?.Result, Is.EqualTo("0-1"));
    }

    private GameController CreateController(ILichessClient lichess, IChessEngine engine, int abortIfNoMoveSeconds = 60)
    {
        return new GameController(lichess, new FakeEngineFactory(engine), Options.Create(new GamesOptions { PgnPath = pgnPath, AbortIfNoMoveSeconds = abortIfNoMoveSeconds }), Options.Create(new ChatOptions { StartMessage = "Good luck!", EndMessage = "Good game!" }), new RecordingStatusDisplay(), NullLogger<GameController>.Instance);
    }

    private static LichessAccount CreateAccount() => new("alarm-bot", "AlarmBot");

    private static GameStreamEvent[] CreateWhiteGame() =>
    [
        CreateFullGame(state: CreateState([], 300_000, 300_000)),
        new GameStateEvent(CreateState(["e2e4", "e7e5"], 298_000, 299_000)),
        new GameStateEvent(CreateState(["e2e4", "e7e5", "g1f3", "b8c6"], 296_000, 297_000, "mate", ChessColor.White))
    ];

    private static GameStreamEvent[] CreateBlackGame() =>
    [
        CreateFullGame(whiteId: "opponent", blackId: "alarm-bot", state: CreateState(["e2e4"], 299_000, 300_000)),
        new GameStateEvent(CreateState(["e2e4", "e7e5"], 299_000, 298_000, "resign", ChessColor.Black))
    ];

    private static GameFullEvent CreateFullGame(string initialFen = "startpos", string whiteId = "alarm-bot", string blackId = "opponent", GameState? state = null) => new("game", whiteId, "White", blackId, "Black", initialFen, 300, 3, state ?? CreateState([], 300_000, 300_000));

    private static GameState CreateState(string[] moves, int whiteTime, int blackTime, string status = "started", ChessColor? winner = null) => new([.. moves.Select(value => new ChessMove(value))], whiteTime, blackTime, status, winner);

    private sealed class FakeLichessClient(IReadOnlyList<GameStreamEvent> events) : ILichessClient
    {
        public Exception? PgnException { get; init; }
        public List<ChessMove> PlayedMoves { get; } = [];
        public List<string> ChatMessages { get; } = [];
        public List<string> AbortedGames { get; } = [];
        public List<string> ResignedGames { get; } = [];
        public bool WaitAfterEvents { get; init; }
        public bool FinishAfterAbort { get; init; }
        private readonly TaskCompletionSource aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LichessAccount> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AccountEvent> StreamAccountEventsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AcceptChallengeAsync(string challengeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeclineChallengeAsync(string challengeId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SendChatMessageAsync(string gameId, string message, CancellationToken cancellationToken)
        {
            ChatMessages.Add(message);

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<GameStreamEvent> StreamGameAsync(string gameId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var streamEvent in events)
            {
                yield return streamEvent;
            }

            if (FinishAfterAbort)
            {
                await aborted.Task.WaitAsync(cancellationToken);
                yield return new GameStateEvent(CreateState([], 300_000, 300_000, "aborted"));
            }

            if (WaitAfterEvents)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            await Task.CompletedTask;
        }

        public Task PlayMoveAsync(string gameId, ChessMove move, CancellationToken cancellationToken)
        {
            PlayedMoves.Add(move);

            return Task.CompletedTask;
        }

        public Task AbortGameAsync(string gameId, CancellationToken cancellationToken)
        {
            AbortedGames.Add(gameId);
            aborted.TrySetResult();

            return Task.CompletedTask;
        }

        public Task ResignGameAsync(string gameId, CancellationToken cancellationToken)
        {
            ResignedGames.Add(gameId);

            return Task.CompletedTask;
        }

        public Task<string> ExportPgnAsync(string gameId, CancellationToken cancellationToken) => PgnException is null ? Task.FromResult("[Result \"1-0\"]") : Task.FromException<string>(PgnException);
        public Task<IReadOnlyList<OnlineBot>> GetOnlineBotsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> CreateChallengeAsync(string username, BotChallengeRequest challenge, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelChallengeAsync(string challengeId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeEngineFactory(IChessEngine engine) : IChessEngineFactory
    {
        public IChessEngine Create() => engine;
    }

    private sealed class SequenceEngineFactory(Queue<IChessEngine> engines) : IChessEngineFactory
    {
        public IChessEngine Create() => engines.Dequeue();
    }

    private sealed class StoppedEngine : IChessEngine
    {
        public bool IsRunning { get; private set; }

        public event Action<EngineAnalysis>? AnalysisUpdated { add { } remove { } }
        public event Action<EngineSearchStatus>? SearchStatusUpdated { add { } remove { } }
        public event Action<string>? HintUpdated { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task SetPositionAsync(IReadOnlyList<ChessMove> position, int initialSeconds, int incrementSeconds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartPonderingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ChessMove> GetMoveAsync(ChessColor engineColor, ChessClock clock, CancellationToken cancellationToken)
        {
            IsRunning = false;
            return Task.FromException<ChessMove>(new InvalidOperationException("Engine exited with code 1."));
        }

        public Task SendResultAsync(GameResult result, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingStatusDisplay : IStatusDisplay
    {
        public void ShowHeader(StatusHeader header)
        {
        }

        public void ShowGame(GameStatus game)
        {
        }

        public void ShowAnalysis(EngineAnalysis? analysis)
        {
        }

        public void ShowSearchStatus(EngineSearchStatus? searchStatus)
        {
        }

        public void ShowHint(string? hint)
        {
        }

        public void ClearGame()
        {
        }

        public void Show(string status)
        {
        }

        public void ShowQuit(bool requested)
        {
        }

        public void ShowResign(bool confirmationRequested)
        {
        }

        public void ShowLogs(IReadOnlyList<DashboardLogEntry> entries)
        {
        }
    }

    private sealed class FakeEngine(Queue<ChessMove> moves) : IChessEngine
    {
        public FakeEngine(IEnumerable<ChessMove> moves) : this(new Queue<ChessMove>(moves))
        {
        }

        public List<IReadOnlyList<ChessMove>> Positions { get; } = [];
        public GameResult? Result { get; private set; }
        public bool Stopped { get; private set; }
        public int PonderCount { get; private set; }
        public bool IsRunning => Stopped is false;

        public event Action<EngineAnalysis>? AnalysisUpdated
        {
            add { }
            remove { }
        }
        public event Action<EngineSearchStatus>? SearchStatusUpdated
        {
            add { }
            remove { }
        }

        public event Action<string>? HintUpdated
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetPositionAsync(IReadOnlyList<ChessMove> position, int initialSeconds, int incrementSeconds, CancellationToken cancellationToken)
        {
            Positions.Add([.. position]);

            return Task.CompletedTask;
        }

        public Task StartPonderingAsync(CancellationToken cancellationToken)
        {
            PonderCount++;
            return Task.CompletedTask;
        }

        public Task<ChessMove> GetMoveAsync(ChessColor engineColor, ChessClock clock, CancellationToken cancellationToken) => Task.FromResult(moves.Dequeue());

        public Task SendResultAsync(GameResult result, CancellationToken cancellationToken)
        {
            Result = result;

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelableEngine : IChessEngine
    {
        public TaskCompletionSource Thinking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GameResult? Result { get; private set; }
        public bool Stopped { get; private set; }
        public bool IsRunning => Stopped is false;

        public event Action<EngineAnalysis>? AnalysisUpdated
        {
            add { }
            remove { }
        }
        public event Action<EngineSearchStatus>? SearchStatusUpdated
        {
            add { }
            remove { }
        }
        public event Action<string>? HintUpdated
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetPositionAsync(IReadOnlyList<ChessMove> position, int initialSeconds, int incrementSeconds, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartPonderingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<ChessMove> GetMoveAsync(ChessColor engineColor, ChessClock clock, CancellationToken cancellationToken)
        {
            Thinking.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            throw new InvalidOperationException();
        }

        public Task SendResultAsync(GameResult result, CancellationToken cancellationToken)
        {
            Result = result;

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
