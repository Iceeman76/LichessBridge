using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Status;

namespace DotNetLab.LichessBridge.Tests;

public sealed class WinBoardEngineTests
{
    [TestCase(5230, 523)]
    [TestCase(9, 0)]
    [TestCase(-1, 0)]
    public async Task SendsClocksInWinBoardCentiseconds(int milliseconds, int expected)
    {
        await using var process = new EngineProcess();
        await using var engine = process.CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.Black, new ChessClock(milliseconds, 300_000), CancellationToken.None);

        Assert.That(process.Commands, Does.Contain($"otim {expected}"));
    }

    [Test]
    public async Task RequestsEmergencyMoveBeforeClockRunsOut()
    {
        await using var process = new EngineProcess(waitForEmergencyMove: true);
        var options = new EngineOptions { Path = process.ExecutablePath, MoveOverheadMilliseconds = 500, SearchStatusPollMilliseconds = 0 };
        await using var engine = process.CreateEngine(options);
        await engine.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(2000, 300_000), cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("g1f3"));
            Assert.That(process.Commands, Does.Contain("?"));
        });
    }

    [Test]
    public async Task ReservesConfiguredMoveOverheadFromEngineClock()
    {
        await using var process = new EngineProcess();
        var options = new EngineOptions { Path = process.ExecutablePath, Name = "Fake", MoveOverheadMilliseconds = 500 };
        await using var engine = process.CreateEngine(options);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(298_000, 299_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(process.Commands, Does.Contain("time 29750"));
            Assert.That(process.Commands, Does.Contain("otim 29900"));
        });
    }

    [Test]
    public async Task SendsCompleteWinBoardCommandSequence()
    {
        await using var process = new EngineProcess();
        await using var engine = process.CreateEngine();

        await engine.StartAsync(CancellationToken.None);
        await engine.SetRatingsAsync(1825, 1740, CancellationToken.None);
        await engine.SetPositionAsync([new ChessMove("e2e4"), new ChessMove("e7e5")], 300, 3, CancellationToken.None);
        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(298_000, 299_000), CancellationToken.None);
        await engine.SendResultAsync(new GameResult("1-0", "mate"), CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("g1f3"));
            Assert.That(process.Commands, Is.EqualTo([
                "xboard", "protover 2", "rejected san", "post", "ratings 1825 1740", "new", "force", "level 0 5:00 3", "e2e4", "e7e5",
                "time 29800", "otim 29900", "go", ".", "force", "result 1-0 {mate}", "quit"
            ]));
        });
    }

    [Test]
    public async Task RejectsInvalidEngineMove()
    {
        await using var process = new EngineProcess(moveResponse: "move invalid");
        await using var engine = process.CreateEngine();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await engine.StartAsync(CancellationToken.None);
            await engine.GetMoveAsync(ChessColor.White, new ChessClock(1000, 1000), CancellationToken.None);
        });
    }

    [Test]
    public async Task PreventsStartingEngineTwice()
    {
        await using var process = new EngineProcess();
        await using var engine = process.CreateEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await engine.StartAsync(CancellationToken.None);
            await engine.StartAsync(CancellationToken.None);
        });
    }

    [Test]
    public async Task UsesUserMoveCommandWhenEngineRequestsIt()
    {
        await using var process = new EngineProcess(handshakeResponse: "feature san=0 usermove=1 done=1");
        await using var engine = process.CreateEngine();

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([new ChessMove("e2e4")], 300, 0, CancellationToken.None);

        Assert.That(process.Commands, Does.Contain("usermove e2e4"));
    }

    [Test]
    public async Task ConvertsEngineSanResponseToUci()
    {
        await using var process = new EngineProcess(moveResponse: "move c5");
        await using var engine = process.CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([new ChessMove("e2e4")], 300, 0, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.Black, new ChessClock(300_000, 300_000), CancellationToken.None);

        Assert.That(move.Value, Is.EqualTo("c7c5"));
    }

    [Test]
    public async Task EnablesPonderingAndSendsConfiguredStartupCommands()
    {
        await using var process = new EngineProcess();
        var options = new EngineOptions
        {
            Path = process.ExecutablePath,
            Name = "Fake",
            SearchStatusPollMilliseconds = 60_000,
            Ponder = true,
            StartupCommands = ["memory 64", "cores 2"]
        };
        await using var engine = process.CreateEngine(options);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(300_000, 300_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(process.Commands, Does.Contain("hard"));
            Assert.That(process.Commands, Does.Contain("memory 64"));
            Assert.That(process.Commands, Does.Contain("cores 2"));
            Assert.That(process.Commands.Last(), Is.EqualTo("."));
        });
    }

    [Test]
    public async Task ContinuesPonderingWithOnlyTheOpponentMove()
    {
        await using var process = new EngineProcess(ponderResponse: "move f3e5");
        var options = new EngineOptions
        {
            Path = process.ExecutablePath,
            Name = "Fake",
            SearchStatusPollMilliseconds = 60_000,
            Ponder = true
        };
        await using var engine = process.CreateEngine(options);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 3, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(300_000, 300_000), CancellationToken.None);
        var commandCount = process.Commands.Count;
        await engine.SetPositionAsync([new ChessMove("g1f3"), new ChessMove("e7e5")], 300, 3, CancellationToken.None);
        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(297_000, 298_000), CancellationToken.None);
        var continuationCommands = process.Commands.Skip(commandCount).ToArray();

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
        await using var process = new EngineProcess(analysisResponse: "12 191 42 3184 Nf6 O-O Be6");
        await using var engine = process.CreateEngine();
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
        await using var process = new EngineProcess(hintResponse: "Hint: e7e5");
        var options = new EngineOptions { Path = process.ExecutablePath, Name = "Fake", SearchStatusPollMilliseconds = 60_000, Ponder = true };
        await using var engine = process.CreateEngine(options);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.HintUpdated += hint => received.TrySetResult(hint);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(300_000, 300_000), CancellationToken.None);
        var hint = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(process.Commands.TakeLast(2), Is.EqualTo(["hint", "."]));
            Assert.That(hint, Is.EqualTo("e7e5"));
        });
    }

    [Test]
    public async Task RestartsSearchStatusPollingWhenLichessEchoesMoveDuringPondering()
    {
        await using var process = new EngineProcess();
        var options = new EngineOptions { Path = process.ExecutablePath, Name = "Fake", SearchStatusPollMilliseconds = 60_000, Ponder = true };
        await using var engine = process.CreateEngine(options);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 0, CancellationToken.None);
        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(300_000, 300_000), CancellationToken.None);
        var commandCount = process.Commands.Count;
        await engine.SetPositionAsync([move], 300, 0, CancellationToken.None);
        await engine.StartPonderingAsync(CancellationToken.None);

        Assert.That(process.Commands.Skip(commandCount), Is.EqualTo(["."]));
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

}
