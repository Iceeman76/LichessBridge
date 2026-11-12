using System.Text;
using DotNetLab.Bridge;
using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DotNetLab.LichessBridge.Tests;

public sealed class ConsoleStatusDisplayTests
{
    [Test]
    public async Task ColorsEngineFreeTextByEventIdentityWithoutRecoloringOrdinaryLogsOrWarnings()
    {
        var policy = new ConsoleOutputPolicy(Options.Create(new ConsoleOutputOptions { ConsoleQuietBelowMilliseconds = 0 }), TimeProvider.System);
        using var output = new RecordingWriter();
        using var display = new ConsoleStatusDisplay(policy, CreateConsole(output));
        display.ShowLogs([new DashboardLogEntry(DateTimeOffset.UtcNow, LogLevel.Information, "UciEngine", "Alarm: Book: selected e2e4 [red]", null) { EventId = EngineLogEvents.UciInfoString }]);
        await WaitForOutputAsync(output, "Alarm: Book: selected e2e4");
        Assert.That(output.Text.Contains("\u001b[96mAlarm: Book: selected e2e4", StringComparison.Ordinal), Is.True, "Expected cyan engine free text.");
        Assert.That(output.Text.Contains("[red]", StringComparison.Ordinal), Is.True, "Engine text must be escaped rather than interpreted as console markup.");
        display.ShowLogs([new DashboardLogEntry(DateTimeOffset.UtcNow, LogLevel.Information, "UciEngine", "Alarm: Book: ordinary message", null)]);
        await WaitForOutputAsync(output, "Alarm: Book: ordinary message");
        Assert.That(output.Text.Contains("\u001b[96mAlarm: Book: ordinary message", StringComparison.Ordinal), Is.False);
        display.ShowLogs([new DashboardLogEntry(DateTimeOffset.UtcNow, LogLevel.Warning, "UciEngine", "Alarm: warning message", null) { EventId = EngineLogEvents.UciInfoString }]);
        await WaitForOutputAsync(output, "Alarm: warning message");
        Assert.That(output.Text.Contains("\u001b[96mAlarm: warning message", StringComparison.Ordinal), Is.False);
    }

    [Test]
    public async Task RendersEveryQueuedSearchSnapshotWithoutBlockingProducer()
    {
        var policy = new ConsoleOutputPolicy(Options.Create(new ConsoleOutputOptions { ConsoleQuietBelowMilliseconds = 0 }), TimeProvider.System);
        await using var process = new EngineProcess(uci: new UciTestSettings { Info = Enumerable.Range(1, 5).Select(move => $"info depth {move} time 100 nodes 1000 currmovenumber {move} currmove e2e4").ToArray() });
        await using var engine = process.CreateEngine(new EngineOptions { Protocol = EngineProtocol.Uci, Path = process.ExecutablePath }, policy);
        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 60, 0, CancellationToken.None);
        using var output = new RecordingWriter();
        using var display = new ConsoleStatusDisplay(policy, CreateConsole(output));
        engine.AnalysisUpdated += display.ShowAnalysis;
        engine.SearchStatusUpdated += display.ShowSearchStatus;
        display.ShowGame(CreateGame());
        try
        {
            await WaitForOutputAsync(output, "LIVE GAME");
            output.BlockNextWrite();
            var thinking = engine.GetMoveAsync(ChessColor.White, new ChessClock(60_000, 60_000), CancellationToken.None);
            await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var moveResponse = await thinking.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.That(moveResponse.Value, Is.EqualTo("g1f3"));
            output.Release();
            await WaitForOutputAsync(output, "move 5 / 20");

            for (var move = 1; move <= 5; move++)
            {
                Assert.That(output.Text.Contains($"move {move}", StringComparison.Ordinal), Is.True, $"Missing Search update {move}.");
            }
        }
        finally
        {
            output.Release();
        }
    }

    [Test]
    public async Task DiscardsPendingFramesDuringQuietModeAndResumesLatestStatus()
    {
        var policy = new ConsoleOutputPolicy(Options.Create(new ConsoleOutputOptions()), TimeProvider.System);
        using var output = new RecordingWriter();
        using var display = new ConsoleStatusDisplay(policy, CreateConsole(output));
        display.ShowGame(CreateGame());
        try
        {
            await WaitForOutputAsync(output, "LIVE GAME");
            output.BlockNextWrite();
            display.ShowSearchStatus(CreateSearch(1));
            await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            display.ShowSearchStatus(CreateSearch(2));
            display.UpdateClock(new GameClockStatus("local", ChessColor.White, new ChessClock(1000, 60_000), ChessColor.White, false));
            display.ShowSearchStatus(CreateSearch(3));
            output.Release();
            await Task.Delay(100);
            Assert.That(output.Text, Does.Not.Contain("move 2 / 20").And.Not.Contain("move 3 / 20"));
            display.UpdateClock(new GameClockStatus("local", ChessColor.White, new ChessClock(16_000, 60_000), ChessColor.White, false));
            await WaitForOutputAsync(output, "move 3 / 20");
            Assert.That(output.Text, Does.Not.Contain("move 2 / 20"));
        }
        finally
        {
            output.Release();
        }
    }

    [Test]
    public void QuietModeDoesNotPrepareSanForBufferedGameHistory()
    {
        var policy = new ConsoleOutputPolicy(Options.Create(new ConsoleOutputOptions()), TimeProvider.System);
        policy.Update(new GameClockStatus("local", ChessColor.White, new ChessClock(1000, 60_000), ChessColor.White, false));
        using var display = new ConsoleStatusDisplay(policy);
        var status = new GameStatus("local", "casual", "blitz", "5+0", "Bot", null, null, "Opponent", null, null, "e2e5", 1000, 60_000, ChessColor.White) { MoveHistory = [new ChessMove("e2e5")] };

        display.ShowGame(status);

        Assert.That(policy.IsQuiet, Is.True);
    }

    [Test]
    public async Task ConcurrentPublicUpdatesDoNotOverflowRenderSignalOrCrashRenderer()
    {
        var policy = new ConsoleOutputPolicy(Options.Create(new ConsoleOutputOptions { ConsoleQuietBelowMilliseconds = 0 }), TimeProvider.System);
        using var display = new ConsoleStatusDisplay(policy);
        var analysis = new EngineAnalysis(12, 20, 100, 1000, "e2e4");
        var search = new EngineSearchStatus(100, 1000, 12, 1, 20, "e2e4");

        for (var batch = 0; batch < 3; batch++)
        {
            await Task.Run(() => Parallel.For(0, 100_000, index =>
            {
                switch (index % 4)
                {
                    case 0:
                        display.ShowAnalysis(analysis);
                        break;
                    case 1:
                        display.ShowAnalysis(null);
                        break;
                    case 2:
                        display.ShowSearchStatus(search);
                        break;
                    default:
                        display.ShowHint("e7e5");
                        break;
                }
            })).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(75);
        }
    }

    [Test]
    public async Task LateCallbacksAfterDisposeDoNotReleaseDisposedSignal()
    {
        var policy = new ConsoleOutputPolicy(Options.Create(new ConsoleOutputOptions()), TimeProvider.System);
        var display = new ConsoleStatusDisplay(policy);
        display.ShowHint("e7e5");
        display.Dispose();

        await Task.Run(() => Parallel.For(0, 100_000, index => display.ShowAnalysis(new EngineAnalysis(12, 20, 100, index, "e2e4")))).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static GameStatus CreateGame() => new("local", "casual", "blitz", "5+0", "Bot", null, null, "Opponent", null, null, "e4", 60_000, 60_000, ChessColor.White);
    private static EngineSearchStatus CreateSearch(int move) => new(100, 1000, 12, 20 - move, 20, "e2e4") { CurrentMoveNumber = move };

    private static IAnsiConsole CreateConsole(TextWriter output)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes, ColorSystem = ColorSystemSupport.Standard });
        console.Profile.Width = 120;
        console.Profile.Height = 50;

        return console;
    }

    private static async Task WaitForOutputAsync(RecordingWriter output, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        while (output.Text.Contains(expected, StringComparison.Ordinal) is false)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingWriter : TextWriter
    {
        private readonly Lock contentLock = new();
        private readonly StringBuilder content = new();
        private readonly ManualResetEventSlim release = new(true);
        private int blockNext;
        public override Encoding Encoding => Encoding.UTF8;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Text
        {
            get
            {
                lock (contentLock)
                {
                    return content.ToString();
                }
            }
        }

        public void BlockNextWrite()
        {
            release.Reset();
            Volatile.Write(ref blockNext, 1);
        }

        public void Release() => release.Set();
        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            if (Interlocked.Exchange(ref blockNext, 0) is 1)
            {
                Blocked.TrySetResult();
                release.Wait();
            }

            lock (contentLock)
            {
                content.Append(value);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                release.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
