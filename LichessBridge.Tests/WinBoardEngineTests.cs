using System.Threading.Channels;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Engine;
using LichessBridge.Status;
using Microsoft.Extensions.Logging.Abstractions;

namespace LichessBridge.Tests;

public sealed class WinBoardEngineTests
{
    [TestCase(5230, 523)]
    [TestCase(9, 0)]
    [TestCase(-1, 0)]
    public void ConvertsLichessMillisecondsToWinBoardCentiseconds(int milliseconds, int expected)
    {
        Assert.That(WinBoardEngine.MillisecondsToCentiseconds(milliseconds), Is.EqualTo(expected));
    }

    [Test]
    public void UsesRemainingClockForMoveTimeout()
    {
        var timeout = WinBoardEngine.CalculateMoveTimeout(new ChessClock(300_000, 120_000), ChessColor.White);

        Assert.That(timeout, Is.EqualTo(TimeSpan.FromSeconds(310)));
    }

    [Test]
    public async Task ReservesConfiguredMoveOverheadFromEngineClock()
    {
        var transport = new FakeEngineTransport();
        var options = new EngineOptions { Path = "in-memory-engine", Name = "Fake", MoveOverheadMilliseconds = 500 };
        await using var engine = new WinBoardEngine(options, NullLogger<WinBoardEngine>.Instance, transport);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(298_000, 299_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(transport.Commands, Does.Contain("time 29750"));
            Assert.That(transport.Commands, Does.Contain("otim 29900"));
        });
    }

    [Test]
    public async Task SendsCompleteWinBoardCommandSequence()
    {
        var transport = new FakeEngineTransport();
        await using var engine = CreateEngine(transport);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([new ChessMove("e2e4"), new ChessMove("e7e5")], 300, 3, CancellationToken.None);
        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(298_000, 299_000), CancellationToken.None);
        await engine.SendResultAsync(new GameResult("1-0", "mate"), CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("g1f3"));
            Assert.That(transport.Commands, Is.EqualTo([
                "xboard", "protover 2", "rejected san", "post", "new", "force", "level 0 5:00 3", "e2e4", "e7e5",
                "time 29800", "otim 29900", "go", ".", "force", "result 1-0 {mate}", "quit"
            ]));
        });
    }

    [Test]
    public void RejectsInvalidEngineMove()
    {
        var transport = new FakeEngineTransport(moveResponse: "move invalid");
        var engine = CreateEngine(transport);

        Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await engine.StartAsync(CancellationToken.None);
            await engine.GetMoveAsync(ChessColor.White, new ChessClock(1000, 1000), CancellationToken.None);
        });
    }

    [Test]
    public void PreventsStartingEngineTwice()
    {
        var engine = CreateEngine(new FakeEngineTransport());

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await engine.StartAsync(CancellationToken.None);
            await engine.StartAsync(CancellationToken.None);
        });
    }

    [Test]
    public async Task UsesUserMoveCommandWhenEngineRequestsIt()
    {
        var transport = new FakeEngineTransport(handshakeResponse: "feature san=0 usermove=1 done=1");
        await using var engine = CreateEngine(transport);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([new ChessMove("e2e4")], 300, 0, CancellationToken.None);

        Assert.That(transport.Commands, Does.Contain("usermove e2e4"));
    }

    [Test]
    public async Task ConvertsEngineSanResponseToUci()
    {
        var transport = new FakeEngineTransport(moveResponse: "move c5");
        await using var engine = CreateEngine(transport);
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([new ChessMove("e2e4")], 300, 0, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.Black, new ChessClock(300_000, 300_000), CancellationToken.None);

        Assert.That(move.Value, Is.EqualTo("c7c5"));
    }

    [Test]
    public async Task EnablesPonderingAndSendsConfiguredStartupCommands()
    {
        var transport = new FakeEngineTransport();
        var options = new EngineOptions
        {
            Path = "in-memory-engine",
            Name = "Fake",
            Ponder = true,
            StartupCommands = ["memory 64", "cores 2"]
        };
        await using var engine = new WinBoardEngine(options, NullLogger<WinBoardEngine>.Instance, transport);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(300_000, 300_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(transport.Commands, Does.Contain("hard"));
            Assert.That(transport.Commands, Does.Contain("memory 64"));
            Assert.That(transport.Commands, Does.Contain("cores 2"));
            Assert.That(transport.Commands.Last(), Is.EqualTo("."));
        });
    }

    [Test]
    public async Task ContinuesPonderingWithOnlyTheOpponentMove()
    {
        var transport = new FakeEngineTransport(ponderResponse: "move f3e5");
        var options = new EngineOptions
        {
            Path = "in-memory-engine",
            Name = "Fake",
            Ponder = true
        };
        await using var engine = new WinBoardEngine(options, NullLogger<WinBoardEngine>.Instance, transport);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 3, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(300_000, 300_000), CancellationToken.None);
        var commandCount = transport.Commands.Count;
        await engine.SetPositionAsync([new ChessMove("g1f3"), new ChessMove("e7e5")], 300, 3, CancellationToken.None);
        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(297_000, 298_000), CancellationToken.None);
        var continuationCommands = transport.Commands.Skip(commandCount).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("f3e5"));
            Assert.That(continuationCommands, Is.EqualTo(["time 29700", "otim 29800", "e7e5", ".", "hint", "."]));
            Assert.That(continuationCommands, Does.Not.Contain("new"));
            Assert.That(continuationCommands, Does.Not.Contain("force"));
            Assert.That(continuationCommands, Does.Not.Contain("go"));
        });
    }

    [Test]
    public async Task PublishesWinBoardAnalysisOutput()
    {
        var transport = new FakeEngineTransport(analysisResponse: "12 191 42 3184 Nf6 O-O Be6");
        await using var engine = CreateEngine(transport);
        EngineAnalysis? received = null;
        engine.AnalysisUpdated += analysis => received = analysis;

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(300_000, 300_000), CancellationToken.None);

        Assert.That(received, Is.EqualTo(new EngineAnalysis(12, 191, 42, 3184, "Nf6 O-O Be6")));
    }

    [Test]
    public async Task RequestsAndPublishesPonderHint()
    {
        var transport = new FakeEngineTransport(hintResponse: "Hint: e7e5");
        var options = new EngineOptions { Path = "in-memory-engine", Name = "Fake", Ponder = true };
        await using var engine = new WinBoardEngine(options, NullLogger<WinBoardEngine>.Instance, transport);
        string? received = null;
        engine.HintUpdated += hint => received = hint;

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(300_000, 300_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(transport.Commands.TakeLast(2), Is.EqualTo(["hint", "."]));
            Assert.That(received, Is.EqualTo("e7e5"));
        });
    }

    [Test]
    public async Task RestartsSearchStatusPollingWhenLichessEchoesMoveDuringPondering()
    {
        var transport = new FakeEngineTransport();
        var options = new EngineOptions { Path = "in-memory-engine", Name = "Fake", Ponder = true };
        await using var engine = new WinBoardEngine(options, NullLogger<WinBoardEngine>.Instance, transport);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 0, CancellationToken.None);
        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(300_000, 300_000), CancellationToken.None);
        var commandCount = transport.Commands.Count;
        await engine.SetPositionAsync([move], 300, 0, CancellationToken.None);
        await engine.StartPonderingAsync(CancellationToken.None);

        Assert.That(transport.Commands.Skip(commandCount), Is.EqualTo(["."]));
    }

    [Test]
    public void PreservesAnalysisVariationPrefix()
    {
        var parsed = EngineAnalysis.TryParse("3\t-45\t12\t9876\t& fxg3 Be6 O-O", out var analysis);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(analysis, Is.EqualTo(new EngineAnalysis(3, -45, 12, 9876, "& fxg3 Be6 O-O")));
        });
    }

    [Test]
    public void ParsesOpeningBookOutput()
    {
        const string line = "0\t0\t0\t0\tD00 Queen's pawn - Chigorin variation (Bg6 100%)";

        var parsed = EngineAnalysis.TryParse(line, out var analysis);
        var formatted = analysis is null ? string.Empty : StatusFormatter.FormatAnalysis(analysis);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(analysis, Is.EqualTo(new EngineAnalysis(0, 0, 0, 0, "D00 Queen's pawn - Chigorin variation (Bg6 100%)")));
            Assert.That(formatted, Is.EqualTo("book • D00 Queen's pawn - Chigorin variation (Bg6 100%)"));
        });
    }

    [Test]
    public void ParsesSearchStatusOutput()
    {
        const string line = "stat01: 123 456789 14 7 32 Nf6";

        var parsed = EngineSearchStatus.TryParse(line, out var searchStatus);
        var formatted = searchStatus is null ? string.Empty : StatusFormatter.FormatSearchStatus(searchStatus);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(searchStatus, Is.EqualTo(new EngineSearchStatus(123, 456789, 14, 7, 32, "Nf6")));
            Assert.That(formatted, Is.EqualTo("depth 14 • 1.23s • 456\u00A0789 nodes • 371 kNPS • Nf6 • 25/32"));
        });
    }

    private static WinBoardEngine CreateEngine(IEngineTransport transport)
    {
        var options = new EngineOptions
        {
            Path = "in-memory-engine",
            Name = "Fake"
        };

        return new WinBoardEngine(options, NullLogger<WinBoardEngine>.Instance, transport);
    }

    private sealed class FakeEngineTransport(string moveResponse = "move g1f3", string handshakeResponse = "feature myname=\"Fake\" san=1 usermove=0 done=1", string? ponderResponse = null, string? analysisResponse = null, string? hintResponse = null) : IEngineTransport
    {
        private readonly Channel<string> responses = Channel.CreateUnbounded<string>();
        private bool hasStartedPlaying;

        public event Action<string>? OutputReceived;

        public bool IsRunning { get; private set; }
        public int? ExitCode => IsRunning ? null : 0;
        public List<string> Commands { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken)
        {
            IsRunning = true;

            return Task.CompletedTask;
        }

        public async Task SendAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);

            if (command == "protover 2")
            {
                await responses.Writer.WriteAsync(handshakeResponse, cancellationToken);
            }

            if (command == "go")
            {
                if (analysisResponse is not null)
                {
                    OutputReceived?.Invoke(analysisResponse);
                    await responses.Writer.WriteAsync(analysisResponse, cancellationToken);
                }

                OutputReceived?.Invoke(moveResponse);
                await responses.Writer.WriteAsync(moveResponse, cancellationToken);
                hasStartedPlaying = true;
            }

            if (hasStartedPlaying && ponderResponse is not null && ChessMove.TryParse(command, out _))
            {
                OutputReceived?.Invoke(ponderResponse);
                await responses.Writer.WriteAsync(ponderResponse, cancellationToken);
                hasStartedPlaying = false;
            }

            if (command == "quit")
            {
                IsRunning = false;
            }

            if (command == "hint" && hintResponse is not null)
            {
                OutputReceived?.Invoke(hintResponse);
                await responses.Writer.WriteAsync(hintResponse, cancellationToken);
            }
        }

        public ValueTask<string> ReceiveAsync(CancellationToken cancellationToken) => responses.Reader.ReadAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken)
        {
            IsRunning = false;

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;

            return ValueTask.CompletedTask;
        }
    }
}
