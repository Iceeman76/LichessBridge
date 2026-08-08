using System.Runtime.CompilerServices;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Engine;
using LichessBridge.Game;
using LichessBridge.Lichess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LichessBridge.Tests;

public sealed class GameControllerTests
{
    private string pgnDirectory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        pgnDirectory = Path.Combine(Path.GetTempPath(), $"LichessBridge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pgnDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(pgnDirectory, recursive: true);
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
            Assert.That(lichess.PlayedMoves, Is.EqualTo(new[] { new ChessMove("e2e4"), new ChessMove("g1f3") }));
            Assert.That(engine.Positions, Has.Count.EqualTo(2));
            Assert.That(engine.Result?.Result, Is.EqualTo("1-0"));
            Assert.That(engine.Stopped, Is.True);
            Assert.That(File.ReadAllText(Path.Combine(pgnDirectory, "game1.pgn")), Is.EqualTo("[Result \"1-0\"]"));
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
            Assert.That(lichess.PlayedMoves, Is.EqualTo(new[] { new ChessMove("e7e5") }));
            Assert.That(engine.Result?.Result, Is.EqualTo("0-1"));
        });
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
    public async Task IgnoresEventsUntilGameMetadataArrives()
    {
        var events = new GameStreamEvent[] { new GameStateEvent(CreateState([], 1_000, 1_000)), new UnknownGameEvent("chatLine"), CreateFullGame(state: CreateState([], 1_000, 1_000, "resign", ChessColor.Black)) };
        var engine = new FakeEngine([]);
        var controller = CreateController(new FakeLichessClient(events), engine);

        await controller.PlayAsync("game", CreateAccount(), CancellationToken.None);

        Assert.That(engine.Result?.Result, Is.EqualTo("0-1"));
    }

    private GameController CreateController(ILichessClient lichess, IChessEngine engine)
    {
        return new GameController(lichess, new FakeEngineFactory(engine), Options.Create(new GamesOptions { PgnDirectory = pgnDirectory }), NullLogger<GameController>.Instance);
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

    private static GameState CreateState(string[] moves, int whiteTime, int blackTime, string status = "started", ChessColor? winner = null) => new(moves.Select(value => new ChessMove(value)).ToArray(), whiteTime, blackTime, status, winner);

    private sealed class FakeLichessClient(IReadOnlyList<GameStreamEvent> events) : ILichessClient
    {
        public Exception? PgnException { get; init; }
        public List<ChessMove> PlayedMoves { get; } = [];

        public Task<LichessAccount> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AccountEvent> StreamAccountEventsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AcceptChallengeAsync(string challengeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeclineChallengeAsync(string challengeId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<GameStreamEvent> StreamGameAsync(string gameId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var streamEvent in events)
            {
                yield return streamEvent;
            }

            await Task.CompletedTask;
        }

        public Task PlayMoveAsync(string gameId, ChessMove move, CancellationToken cancellationToken)
        {
            PlayedMoves.Add(move);

            return Task.CompletedTask;
        }

        public Task<string> ExportPgnAsync(string gameId, CancellationToken cancellationToken) => PgnException is null ? Task.FromResult("[Result \"1-0\"]") : Task.FromException<string>(PgnException);
    }

    private sealed class FakeEngineFactory(IChessEngine engine) : IChessEngineFactory
    {
        public IChessEngine Create() => engine;
    }

    private sealed class FakeEngine(Queue<ChessMove> moves) : IChessEngine
    {
        public FakeEngine(IEnumerable<ChessMove> moves) : this(new Queue<ChessMove>(moves))
        {
        }

        public List<IReadOnlyList<ChessMove>> Positions { get; } = [];
        public GameResult? Result { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetPositionAsync(IReadOnlyList<ChessMove> position, int initialSeconds, int incrementSeconds, CancellationToken cancellationToken)
        {
            Positions.Add(position.ToArray());

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
}
