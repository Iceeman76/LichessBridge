using System.Collections.Concurrent;
using System.Diagnostics;
using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Diagnostics;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Status;

namespace DotNetLab.LichessBridge.Tests;

public sealed class UciEngineTests
{
    [Test]
    public async Task RetainsMetricsWithinSearchAndClearsThemForNextSearch()
    {
        var settings = new UciTestSettings
        {
            BestMoves = ["bestmove g1f3", "bestmove b1c3"],
            InfoBySearch =
            [
                ["info hashfull 375 tbhits 1234", "info depth 14 nodes 800"],
                ["info depth 15 nodes 1000"]
            ]
        };
        await using var process = new EngineProcess(uci: settings);
        await using var engine = process.CreateEngine(CreateOptions(process));
        var statuses = new ConcurrentQueue<EngineSearchStatus>();
        engine.SearchStatusUpdated += statuses.Enqueue;
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        var first = statuses.Last();
        await engine.SetPositionAsync(Parse("g1f3 g8f6"), 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        var second = statuses.Last();

        Assert.Multiple(() =>
        {
            Assert.That(first.Depth, Is.EqualTo(14));
            Assert.That(first.HashFullPermille, Is.EqualTo(375));
            Assert.That(first.TablebaseHits, Is.EqualTo(1234));
            Assert.That(second.Depth, Is.EqualTo(15));
            Assert.That(second.HashFullPermille, Is.Null);
            Assert.That(second.TablebaseHits, Is.Null);
        });
    }

    [Test]
    public async Task AcceptsWhitespaceInOptionDeclarations()
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { Options = ["\toption\tname\tHash\ttype\tspin\tdefault\t16\tmin\t1\tmax\t2048"] });
        await using var engine = process.CreateEngine(CreateOptions(process, settings: new UciOptions { Options = new Dictionary<string, string> { ["Hash"] = "128" } }));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);

        Assert.That(move.Value, Is.EqualTo("g1f3"));
        Assert.That(process.Commands, Does.Contain("setoption name Hash value 128"));
        Assert.That(process.Commands.Any(command => command.StartsWith("setoption name Ponder", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task SupportsEngineWithoutAdvertisedOptions()
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { Options = [] });
        await using var engine = process.CreateEngine(CreateOptions(process));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);

        Assert.That(move.Value, Is.EqualTo("g1f3"));
    }

    [Test]
    public async Task InitializesAdvertisedOptionsAndSynchronizesStartup()
    {
        await using var process = new EngineProcess(uci: new UciTestSettings());
        var settings = new UciOptions
        {
            Options = new Dictionary<string, string>
            {
                ["Threads"] = "2",
                ["Hash"] = "128",
                ["Style"] = "Very Aggressive",
                ["SyzygyPath"] = @"D:\Tablebases\Syzygy",
                ["Clear Hash"] = ""
            }
        };
        await using var engine = process.CreateEngine(CreateOptions(process, ponder: true, settings: settings));

        await engine.StartAsync(CancellationToken.None);

        Assert.That(process.Commands, Is.EqualTo(new[] { "uci", "setoption name Threads value 2", "setoption name Hash value 128", "setoption name Style value Very Aggressive", @"setoption name SyzygyPath value D:\Tablebases\Syzygy", "setoption name Clear Hash", "setoption name Ponder value true", "isready" }));
    }

    [TestCase("Missing", "1")]
    [TestCase("Threads", "0")]
    [TestCase("Threads", "17")]
    [TestCase("Threads", "many")]
    [TestCase("Style", "Passive")]
    [TestCase("Ponder", "true")]
    [TestCase("Clear Hash", "true")]
    [TestCase("Hash", "1\nquit")]
    public async Task RejectsUnsupportedOrInvalidOptions(string name, string value)
    {
        await using var process = new EngineProcess(uci: new UciTestSettings());
        var settings = new UciOptions { Options = new Dictionary<string, string> { [name] = value } };
        await using var engine = process.CreateEngine(CreateOptions(process, settings: settings));

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(CancellationToken.None));
        Assert.That(process.Commands, Does.Not.Contain("go"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task TimesOutMissingHandshakeOrReadiness(bool missingHandshake)
    {
        var testSettings = new UciTestSettings { OmitUciOk = missingHandshake, OmitReadyOk = missingHandshake is false };
        await using var process = new EngineProcess(uci: testSettings);
        await using var engine = process.CreateEngine(CreateOptions(process, settings: new UciOptions { ResponseTimeoutSeconds = 1 }));

        await Assert.ThrowsAsync<TimeoutException>(() => engine.StartAsync(CancellationToken.None));
    }

    [TestCase(ChessColor.White, "go wtime 12195 btime 23456 winc 2000 binc 2000")]
    [TestCase(ChessColor.Black, "go wtime 12345 btime 23306 winc 2000 binc 2000")]
    public async Task SendsHistoryAndMillisecondClocksWithOverheadForOurSide(ChessColor color, string expectedGo)
    {
        await using var process = new EngineProcess(uci: new UciTestSettings());
        await using var engine = process.CreateEngine(CreateOptions(process, overhead: 150));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync(Parse("e2e4 e7e5"), 60, 2, CancellationToken.None);
        using var timing = new MoveTiming("local", 3, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        timing.Activate();

        var move = await engine.GetMoveAsync(color, new ChessClock(12345, 23456), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("g1f3"));
            Assert.That(process.Commands.TakeLast(4), Is.EqualTo(new[] { "ucinewgame", "isready", "position startpos moves e2e4 e7e5", expectedGo }));
            Assert.That(timing.EngineMoveFormat, Is.EqualTo("uci"));
            Assert.That(timing.StdoutRead, Is.GreaterThan(0));
            Assert.That(timing.EngineReceived, Is.GreaterThanOrEqualTo(timing.StdoutRead));
            Assert.That(process.Commands, Does.Not.Contain("hint").And.Not.Contain("force").And.Not.Contain("."));
        });
    }

    [Test]
    public async Task PublishesPartialAnalysisMateAndTablebaseHitsWithoutPolling()
    {
        var testSettings = new UciTestSettings
        {
            Info =
            [
                "info depth 12 score mate -3 time 21 nodes 100 hashfull 375 pv g1f3 g8f6",
                "info nodes 300 time 95 tbhits 42 currmove b1c3 currmovenumber 2",
                "info depth 99 multipv 2 score cp 999 nodes 9999 pv a2a3",
                "info string depth 88 score cp 10000"
            ]
        };
        await using var process = new EngineProcess(uci: testSettings);
        await using var engine = process.CreateEngine(CreateOptions(process));
        var analysisReceived = new TaskCompletionSource<EngineAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusReceived = new TaskCompletionSource<EngineSearchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AnalysisUpdated += analysis =>
        {
            if (analysis.TablebaseHits is 42)
            {
                analysisReceived.TrySetResult(analysis);
            }
        };
        engine.SearchStatusUpdated += status =>
        {
            if (status.TablebaseHits is 42)
            {
                statusReceived.TrySetResult(status);
            }
        };
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        var analysis = await analysisReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var status = await statusReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(analysis.Depth, Is.EqualTo(12));
            Assert.That(analysis.MateInMoves, Is.EqualTo(-3));
            Assert.That(analysis.Nodes, Is.EqualTo(300));
            Assert.That(analysis.TimeCentiseconds, Is.EqualTo(9));
            Assert.That(analysis.PrincipalVariation, Is.EqualTo("g1f3 g8f6"));
            Assert.That(status.CurrentMove, Is.EqualTo("b1c3"));
            Assert.That(status.CurrentMoveNumber, Is.EqualTo(2));
            Assert.That(status.HashFullPermille, Is.EqualTo(375));
            Assert.That(StatusFormatter.FormatAnalysis(analysis), Does.Contain("mate -3"));
            Assert.That(StatusFormatter.FormatSearchStatus(status), Does.Contain("TB 42").And.Not.Contain("0/0"));
            Assert.That(StatusFormatter.FormatSearchStatus(status), Does.Contain("Hash 37.5%"));
            Assert.That(process.Commands, Does.Not.Contain("."));
        });
    }

    [TestCase("bestmove 0000")]
    [TestCase("bestmove (none)")]
    [TestCase("bestmove Nf3")]
    [TestCase("bestmove")]
    public async Task RejectsMissingOrMalformedMoves(string response)
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { BestMoves = [response] });
        await using var engine = process.CreateEngine(CreateOptions(process));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None));
    }

    [Test]
    public async Task ContinuesPredictedSearchWithPonderhitAndServerClocks()
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { BestMoves = ["bestmove g1f3 ponder g8f6", "bestmove b1c3"] });
        await using var engine = process.CreateEngine(CreateOptions(process, ponder: true));
        var hint = string.Empty;
        engine.HintUpdated += value => hint = value;
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 2, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        Assert.That(process.Commands.Last(), Does.StartWith("go wtime"));
        await engine.SetPositionAsync(Parse("g1f3"), 60, 2, CancellationToken.None);
        await engine.StartPonderingAsync(new ChessClock(61_000, 59_000), CancellationToken.None);
        var commandCount = process.Commands.Count;
        using var timing = new MoveTiming("local", 3, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        timing.Activate();
        await engine.SetPositionAsync(Parse("g1f3 g8f6"), 60, 2, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(61_000, 58_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("b1c3"));
            Assert.That(hint, Is.EqualTo("g8f6"));
            Assert.That(process.Commands, Does.Contain("position startpos moves g1f3 g8f6"));
            Assert.That(process.Commands, Does.Contain("go ponder wtime 61000 btime 59000 winc 2000 binc 2000"));
            Assert.That(process.Commands.Skip(commandCount), Is.EqualTo(new[] { "ponderhit" }));
            Assert.That(timing.SearchPath, Is.EqualTo("continue-pondering"));
            Assert.That(timing.WasPondering, Is.True);
        });
    }

    [Test]
    public async Task DiscardsStoppedPonderResultBeforeStartingCorrectPosition()
    {
        var testSettings = new UciTestSettings { BestMoves = ["bestmove g1f3 ponder g8f6", "bestmove b1c3", "bestmove c2c4"], StopMove = "bestmove a2a3", StopDelayMilliseconds = 40 };
        await using var process = new EngineProcess(uci: testSettings);
        await using var engine = process.CreateEngine(CreateOptions(process, ponder: true));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        await engine.SetPositionAsync(Parse("g1f3"), 60, 0, CancellationToken.None);
        await engine.StartPonderingAsync(CancellationToken.None);
        var commandCount = process.Commands.Count;
        using var timing = new MoveTiming("local", 3, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        timing.Activate();
        await engine.SetPositionAsync(Parse("g1f3 d7d5"), 60, 0, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(59_000, 59_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("c2c4"));
            Assert.That(process.Commands.Skip(commandCount), Is.EqualTo(new[] { "stop", "position startpos moves g1f3 d7d5", "go wtime 59000 btime 59000 winc 0 binc 0" }));
            Assert.That(timing.SearchPath, Is.EqualTo("new-search"));
            Assert.That(timing.WasPondering, Is.True);
        });
    }

    [Test]
    public async Task LeavesAnalysisAndDiscardsItsBestMove()
    {
        await using var process = new EngineProcess(uci: new UciTestSettings());
        await using var engine = process.CreateEngine(CreateOptions(process, ponder: true));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
        await engine.StartPonderingAsync(CancellationToken.None);
        using var timing = new MoveTiming("local", 3, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        timing.Activate();
        await engine.SetPositionAsync(Parse("e2e4 e7e5"), 60, 0, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("g1f3"));
            Assert.That(process.Commands, Does.Contain("go infinite").And.Contain("stop"));
            Assert.That(timing.LeftAnalysis, Is.True);
        });
    }

    [Test]
    public async Task CancellationDrainsSearchResultBeforeEngineIsReused()
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { WaitForStop = true, BestMoves = ["bestmove g1f3", "bestmove b1c3"] });
        await using var engine = process.CreateEngine(CreateOptions(process));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var thinking = engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), cancellation.Token);
        await WaitForCommandAsync(process, "go wtime");
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await thinking);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(1800, 60_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("b1c3"));
            Assert.That(engine.IsRunning, Is.True);
            Assert.That(process.Commands.Count(command => command is "stop"), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RequestsImmediateMoveBeforeClockExpires()
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { WaitForStop = true });
        await using var engine = process.CreateEngine(CreateOptions(process));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(1800, 60_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("g1f3"));
            Assert.That(process.Commands, Does.Contain("stop"));
            Assert.That(engine.IsRunning, Is.True);
        });
    }

    [Test]
    public async Task RequiresRestartWhenStoppedSearchDoesNotFinish()
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { WaitForStop = true, OmitStopReply = true });
        await using var engine = process.CreateEngine(CreateOptions(process, settings: new UciOptions { ResponseTimeoutSeconds = 1 }));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => engine.GetMoveAsync(ChessColor.White, new ChessClock(1800, 60_000), CancellationToken.None));
        Assert.That(engine.IsRunning, Is.False);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None));
    }

    private static EngineOptions CreateOptions(EngineProcess process, bool ponder = false, UciOptions? settings = null, int overhead = 0) => new() { Path = process.ExecutablePath, Protocol = EngineProtocol.Uci, Ponder = ponder, MoveOverheadMilliseconds = overhead, Uci = settings ?? new UciOptions() };
    private static ChessMove[] Parse(string moves) => moves.Split(' ').Select(value => new ChessMove(value)).ToArray();

    private static async Task WaitForCommandAsync(EngineProcess process, string prefix)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        while (process.Commands.Any(command => command.StartsWith(prefix, StringComparison.Ordinal)) is false)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
