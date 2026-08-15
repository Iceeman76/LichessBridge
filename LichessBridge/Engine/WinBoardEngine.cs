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
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MoveTimeoutGrace = TimeSpan.FromSeconds(10);
    public bool IsRunning => transport.IsRunning;
    private readonly EngineOptions options;
    private readonly ILogger<WinBoardEngine> logger;
    private readonly IEngineTransport transport;
    private bool isStarted;
    private bool isStopped;
    private bool usesUserMoveCommand;
    private bool isPositionInitialized;
    private bool isPondering;
    private bool isAnalyzing;
    private IReadOnlyList<ChessMove> currentPosition = [];
    private IReadOnlyList<ChessMove> pendingMoves = [];
    private readonly CancellationTokenSource pollingStop = new();
    private Task? pollingTask;
    private int searchActive;

    public event Action<EngineAnalysis>? AnalysisUpdated;
    public event Action<EngineSearchStatus>? SearchStatusUpdated;
    public event Action<string>? HintUpdated;

    internal WinBoardEngine(EngineOptions options, ILogger<WinBoardEngine> logger, IEngineTransport transport)
    {
        this.options = options;
        this.logger = logger;
        this.transport = transport;
        transport.OutputReceived += HandleOutput;
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
        await SendAsync("xboard", cancellationToken);
        await SendAsync("protover 2", cancellationToken);
        await CompleteHandshakeAsync(cancellationToken);
        await SendAsync("post", cancellationToken);

        foreach (var command in options.StartupCommands)
        {
            await SendAsync(command, cancellationToken);
        }

        if (options.SearchStatusPollMilliseconds > 0)
        {
            pollingTask = PollSearchStatusAsync(TimeSpan.FromMilliseconds(options.SearchStatusPollMilliseconds), pollingStop.Token);
        }
    }

    public async Task SetPositionAsync(IReadOnlyList<ChessMove> moves, int initialSeconds, int incrementSeconds, CancellationToken cancellationToken)
    {
        EnsureRunning();

        if (isAnalyzing)
        {
            await SendAsync("exit", cancellationToken);
            isAnalyzing = false;
        }

        Interlocked.Exchange(ref searchActive, 0);
        var canContinuePosition = isPositionInitialized && StartsWith(moves, currentPosition) && (options.Ponder is false || moves.Count - currentPosition.Count <= 1);

        if (canContinuePosition)
        {
            pendingMoves = moves.Skip(currentPosition.Count).ToArray();

            return;
        }

        await SendAsync("new", cancellationToken);

        if (options.Ponder)
        {
            await SendAsync("hard", cancellationToken);
        }

        await SendAsync("force", cancellationToken);
        await SendAsync($"level 0 {FormatBaseTime(initialSeconds)} {incrementSeconds}", cancellationToken);

        foreach (var move in moves)
        {
            await SendAsync(FormatMoveCommand(move), cancellationToken);
        }

        currentPosition = moves.ToArray();
        pendingMoves = [];
        isPositionInitialized = true;
        isPondering = false;
    }

    public async Task StartPonderingAsync(CancellationToken cancellationToken)
    {
        EnsureRunning();

        if (options.Ponder is false || isPositionInitialized is false || isAnalyzing)
        {
            return;
        }

        if (isPondering)
        {
            await ActivateSearchStatusAsync(cancellationToken);

            return;
        }

        await SendAsync("analyze", cancellationToken);
        isAnalyzing = true;
        await ActivateSearchStatusAsync(cancellationToken);
    }

    public async Task<ChessMove> GetMoveAsync(ChessColor engineColor, ChessClock clock, CancellationToken cancellationToken)
    {
        EnsureRunning();
        var opponentColor = engineColor == ChessColor.White ? ChessColor.Black : ChessColor.White;
        var engineTime = Math.Max(0, clock.For(engineColor) - options.MoveOverheadMilliseconds);
        await SendAsync($"time {MillisecondsToCentiseconds(engineTime)}", cancellationToken);
        await SendAsync($"otim {MillisecondsToCentiseconds(clock.For(opponentColor))}", cancellationToken);

        foreach (var pendingMove in pendingMoves)
        {
            await SendAsync(FormatMoveCommand(pendingMove), cancellationToken);
        }

        currentPosition = currentPosition.Concat(pendingMoves).ToArray();
        var continuesPondering = options.Ponder && isPondering && pendingMoves.Count == 1;
        pendingMoves = [];

        if (continuesPondering is false)
        {
            await SendAsync("go", cancellationToken);
        }

        await ActivateSearchStatusAsync(cancellationToken);
        var moveTimeout = CalculateMoveTimeout(clock, engineColor);
        var move = await ReceiveMoveAsync(moveTimeout, cancellationToken);
        currentPosition = currentPosition.Append(move).ToArray();
        isPondering = options.Ponder;

        if (options.Ponder)
        {
            await SendAsync("hint", cancellationToken);
            await ActivateSearchStatusAsync(cancellationToken);
        }

        if (options.Ponder is false)
        {
            Interlocked.Exchange(ref searchActive, 0);
            await SendAsync("force", cancellationToken);
            isPondering = false;
        }

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
        Interlocked.Exchange(ref searchActive, 0);
        pollingStop.Cancel();

        if (pollingTask is not null)
        {
            await pollingTask;
        }

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
        pollingStop.Dispose();
    }

    internal static int MillisecondsToCentiseconds(int milliseconds) => Math.Max(0, milliseconds / 10);

    internal static TimeSpan CalculateMoveTimeout(ChessClock clock, ChessColor engineColor) => TimeSpan.FromMilliseconds(Math.Max(0, clock.For(engineColor))) + MoveTimeoutGrace;

    private async Task CompleteHandshakeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartupTimeout);
        try
        {
            while (true)
            {
                var line = await transport.ReceiveAsync(timeout.Token);

                if (line.StartsWith("feature ", StringComparison.OrdinalIgnoreCase) && line.Contains("myname=", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Engine features: {Features}", line[8..]);
                }

                if (HasFeature(line, "usermove", "1"))
                {
                    usesUserMoveCommand = true;
                }

                if (HasFeature(line, "usermove", "0"))
                {
                    usesUserMoveCommand = false;
                }

                if (HasFeature(line, "san", "1"))
                {
                    await SendAsync("rejected san", cancellationToken);
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

    private async Task<ChessMove> ReceiveMoveAsync(TimeSpan moveTimeout, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(moveTimeout);
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

                if (ChessMove.TryParse(moveValue, out var move))
                {
                    return move;
                }

                return SanMoveConverter.ConvertToUci(moveValue, currentPosition);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
        {
            throw new TimeoutException("Engine did not return a move within its remaining clock time plus the safety margin.");
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

    private static bool StartsWith(IReadOnlyList<ChessMove> position, IReadOnlyList<ChessMove> prefix) => prefix.Count <= position.Count && prefix.Select((move, index) => move == position[index]).All(matches => matches);

    private string FormatMoveCommand(ChessMove move) => usesUserMoveCommand ? $"usermove {move.Value}" : move.Value;

    private static bool HasFeature(string line, string name, string value) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains($"{name}={value}", StringComparer.OrdinalIgnoreCase);

    private void HandleOutput(string line)
    {
        if (EngineSearchStatus.TryParse(line, out var searchStatus) && searchStatus is not null)
        {
            SearchStatusUpdated?.Invoke(searchStatus);

            return;
        }

        if (line.StartsWith("Hint:", StringComparison.OrdinalIgnoreCase))
        {
            var hint = line[5..].Trim();

            if (string.IsNullOrWhiteSpace(hint) is false)
            {
                HintUpdated?.Invoke(hint);
            }

            return;
        }

        if (EngineAnalysis.TryParse(line, out var analysis) && analysis is not null)
        {
            AnalysisUpdated?.Invoke(analysis);
        }
    }

    private async Task PollSearchStatusAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (Volatile.Read(ref searchActive) is 1 && transport.IsRunning)
                {
                    await SendAsync(".", cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ActivateSearchStatusAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref searchActive, 1);

        if (options.SearchStatusPollMilliseconds > 0)
        {
            await SendAsync(".", cancellationToken);
        }
    }
}
