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
    [Test]
    public async Task PlaysWhiteGameReportsResultAndSavesPgn()
    {
        var pgnDirectory = Path.Combine(Path.GetTempPath(), $"LichessBridge-{Guid.NewGuid():N}");
        try
        {
            var lichess = new FakeLichessClient();
            var engine = new FakeEngine([new ChessMove("e2e4"), new ChessMove("g1f3")]);
            var controller = new GameController(
                lichess,
                new FakeEngineFactory(engine),
                Options.Create(new GamesOptions { PgnDirectory = pgnDirectory }),
                NullLogger<GameController>.Instance);

            await controller.PlayAsync("game1", new LichessAccount("alarm-bot", "AlarmBot"), CancellationToken.None);

            Assert.That(lichess.PlayedMoves, Is.EqualTo(new[] { new ChessMove("e2e4"), new ChessMove("g1f3") }));
            Assert.That(engine.Positions, Has.Count.EqualTo(2));
            Assert.That(engine.Result?.Result, Is.EqualTo("1-0"));
            Assert.That(engine.Stopped, Is.True);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(pgnDirectory, "game1.pgn")), Is.EqualTo("[Result \"1-0\"]"));
        }
        finally
        {
            if (Directory.Exists(pgnDirectory))
                Directory.Delete(pgnDirectory, recursive: true);
        }
    }

    [Test]
    public async Task PlaysAsBlackAfterOpponentsOpeningMove()
    {
        var pgnDirectory = Path.Combine(Path.GetTempPath(), $"LichessBridge-{Guid.NewGuid():N}");
        try
        {
            var lichess = new FakeLichessClient(botPlaysWhite: false);
            var engine = new FakeEngine([new ChessMove("e7e5")]);
            var controller = new GameController(
                lichess,
                new FakeEngineFactory(engine),
                Options.Create(new GamesOptions { PgnDirectory = pgnDirectory }),
                NullLogger<GameController>.Instance);

            await controller.PlayAsync("game2", new LichessAccount("alarm-bot", "AlarmBot"), CancellationToken.None);

            Assert.That(lichess.PlayedMoves, Is.EqualTo(new[] { new ChessMove("e7e5") }));
            Assert.That(engine.Result?.Result, Is.EqualTo("0-1"));
        }
        finally
        {
            if (Directory.Exists(pgnDirectory))
                Directory.Delete(pgnDirectory, recursive: true);
        }
    }

    [Test]
    public void RejectsNonStandardStartingPosition()
    {
        var lichess = new EventLichessClient([
            FullGame(initialFen: "8/8/8/8/8/8/8/8 w - - 0 1")
        ]);
        var controller = CreateController(lichess, new FakeEngine([]));

        Assert.ThrowsAsync<NotSupportedException>(() =>
            controller.PlayAsync("game", new LichessAccount("alarm-bot", "AlarmBot"), CancellationToken.None));
    }

    [Test]
    public void RejectsGameThatDoesNotContainBotAccount()
    {
        var lichess = new EventLichessClient([
            FullGame(whiteId: "other-white", blackId: "other-black")
        ]);
        var controller = CreateController(lichess, new FakeEngine([]));

        Assert.ThrowsAsync<InvalidDataException>(() =>
            controller.PlayAsync("game", new LichessAccount("alarm-bot", "AlarmBot"), CancellationToken.None));
    }

    [Test]
    public async Task ReportsDrawAndDoesNotFailWhenPgnExportFails()
    {
        var lichess = new EventLichessClient([
            FullGame(state: State([], 1_000, 1_000, "draw", null))
        ])
        {
            PgnException = new HttpRequestException("export failed")
        };
        var engine = new FakeEngine([]);
        var controller = CreateController(lichess, engine);

        await controller.PlayAsync("game", new LichessAccount("alarm-bot", "AlarmBot"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Result?.Result, Is.EqualTo("1/2-1/2"));
            Assert.That(engine.Stopped, Is.True);
        });
    }

    [Test]
    public async Task IgnoresEventsUntilGameMetadataArrives()
    {
        var lichess = new EventLichessClient([
            new GameStateEvent(State([], 1_000, 1_000)),
            new UnknownGameEvent("chatLine"),
            FullGame(state: State([], 1_000, 1_000, "resign", ChessColor.Black))
        ]);
        var engine = new FakeEngine([]);
        var controller = CreateController(lichess, engine);

        await controller.PlayAsync("game", new LichessAccount("alarm-bot", "AlarmBot"), CancellationToken.None);

        Assert.That(engine.Result?.Result, Is.EqualTo("0-1"));
    }

    private static GameController CreateController(ILichessClient lichess, IChessEngine engine) => new(
        lichess,
        new FakeEngineFactory(engine),
        Options.Create(new GamesOptions
        {
            PgnDirectory = Path.Combine(Path.GetTempPath(), $"LichessBridge-{Guid.NewGuid():N}")
        }),
        NullLogger<GameController>.Instance);

    private static GameFullEvent FullGame(
        string initialFen = "startpos",
        string whiteId = "alarm-bot",
        string blackId = "opponent",
        GameState? state = null) =>
        new("game", whiteId, "White", blackId, "Black", initialFen, 300, 3,
            state ?? State([], 300_000, 300_000));

    private static GameState State(
        string[] moves,
        int white,
        int black,
        string status = "started",
        ChessColor? winner = null) =>
        new(moves.Select(value => new ChessMove(value)).ToArray(), white, black, status, winner);

    private sealed class FakeLichessClient(bool botPlaysWhite = true) : ILichessClient
    {
        public List<ChessMove> PlayedMoves { get; } = [];

        public Task<LichessAccount> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AccountEvent> StreamAccountEventsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AcceptChallengeAsync(string challengeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeclineChallengeAsync(string challengeId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<GameStreamEvent> StreamGameAsync(
            string gameId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (botPlaysWhite)
            {
                yield return new GameFullEvent(
                    gameId, "alarm-bot", "AlarmBot", "opponent", "Opponent", "startpos", 300, 3,
                    State([], 300_000, 300_000));
                yield return new GameStateEvent(State(["e2e4", "e7e5"], 298_000, 299_000));
                yield return new GameStateEvent(State(["e2e4", "e7e5", "g1f3", "b8c6"], 296_000, 297_000, "mate", ChessColor.White));
            }
            else
            {
                yield return new GameFullEvent(
                    gameId, "opponent", "Opponent", "alarm-bot", "AlarmBot", "startpos", 300, 3,
                    State(["e2e4"], 299_000, 300_000));
                yield return new GameStateEvent(State(["e2e4", "e7e5"], 299_000, 298_000, "resign", ChessColor.Black));
            }
            await Task.CompletedTask;
        }

        public Task PlayMoveAsync(string gameId, ChessMove move, CancellationToken cancellationToken)
        {
            PlayedMoves.Add(move);
            return Task.CompletedTask;
        }

        public Task<string> ExportPgnAsync(string gameId, CancellationToken cancellationToken) =>
            Task.FromResult("[Result \"1-0\"]");

    }

    private sealed class EventLichessClient(IReadOnlyList<GameStreamEvent> events) : ILichessClient
    {
        public Exception? PgnException { get; init; }

        public Task<LichessAccount> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AccountEvent> StreamAccountEventsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AcceptChallengeAsync(string challengeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeclineChallengeAsync(string challengeId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PlayMoveAsync(string gameId, ChessMove move, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<GameStreamEvent> StreamGameAsync(
            string gameId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var streamEvent in events)
                yield return streamEvent;
            await Task.CompletedTask;
        }

        public Task<string> ExportPgnAsync(string gameId, CancellationToken cancellationToken) =>
            PgnException is null
                ? Task.FromResult("[Result \"*\"]")
                : Task.FromException<string>(PgnException);
    }

    private sealed class FakeEngineFactory(IChessEngine engine) : IChessEngineFactory
    {
        public IChessEngine Create() => engine;
    }

    private sealed class FakeEngine(Queue<ChessMove> moves) : IChessEngine
    {
        public FakeEngine(IEnumerable<ChessMove> moves) : this(new Queue<ChessMove>(moves)) { }
        public List<IReadOnlyList<ChessMove>> Positions { get; } = [];
        public GameResult? Result { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetPositionAsync(IReadOnlyList<ChessMove> position, int initialSeconds, int incrementSeconds, CancellationToken cancellationToken)
        {
            Positions.Add(position.ToArray());
            return Task.CompletedTask;
        }

        public Task<ChessMove> GetMoveAsync(ChessColor engineColor, ChessClock clock, CancellationToken cancellationToken) =>
            Task.FromResult(moves.Dequeue());

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
