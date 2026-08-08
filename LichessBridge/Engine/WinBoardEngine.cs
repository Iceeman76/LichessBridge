using System.Globalization;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LichessBridge.Engine;

public sealed class WinBoardEngineFactory(IOptions<EngineOptions> options, ILoggerFactory loggerFactory) : IChessEngineFactory
{
    public IChessEngine Create()
    {
        var logger = loggerFactory.CreateLogger<WinBoardEngine>();
        var transport = new ProcessEngineTransport(options.Value, logger);

        return new WinBoardEngine(options.Value, logger, transport);
    }
}

public sealed class WinBoardEngine : IChessEngine
{
    private readonly EngineOptions options;
    private readonly ILogger<WinBoardEngine> logger;
    private readonly IEngineTransport transport;
    private bool isStarted;
    private bool isStopped;

    internal WinBoardEngine(EngineOptions options, ILogger<WinBoardEngine> logger, IEngineTransport transport)
    {
        this.options = options;
        this.logger = logger;
        this.transport = transport;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (isStarted)
        {
            throw new InvalidOperationException("The engine has already been started.");
        }

        if (File.Exists(options.Path) is false && transport is ProcessEngineTransport)
        {
            throw new FileNotFoundException("Chess engine executable was not found.", options.Path);
        }

        await transport.StartAsync(cancellationToken);
        isStarted = true;

        if (string.IsNullOrWhiteSpace(options.Command) is false)
        {
            await SendAsync(options.Command, cancellationToken);
        }

        await SendAsync("protover 2", cancellationToken);
        await CompleteHandshakeAsync(cancellationToken);
    }

    public async Task SetPositionAsync(IReadOnlyList<ChessMove> moves, int initialSeconds, int incrementSeconds, CancellationToken cancellationToken)
    {
        EnsureRunning();
        await SendAsync("new", cancellationToken);
        await SendAsync("force", cancellationToken);
        await SendAsync($"level 0 {FormatBaseTime(initialSeconds)} {incrementSeconds}", cancellationToken);

        foreach (var move in moves)
        {
            await SendAsync($"usermove {move.Value}", cancellationToken);
        }
    }

    public async Task<ChessMove> GetMoveAsync(ChessColor engineColor, ChessClock clock, CancellationToken cancellationToken)
    {
        EnsureRunning();
        var opponentColor = engineColor == ChessColor.White ? ChessColor.Black : ChessColor.White;
        await SendAsync($"time {MillisecondsToCentiseconds(clock.For(engineColor))}", cancellationToken);
        await SendAsync($"otim {MillisecondsToCentiseconds(clock.For(opponentColor))}", cancellationToken);
        await SendAsync("go", cancellationToken);

        var move = await ReceiveMoveAsync(cancellationToken);
        await SendAsync("force", cancellationToken);

        return move;
    }

    public Task SendResultAsync(GameResult result, CancellationToken cancellationToken)
    {
        var reason = result.Reason.Replace('{', '(').Replace('}', ')');

        return SendAsync($"result {result.Result} {{{reason}}}", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (isStarted is false || isStopped)
        {
            return;
        }

        isStopped = true;

        if (transport.IsRunning)
        {
            await SendAsync("quit", cancellationToken);
        }

        await transport.StopAsync(cancellationToken);
        logger.LogInformation("Chess engine stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        await transport.DisposeAsync();
    }

    internal static int MillisecondsToCentiseconds(int milliseconds) => Math.Max(0, milliseconds / 10);

    private async Task CompleteHandshakeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
        try
        {
            while (true)
            {
                var line = await transport.ReceiveAsync(timeout.Token);

                if (line.StartsWith("feature ", StringComparison.OrdinalIgnoreCase) && line.Contains("myname=", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Engine features: {Features}", line[8..]);
                }

                if (line.StartsWith("feature ", StringComparison.OrdinalIgnoreCase) && line.Contains("done=1", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
        {
            throw new TimeoutException("The engine did not complete its WB/2 feature handshake.");
        }
    }

    private async Task<ChessMove> ReceiveMoveAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.MoveTimeoutSeconds));
        try
        {
            while (true)
            {
                var line = await transport.ReceiveAsync(timeout.Token);

                if (line.StartsWith("move ", StringComparison.OrdinalIgnoreCase) is false)
                {
                    continue;
                }

                var moveValue = line[5..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

                if (ChessMove.TryParse(moveValue, out var move) is false)
                {
                    throw new InvalidDataException($"Engine returned invalid move '{moveValue}'.");
                }

                return move;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
        {
            throw new TimeoutException($"Engine did not return a move within {options.MoveTimeoutSeconds} seconds.");
        }
    }

    private async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        EnsureRunning();
        await transport.SendAsync(command, cancellationToken);
    }

    private void EnsureRunning()
    {
        if (isStarted is false)
        {
            throw new InvalidOperationException("The engine has not been started.");
        }

        if (transport.IsRunning is false)
        {
            throw new InvalidOperationException($"The engine exited with code {transport.ExitCode}.");
        }
    }

    private static string FormatBaseTime(int seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
}
