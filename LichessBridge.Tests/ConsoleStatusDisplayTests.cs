using DotNetLab.Bridge;
using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Tests;

public sealed class ConsoleStatusDisplayTests
{
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
}
