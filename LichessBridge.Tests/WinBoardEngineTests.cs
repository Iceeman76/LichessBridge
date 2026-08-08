using System.Threading.Channels;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Engine;
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
            Assert.That(transport.Commands, Is.EqualTo(new[]
            {
                "xboard", "protover 2", "new", "force", "level 0 5:00 3", "usermove e2e4", "usermove e7e5",
                "time 29800", "otim 29900", "go", "force", "result 1-0 {mate}", "quit"
            }));
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

    private static WinBoardEngine CreateEngine(IEngineTransport transport)
    {
        var options = new EngineOptions
        {
            Path = "in-memory-engine",
            Name = "Fake",
            Command = "xboard",
            StartupTimeoutSeconds = 1,
            MoveTimeoutSeconds = 1
        };

        return new WinBoardEngine(options, NullLogger<WinBoardEngine>.Instance, transport);
    }

    private sealed class FakeEngineTransport(string moveResponse = "move g1f3") : IEngineTransport
    {
        private readonly Channel<string> responses = Channel.CreateUnbounded<string>();

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
                await responses.Writer.WriteAsync("feature myname=\"Fake\" usermove=1 done=1", cancellationToken);
            }

            if (command == "go")
            {
                await responses.Writer.WriteAsync(moveResponse, cancellationToken);
            }

            if (command == "quit")
            {
                IsRunning = false;
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
