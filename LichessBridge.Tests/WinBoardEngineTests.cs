using LichessBridge.Engine;
using LichessBridge.Configuration;
using LichessBridge.Domain;
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
    public async Task CompletesWinBoardHandshakeAndReadsMoveFromProcess()
    {
        var enginePath = FindFakeEngine();
        await using var engine = new WinBoardEngine(
            new EngineOptions
            {
                Path = enginePath,
                Name = "Fake",
                Command = "xboard",
                StartupTimeoutSeconds = 5,
                MoveTimeoutSeconds = 5
            },
            NullLogger<WinBoardEngine>.Instance);

        await engine.StartAsync(CancellationToken.None);
        await engine.SetPositionAsync([], 300, 3, CancellationToken.None);
        var move = await engine.GetMoveAsync(
            ChessColor.White,
            new ChessClock(300_000, 300_000),
            CancellationToken.None);
        await engine.SendResultAsync(new GameResult("1-0", "mate"), CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        Assert.That(move.Value, Is.EqualTo("e2e4"));
    }

    private static string FindFakeEngine()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LichessBridge.slnx")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null);

        var executable = OperatingSystem.IsWindows() ? "FakeWinBoard.exe" : "FakeWinBoard";
        var path = Path.Combine(root!.FullName, "LichessBridge.Tests", "FakeWinBoard", "bin", configuration, "net10.0", executable);
        Assert.That(File.Exists(path), Is.True, $"Fake engine not found at {path}");
        return path;
    }
}
