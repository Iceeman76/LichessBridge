using System.Collections.Concurrent;
using System.Diagnostics;
using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Diagnostics;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace DotNetLab.LichessBridge.Tests;

public sealed class UciEngineTests
{
    [Test]
    public async Task SlowInfoStringLoggerDoesNotBlockReadingBestMove()
    {
        using var release = new ManualResetEventSlim(false);
        var loggingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var process = new EngineProcess(uci: new UciTestSettings { Info = ["info string Book: selected g1f3"] })
        {
            BeforeInfoMessage = () =>
            {
                loggingStarted.TrySetResult();
                release.Wait();
            }
        };
        await using var engine = process.CreateEngine(CreateOptions(process));
        try
        {
            await engine.StartAsync(CancellationToken.None);
            await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
            var thinking = engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
            await loggingStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var move = await thinking.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.That(move.Value, Is.EqualTo("g1f3"));
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public async Task RetainsInfoStringLogsWhileConsoleIsQuiet()
    {
        var policy = new ConsoleOutputPolicy(Options.Create(new ConsoleOutputOptions()), TimeProvider.System);
        policy.Update(new GameClockStatus("local", ChessColor.White, new ChessClock(1000, 60_000), ChessColor.White, false));
        await using var process = new EngineProcess(uci: new UciTestSettings { Info = ["info string Book: selected e2e4"] });
        await using (var engine = process.CreateEngine(new EngineOptions { Path = process.ExecutablePath, Protocol = EngineProtocol.Uci, Name = "Alarm" }, policy))
        {
            await engine.StartAsync(CancellationToken.None);
            await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
            await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        }

        Assert.That(policy.IsQuiet, Is.True);
        Assert.That(process.InfoMessages.Single().Message, Is.EqualTo("Alarm: Book: selected e2e4"));
    }

    [TestCase(LogLevel.Information)]
    [TestCase(LogLevel.Debug)]
    [TestCase(LogLevel.Warning)]
    [TestCase(LogLevel.None)]
    public async Task LogsUciFreeTextAtConfiguredLevelWithoutChangingAnalysis(LogLevel level)
    {
        var settings = new UciTestSettings { Info = ["info depth 12 score cp 25 nodes 100", "info string Book: selected e2e4", "\tinfo\tstring\tNNUE loaded [network] successfully", "info string"] };
        await using var process = new EngineProcess(uci: settings);
        var options = new EngineOptions { Path = process.ExecutablePath, Protocol = EngineProtocol.Uci, Name = "Alarm" };
        EngineAnalysis? received = null;
        var updates = 0;
        await using (var engine = process.CreateEngine(options, loggingOptions: new EngineLoggingOptions { EngineInfoStringLogLevel = level }))
        {
            engine.AnalysisUpdated += analysis =>
            {
                received = analysis;
                updates++;
            };
            await engine.StartAsync(CancellationToken.None);
            await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
            await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        }

        Assert.That(updates, Is.EqualTo(1));
        Assert.That(received?.ScoreCentipawns, Is.EqualTo(25));

        if (level is LogLevel.None)
        {
            Assert.That(process.InfoMessages, Is.Empty);
        }
        else
        {
            Assert.That(process.InfoMessages.Select(message => message.Level), Is.All.EqualTo(level));
            Assert.That(process.InfoMessages.Select(message => message.Message), Is.EqualTo(new[] { "Alarm: Book: selected e2e4", "Alarm: NNUE loaded [network] successfully" }));
        }
    }

    [TestCase(ChessColor.White, "cp", -6000, 6000)]
    [TestCase(ChessColor.Black, "cp", -6000, 6000)]
    [TestCase(ChessColor.White, "cp", 6000, -6000)]
    [TestCase(ChessColor.Black, "cp", 6000, -6000)]
    [TestCase(ChessColor.White, "mate", -3, 3)]
    [TestCase(ChessColor.Black, "mate", -3, 3)]
    [TestCase(ChessColor.White, "mate", 3, -3)]
    [TestCase(ChessColor.Black, "mate", 3, -3)]
    public async Task NormalizesOpponentTurnAnalysisWithoutFlippingPartialUpdatesTwice(ChessColor color, string scoreType, int rawScore, int expected)
    {
        var ownMove = color is ChessColor.White ? "g1f3" : "c7c5";
        var opponentMove = color is ChessColor.White ? "g8f6" : "g1f3";
        var reply = color is ChessColor.White ? "b1c3" : "g8f6";
        var before = color is ChessColor.White ? Array.Empty<ChessMove>() : Parse("e2e4");
        var settings = new UciTestSettings
        {
            BestMoves = [$"bestmove {ownMove}", $"bestmove {reply}"],
            InfoBySearch =
            [
                ["info depth 10 score cp 25 nodes 1"],
                [$"info depth 11 score {scoreType} {rawScore} nodes 2", "info nodes 3 time 20"],
                ["info depth 12 score cp -100 nodes 4"]
            ]
        };
        await using var process = new EngineProcess(uci: settings);
        await using var engine = process.CreateEngine(CreateOptions(process, ponder: true));
        var ownAnalysis = new TaskCompletionSource<EngineAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opponentAnalysis = new TaskCompletionSource<EngineAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedAnalysis = new TaskCompletionSource<EngineAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AnalysisUpdated += analysis =>
        {
            if (analysis.Depth is 10)
            {
                ownAnalysis.TrySetResult(analysis);
            }

            if (analysis.Depth is 11 && analysis.Nodes is 2)
            {
                opponentAnalysis.TrySetResult(analysis);
            }

            if (analysis.Depth is 12)
            {
                resumedAnalysis.TrySetResult(analysis);
            }
        };
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync(before, 60, 0, CancellationToken.None);
        var move = await engine.GetMoveAsync(color, new ChessClock(60_000, 60_000), CancellationToken.None);
        await engine.SetPositionAsync([.. before, move], 60, 0, CancellationToken.None);
        await engine.StartPonderingAsync(CancellationToken.None);
        var own = await ownAnalysis.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var opponent = await opponentAnalysis.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await engine.SetPositionAsync([.. before, move, new ChessMove(opponentMove)], 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(color, new ChessClock(60_000, 60_000), CancellationToken.None);
        var resumed = await resumedAnalysis.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(own.ScoreCentipawns, Is.EqualTo(25));
        Assert.That(resumed.ScoreCentipawns, Is.EqualTo(-100));
        Assert.That(resumed.MateInMoves, Is.Null);

        if (scoreType is "cp")
        {
            Assert.That(opponent.ScoreCentipawns, Is.EqualTo(expected));
            Assert.That(opponent.MateInMoves, Is.Null);
        }
        else
        {
            Assert.That(opponent.MateInMoves, Is.EqualTo(expected));
            Assert.That(opponent.ScoreCentipawns, Is.EqualTo(0));
        }
    }

    [TestCase(ChessColor.White)]
    [TestCase(ChessColor.Black)]
    public async Task InfersOurPerspectiveBeforeFirstMoveWhenStartingInOpponentTurn(ChessColor color)
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { Info = ["info depth 12 score cp -6000 nodes 1"] });
        await using var engine = process.CreateEngine(CreateOptions(process, ponder: true));
        var received = new TaskCompletionSource<EngineAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AnalysisUpdated += analysis => received.TrySetResult(analysis);
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync(color is ChessColor.White ? Parse("e2e4") : [], 60, 0, CancellationToken.None);
        await engine.StartPonderingAsync(CancellationToken.None);

        var analysis = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(analysis.ScoreCentipawns, Is.EqualTo(6000));
        Assert.That(StatusFormatter.FormatAnalysis(analysis), Does.Contain("+60.00"));
    }

    [TestCase(ChessColor.White)]
    [TestCase(ChessColor.Black)]
    public async Task PreservesOurPerspectiveDuringPredictedPonderAndPonderhit(ChessColor color)
    {
        var before = color is ChessColor.White ? Array.Empty<ChessMove>() : Parse("e2e4");
        var ownMove = color is ChessColor.White ? "g1f3" : "c7c5";
        var prediction = color is ChessColor.White ? "g8f6" : "g1f3";
        var reply = color is ChessColor.White ? "b1c3" : "b8c6";
        var settings = new UciTestSettings
        {
            BestMoves = [$"bestmove {ownMove} ponder {prediction}", $"bestmove {reply}"],
            InfoBySearch = [["info depth 10 score cp 25 nodes 1"], ["info depth 11 score cp 6000 nodes 2"]]
        };
        await using var process = new EngineProcess(uci: settings);
        await using var engine = process.CreateEngine(CreateOptions(process, ponder: true));
        var received = new TaskCompletionSource<EngineAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AnalysisUpdated += analysis =>
        {
            if (analysis.Depth is 11)
            {
                received.TrySetResult(analysis);
            }
        };
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync(before, 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(color, new ChessClock(60_000, 60_000), CancellationToken.None);
        await engine.SetPositionAsync([.. before, new ChessMove(ownMove)], 60, 0, CancellationToken.None);
        await engine.StartPonderingAsync(CancellationToken.None);
        await engine.SetPositionAsync([.. before, new ChessMove(ownMove), new ChessMove(prediction)], 60, 0, CancellationToken.None);
        var move = await engine.GetMoveAsync(color, new ChessClock(60_000, 60_000), CancellationToken.None);
        var analysis = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(analysis.ScoreCentipawns, Is.EqualTo(6000));
        Assert.That(move.Value, Is.EqualTo(reply));
        Assert.That(process.Commands, Does.Contain("ponderhit"));
    }

    [Test]
    public async Task SendsOnlyLatestPreparedPositionWhenSearchStarts()
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { BestMoves = ["bestmove c7c5"] });
        await using var engine = process.CreateEngine(CreateOptions(process));
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync(Parse("e2e4"), 60, 0, CancellationToken.None);
        await engine.SetPositionAsync(Parse("d2d4"), 60, 0, CancellationToken.None);
        Assert.That(process.Commands.Any(command => command.StartsWith("position ", StringComparison.Ordinal)), Is.False);

        await engine.GetMoveAsync(ChessColor.Black, new ChessClock(60_000, 60_000), CancellationToken.None);

        Assert.That(process.Commands.Where(command => command.StartsWith("position ", StringComparison.Ordinal)), Is.EqualTo(new[] { "position startpos moves d2d4" }));
    }

    [Test]
    public async Task SkipsRootCountInQuietModeAndRestoresItForNextSearchAfterResume()
    {
        var policy = new ConsoleOutputPolicy(Options.Create(new ConsoleOutputOptions()), TimeProvider.System);
        policy.Update(new GameClockStatus("local", ChessColor.White, new ChessClock(1000, 60_000), ChessColor.White, false));
        await using var process = new EngineProcess(uci: new UciTestSettings { Info = ["info depth 12 currmovenumber 18 currmove g1f3"] });
        await using var engine = process.CreateEngine(CreateOptions(process), policy);
        var received = new TaskCompletionSource<EngineSearchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new ConcurrentQueue<EngineSearchStatus>();
        engine.SearchStatusUpdated += status =>
        {
            statuses.Enqueue(status);

            if (status.TotalMoves is 29 && status.CurrentMoveNumber is 18)
            {
                received.TrySetResult(status);
            }
        };
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        Assert.That(statuses, Is.Not.Empty.And.All.Property(nameof(EngineSearchStatus.TotalMoves)).EqualTo(0));
        policy.Update(new GameClockStatus("local", ChessColor.White, new ChessClock(60_000, 60_000), ChessColor.White, false));
        await engine.SetPositionAsync(Parse("e2e4 e7e5"), 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);

        var resumed = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(StatusFormatter.FormatSearchStatus(resumed), Does.Contain("move 18 / 29"));
    }

    [TestCase("", 20)]
    [TestCase("e2e4", 20)]
    [TestCase("e2e4 e7e5", 29)]
    [TestCase("e2e4 e7e5 g1f3 b8c6 f1c4 g8f6", 33)]
    [TestCase("a2a4 h7h5 a4a5 h5h4 a5a6 h4h3 a6b7 h3g2", 31)]
    public async Task DisplaysCurrentMoveAndLegalRootMoveCount(string history, int expectedTotal)
    {
        await using var process = new EngineProcess(uci: new UciTestSettings { Info = ["info depth 12 currmovenumber 18 currmove g1f3"] });
        await using var engine = process.CreateEngine(CreateOptions(process));
        var received = new TaskCompletionSource<EngineSearchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SearchStatusUpdated += status =>
        {
            if (status.CurrentMoveNumber is 18 && status.TotalMoves == expectedTotal)
            {
                received.TrySetResult(status);
            }
        };
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync(history.Length is 0 ? [] : Parse(history), 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);

        var status = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(StatusFormatter.FormatSearchStatus(status), Does.Contain($"move 18 / {expectedTotal}"));
    }

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
    public async Task PublishesOnlyExactDepthResultsAsAnalysis()
    {
        var settings = new UciTestSettings
        {
            Info =
            [
                "info depth 12 score cp 25 time 20 nodes 100 pv g1f3 g8f6",
                "info depth 13 currmove b1c3 currmovenumber 2 nodes 200",
                "info depth 13 score cp 50 lowerbound nodes 300 pv b1c3",
                "info depth 13 score cp -50 upperbound nodes 400 pv a2a3",
                "info depth 13 score cp 60 lowerbound",
                "info depth 13 score cp -60 upperbound",
                "info depth 99 multipv 2 score cp 999 nodes 9999 pv a2a3",
                "info depth 14 score cp 30 time 80 nodes 800",
                "info score cp 90",
                "info depth 15 nodes 900 time 90"
            ]
        };
        await using var process = new EngineProcess(uci: settings);
        await using var engine = process.CreateEngine(CreateOptions(process));
        var analyses = new ConcurrentQueue<EngineAnalysis>();
        var statuses = new ConcurrentQueue<EngineSearchStatus>();
        engine.AnalysisUpdated += analyses.Enqueue;
        engine.SearchStatusUpdated += statuses.Enqueue;
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(analyses.Select(analysis => analysis.Depth), Is.EqualTo(new[] { 12, 14 }));
            Assert.That(analyses.Select(analysis => analysis.ScoreCentipawns), Is.EqualTo(new[] { 25, 30 }));
            Assert.That(analyses.Select(analysis => analysis.Nodes), Is.EqualTo(new long[] { 100, 800 }));
            Assert.That(analyses.Select(analysis => analysis.PrincipalVariation), Is.EqualTo(new[] { "g1f3 g8f6", string.Empty }));
            Assert.That(statuses, Has.Some.Matches<EngineSearchStatus>(status => status is { Depth: 13, Nodes: 400 }));
            Assert.That(statuses, Has.Some.Matches<EngineSearchStatus>(status => status is { Depth: 15, Nodes: 900 }));
            Assert.That(statuses, Has.None.Property(nameof(EngineSearchStatus.Depth)).EqualTo(99));
        });
    }

    [Test]
    public async Task KeepsAnalysisSnapshotWhilePublishingLiveSearchStatusWithoutPolling()
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
            if (analysis.Depth is 12)
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
            Assert.That(analysis.Nodes, Is.EqualTo(100));
            Assert.That(analysis.TimeCentiseconds, Is.EqualTo(2));
            Assert.That(analysis.TablebaseHits, Is.Null);
            Assert.That(analysis.PrincipalVariation, Is.EqualTo("g1f3 g8f6"));
            Assert.That(status.Nodes, Is.EqualTo(300));
            Assert.That(status.TimeCentiseconds, Is.EqualTo(9));
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
        await using var process = new EngineProcess(uci: new UciTestSettings { BestMoves = ["bestmove g1f3 ponder g8f6", "bestmove b1c3"], Info = ["info depth 12 currmovenumber 18 currmove b1c3"] });
        await using var engine = process.CreateEngine(CreateOptions(process, ponder: true));
        var ponderStatus = new TaskCompletionSource<EngineSearchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SearchStatusUpdated += status =>
        {
            if (status.TotalMoves is 22 && status.CurrentMoveNumber is 18)
            {
                ponderStatus.TrySetResult(status);
            }
        };
        var hint = string.Empty;
        engine.HintUpdated += value => hint = value;
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 2, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        Assert.That(process.Commands.Last(), Does.StartWith("go wtime"));
        await engine.SetPositionAsync(Parse("g1f3"), 60, 2, CancellationToken.None);
        var beforePonderCommands = process.Commands.Count;
        await engine.StartPonderingAsync(new ChessClock(61_000, 59_000), CancellationToken.None);
        Assert.That(process.Commands.Skip(beforePonderCommands), Is.EqualTo(new[] { "position startpos moves g1f3 g8f6", "go ponder wtime 61000 btime 59000 winc 2000 binc 2000" }));
        Assert.That(process.Commands, Does.Not.Contain("position startpos moves g1f3"));
        var commandCount = process.Commands.Count;
        using var timing = new MoveTiming("local", 3, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        timing.Activate();
        await engine.SetPositionAsync(Parse("g1f3 g8f6"), 60, 2, CancellationToken.None);

        var move = await engine.GetMoveAsync(ChessColor.White, new ChessClock(61_000, 58_000), CancellationToken.None);
        var status = await ponderStatus.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(move.Value, Is.EqualTo("b1c3"));
            Assert.That(hint, Is.EqualTo("g8f6"));
            Assert.That(process.Commands, Does.Contain("position startpos moves g1f3 g8f6"));
            Assert.That(process.Commands, Does.Contain("go ponder wtime 61000 btime 59000 winc 2000 binc 2000"));
            Assert.That(process.Commands.Skip(commandCount), Is.EqualTo(new[] { "ponderhit" }));
            Assert.That(timing.SearchPath, Is.EqualTo("continue-pondering"));
            Assert.That(timing.WasPondering, Is.True);
            Assert.That(StatusFormatter.FormatSearchStatus(status), Does.Contain("move 18 / 22"));
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
