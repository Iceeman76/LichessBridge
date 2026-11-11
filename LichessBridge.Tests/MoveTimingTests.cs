using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Diagnostics;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Game;
using DotNetLab.LichessBridge.Lichess;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Tests;

public sealed class MoveTimingTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task RecordsPonderContinuationAndExitFromAnalysis(bool continuePondering)
    {
        await using var process = new EngineProcess(ponderResponse: "move b1c3");
        await using var engine = process.CreateEngine(new EngineOptions { Path = process.ExecutablePath, Ponder = true, SearchStatusPollMilliseconds = 60_000 });
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);

        if (continuePondering)
        {
            await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
        }
        else
        {
            await engine.StartPonderingAsync(CancellationToken.None);
        }

        using var timing = new MoveTiming("local-ponder", 3, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        timing.Activate();
        await engine.SetPositionAsync(continuePondering ? [new ChessMove("g1f3"), new ChessMove("e7e5")] : [], 60, 0, CancellationToken.None);
        await engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(timing.SearchPath, Is.EqualTo(continuePondering ? "continue-pondering" : "new-search"));
            Assert.That(timing.WasPondering, Is.EqualTo(continuePondering));
            Assert.That(timing.LeftAnalysis, Is.EqualTo(continuePondering is false));
            Assert.That(timing.StdoutRead, Is.GreaterThan(0));
            Assert.That(timing.EngineReceived, Is.GreaterThanOrEqualTo(timing.StdoutRead));
        });
    }

    [TestCase(false, EngineProtocol.WinBoard)]
    [TestCase(true, EngineProtocol.WinBoard)]
    [TestCase(false, EngineProtocol.Uci)]
    [TestCase(true, EngineProtocol.Uci)]
    public async Task CorrelatesLocalEngineHttpAndServerClockWithoutProtocolLogging(bool ponder, EngineProtocol protocol)
    {
        await using var process = new EngineProcess(uci: protocol is EngineProtocol.Uci ? new UciTestSettings() : null);
        using var http = new HttpClient(new LocalGameHandler());
        var client = new LichessClient(http, Options.Create(new LichessOptions { BaseUri = new Uri("https://test.invalid"), Token = "never-log-this-token" }), NullLogger<LichessClient>.Instance);
        var logger = new TimingLogger();
        var directory = Path.Combine(Path.GetTempPath(), $"LichessBridge-timing-{Guid.NewGuid():N}");
        var options = new EngineOptions { Path = process.ExecutablePath, Protocol = protocol, Ponder = ponder, SearchStatusPollMilliseconds = 60_000 };
        using var engineLogs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
        var controller = new GameController(client, new ChessEngineFactory(Options.Create(options), engineLogs), Options.Create(new GamesOptions { PgnPath = Path.Combine(directory, "game.pgn") }), Options.Create(new ChatOptions()), new NullStatusDisplay(), logger);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await controller.PlayAsync("local-game", new LichessAccount("bot", "Bot"), cancellation.Token);
            var summary = logger.Entries.Single(entry => entry.Template.StartsWith("MoveTiming ", StringComparison.Ordinal));
            var clock = logger.Entries.Single(entry => entry.Template.StartsWith("ServerClock ", StringComparison.Ordinal));
            var stages = new[] { "EventHandlingMs", "SetPositionMs", "EngineSetupMs", "ResponseWaitMs", "EnginePostResponseMs", "ControllerPostResponseMs", "SubmissionMs" };

            Assert.Multiple(() =>
            {
                Assert.That(summary.Properties["GameId"], Is.EqualTo("local-game"));
                Assert.That(summary.Properties["Ply"], Is.EqualTo(1));
                Assert.That(summary.Properties["Move"]?.ToString(), Is.EqualTo("g1f3"));
                Assert.That(summary.Properties["CorrelationId"], Is.EqualTo(clock.Properties["CorrelationId"]));
                Assert.That(clock.Properties["ServerConsumedMs"], Is.EqualTo(50));
                Assert.That(summary.Properties["HttpMs"], Is.GreaterThanOrEqualTo(15d));
                Assert.That(summary.Properties["StdoutToGetMoveMs"], Is.GreaterThanOrEqualTo(0d));
                Assert.That(stages.Sum(stage => (double)summary.Properties[stage]!), Is.EqualTo((double)summary.Properties["LocalTotalMs"]!).Within(0.001));
                Assert.That(summary.Message, Does.Not.Contain("never-log-this-token"));
            });

            if (protocol is EngineProtocol.WinBoard)
            {
                Assert.That(summary.Properties["Commands"]?.ToString(), Does.Contain("time 1000;otim 1000;go;."));
                Assert.That(summary.Properties["Commands"]?.ToString(), Does.Contain(ponder ? "hint" : "force"));
            }
            else
            {
                Assert.That(summary.Properties["Commands"]?.ToString(), Does.Contain("position startpos;go wtime 10000 btime 10000 winc 0 binc 0"));
                Assert.That(summary.Properties["EngineMoveFormat"], Is.EqualTo("uci"));
            }

            TestContext.Out.WriteLine(summary.Message);
            TestContext.Out.WriteLine(clock.Message);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void RestoresAmbientCorrelationAfterNestedMeasurements()
    {
        using var outer = new MoveTiming("first", 1, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        outer.Activate();
        using (var inner = new MoveTiming("second", 2, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow))
        {
            inner.Activate();
            Assert.That(MoveTiming.Current, Is.SameAs(inner));
        }

        Assert.That(MoveTiming.Current, Is.SameAs(outer));
    }

    private sealed class LocalGameHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/stream/local-game", StringComparison.Ordinal))
            {
                const string events = """
                    {"type":"gameFull","id":"local-game","white":{"id":"bot","name":"Bot"},"black":{"id":"opponent","name":"Opponent"},"clock":{"initial":10000,"increment":0},"state":{"moves":"","wtime":10000,"btime":10000,"winc":0,"binc":0,"status":"started"}}
                    {"type":"gameState","moves":"g1f3","wtime":9950,"btime":10000,"winc":0,"binc":0,"status":"mate","winner":"white"}
                    """;

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "application/x-ndjson") };
            }

            if (path.Contains("/move/", StringComparison.Ordinal))
            {
                await Task.Delay(20, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[Result \"1-0\"]") };
        }
    }

    private sealed class TimingLogger : ILogger<GameController>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary();
            Entries.Enqueue(new LogEntry((string)properties["{OriginalFormat}"]!, properties, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(string Template, Dictionary<string, object?> Properties, string Message);

    private sealed class NullStatusDisplay : IStatusDisplay
    {
        public void ShowHeader(StatusHeader header) { }
        public void ShowGame(GameStatus game) { }
        public void ShowAnalysis(EngineAnalysis? analysis) { }
        public void ShowSearchStatus(EngineSearchStatus? searchStatus) { }
        public void ShowHint(string? hint) { }
        public void ClearGame() { }
        public void Show(string status) { }
        public void ShowQuit(bool requested) { }
        public void ShowResign(ResignationRequestResult result) { }
        public void ShowLogs(IReadOnlyList<DashboardLogEntry> entries) { }
    }
}
