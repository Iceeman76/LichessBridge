using System.Diagnostics;
using System.Globalization;
using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Diagnostics;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Engine;

public sealed class UciEngineFactory(IOptions<EngineOptions> options, ILoggerFactory loggerFactory, ConsoleOutputPolicy? outputPolicy = null) : IChessEngineFactory
{
    public IChessEngine Create()
    {
        var logger = loggerFactory.CreateLogger<UciEngine>();

        return new UciEngine(options.Value, logger, new ProcessEngineTransport(options.Value, logger, EngineProtocol.Uci), outputPolicy);
    }
}

public sealed class UciEngine : IChessEngine
{
    private static readonly TimeSpan EmergencyMoveGrace = TimeSpan.FromMilliseconds(750);
    private readonly EngineOptions options;
    private readonly ILogger<UciEngine> logger;
    private readonly IEngineTransport transport;
    private readonly ConsoleOutputPolicy? outputPolicy;
    private readonly Dictionary<string, UciOption> advertisedOptions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ChessMove> currentPosition = [];
    private IReadOnlyList<ChessMove> searchPosition = [];
    private UciSearchInfo searchInfo = new();
    private readonly SanMoveConverter legalMoveCounter = new();
    private readonly Lock moveCounterLock = new();
    private Task<int?>? legalMoveCount;
    private int searchGeneration;
    private int analysisSign = 1;
    private ChessMove? predictedMove;
    private ChessColor? engineColor;
    private ChessClock lastClock;
    private int incrementMilliseconds;
    private SearchMode searchMode;
    private bool isStarted;
    private bool isStopped;
    private bool isGameInitialized;
    private bool isUnusable;

    internal UciEngine(EngineOptions options, ILogger<UciEngine> logger, IEngineTransport transport, ConsoleOutputPolicy? outputPolicy)
    {
        this.options = options;
        this.logger = logger;
        this.transport = transport;
        this.outputPolicy = outputPolicy;
        transport.OutputReceived += HandleOutput;
    }

    public bool IsRunning => transport.IsRunning && isUnusable is false;
    public event Action<EngineAnalysis>? AnalysisUpdated;
    public event Action<EngineSearchStatus>? SearchStatusUpdated;
    public event Action<string>? HintUpdated;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (isStarted)
        {
            throw new InvalidOperationException("The engine has already been started.");
        }

        if (options.StartupCommands.Length > 0)
        {
            throw new InvalidOperationException("Configure UCI engines through Engine:Uci:Options instead of StartupCommands.");
        }

        await transport.StartAsync(cancellationToken);
        isStarted = true;
        await SendAsync("uci", cancellationToken);
        await ReadHandshakeAsync(cancellationToken);

        foreach (var (name, value) in options.Uci.Options)
        {
            if (name.Equals("Ponder", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Configure pondering through Engine:Ponder instead of Engine:Uci:Options:Ponder.");
            }

            if (advertisedOptions.TryGetValue(name, out var option) is false)
            {
                throw new InvalidOperationException($"The UCI engine does not support option '{name}'.");
            }

            await SendAsync(option.FormatCommand(value), cancellationToken);
        }

        if (advertisedOptions.TryGetValue("Ponder", out var ponderOption))
        {
            await SendAsync(ponderOption.FormatCommand(options.Ponder ? "true" : "false"), cancellationToken);
        }

        await WaitUntilReadyAsync(cancellationToken);
        logger.LogInformation("UCI engine initialized with {OptionCount} advertised options; pondering={Ponder}", advertisedOptions.Count, options.Ponder);
    }

    public async Task SetPositionAsync(IReadOnlyList<ChessMove> moves, int initialSeconds, int incrementSeconds, CancellationToken cancellationToken)
    {
        EnsureRunning();
        var continuesPondering = searchMode is SearchMode.Ponder && searchPosition.SequenceEqual(moves);

        if (MoveTiming.Current is { } timing)
        {
            timing.PonderEnabled = options.Ponder;
            timing.WasPondering = searchMode is SearchMode.Ponder;
            timing.LeftAnalysis = searchMode is SearchMode.Analysis;
        }

        if (continuesPondering is false)
        {
            await StopSearchAsync(cancellationToken);
        }

        if (isGameInitialized is false)
        {
            await SendAsync("ucinewgame", cancellationToken);
            await WaitUntilReadyAsync(cancellationToken);
            lastClock = new ChessClock(initialSeconds * 1000, initialSeconds * 1000);
            isGameInitialized = true;
        }

        if (currentPosition.SequenceEqual(moves) is false && continuesPondering is false)
        {
            predictedMove = null;
        }

        currentPosition = [.. moves];
        incrementMilliseconds = incrementSeconds * 1000;

    }

    public Task SetRatingsAsync(int engineRating, int opponentRating, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartPonderingAsync(CancellationToken cancellationToken) => StartPonderingAsync(lastClock, cancellationToken);

    public async Task StartPonderingAsync(ChessClock clock, CancellationToken cancellationToken)
    {
        EnsureRunning();

        if (options.Ponder is false || isGameInitialized is false || searchMode is not SearchMode.Idle)
        {
            return;
        }

        lastClock = clock;
        engineColor ??= currentPosition.Count % 2 is 0 ? ChessColor.Black : ChessColor.White;

        if (predictedMove is { } prediction)
        {
            searchPosition = [.. currentPosition, prediction];
            StartSearchStatus(searchPosition);
            await SendPositionAsync(searchPosition, cancellationToken);
            searchMode = SearchMode.Ponder;
            await SendAsync(FormatGo(clock, true), cancellationToken);
            HintUpdated?.Invoke(prediction.Value);
        }
        else
        {
            searchPosition = currentPosition;
            StartSearchStatus(searchPosition);
            await SendPositionAsync(searchPosition, cancellationToken);
            searchMode = SearchMode.Analysis;
            await SendAsync("go infinite", cancellationToken);
        }
    }

    public async Task<ChessMove> GetMoveAsync(ChessColor color, ChessClock clock, CancellationToken cancellationToken)
    {
        EnsureRunning();

        if (isGameInitialized is false)
        {
            throw new InvalidOperationException("Set the position before requesting a UCI engine move.");
        }

        engineColor = color;
        lastClock = clock;
        var timing = MoveTiming.Current;
        var continuesPondering = searchMode is SearchMode.Ponder && searchPosition.SequenceEqual(currentPosition);

        if (timing is not null)
        {
            timing.PonderEnabled = options.Ponder;
            timing.WasPondering |= searchMode is SearchMode.Ponder;
            timing.SearchPath = continuesPondering ? "continue-pondering" : "new-search";
        }

        try
        {
            if (continuesPondering)
            {
                await SendAsync("ponderhit", cancellationToken);
                searchMode = SearchMode.Move;
            }
            else
            {
                await StopSearchAsync(cancellationToken);
                searchPosition = currentPosition;
                StartSearchStatus(searchPosition);
                await SendPositionAsync(searchPosition, cancellationToken);
                searchMode = SearchMode.Move;
                await SendAsync(FormatGo(clock, false), cancellationToken);
            }

            if (timing is not null)
            {
                timing.WaitStarted = Stopwatch.GetTimestamp();
            }

            EngineOutput response;
            var reserve = Math.Max(0, options.MoveOverheadMilliseconds) + 1000 + EmergencyMoveGrace.TotalMilliseconds;
            var timeout = TimeSpan.FromMilliseconds(Math.Max(1, clock.For(color) - reserve));
            try
            {
                response = await ReadResponseAsync("bestmove", timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("UCI engine did not move before the safety deadline; requesting an immediate move with stop");
                await SendAsync("stop", cancellationToken);
                response = await ReadResponseAsync("bestmove", EmergencyMoveGrace, cancellationToken);
            }

            searchMode = SearchMode.Idle;
            RecordResponseTiming(response, timing);
            var move = ParseBestMove(response.Line, timing);
            currentPosition = [.. currentPosition, move];

            return move;
        }
        catch (Exception exception)
        {
            if (exception is TimeoutException)
            {
                isUnusable = true;
            }

            await CleanUpFailedSearchAsync();

            throw;
        }
    }

    public Task SendResultAsync(GameResult result, CancellationToken cancellationToken) => StopSearchAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (isStarted is false || isStopped)
        {
            return;
        }

        isStopped = true;
        Interlocked.Increment(ref searchGeneration);
        await CleanUpFailedSearchAsync();

        if (transport.IsRunning)
        {
            await transport.SendAsync("quit", cancellationToken);
        }

        await transport.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        transport.OutputReceived -= HandleOutput;
        await transport.DisposeAsync();
    }

    private async Task ReadHandshakeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);
        try
        {
            while (true)
            {
                var response = await transport.ReceiveAsync(timeout.Token);
                var line = response.Line.Trim();

                if (line is "uciok")
                {
                    return;
                }

                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                if (fields.FirstOrDefault() is "option")
                {
                    var option = UciOption.Parse(line);
                    advertisedOptions[option.Name] = option;
                }
                else if (fields.Length > 2 && fields[0] is "id" && fields[1] is "name")
                {
                    logger.LogInformation("UCI engine identified as {Name}", string.Join(' ', fields[2..]));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
        {
            throw new TimeoutException("The engine did not complete its UCI handshake.");
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        await SendAsync("isready", cancellationToken);
        await ReadResponseAsync("readyok", ResponseTimeout, cancellationToken);
    }

    private async Task<EngineOutput> ReadResponseAsync(string expected, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var response = await transport.ReceiveAsync(timeout.Token);
                var command = response.Line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

                if (command == expected)
                {
                    return response;
                }

                if (command is "bestmove")
                {
                    throw new InvalidDataException($"Received an unexpected UCI bestmove while waiting for {expected}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
        {
            throw new TimeoutException($"UCI engine did not return {expected} within {wait.TotalMilliseconds:0} ms.");
        }
    }

    private async Task StopSearchAsync(CancellationToken cancellationToken)
    {
        if (searchMode is SearchMode.Idle)
        {
            return;
        }

        await transport.SendAsync("stop", cancellationToken);
        try
        {
            await ReadResponseAsync("bestmove", ResponseTimeout, cancellationToken);
        }
        catch
        {
            isUnusable = true;

            throw;
        }
        searchMode = SearchMode.Idle;
        predictedMove = null;
    }

    private async Task CleanUpFailedSearchAsync()
    {
        if (isUnusable || searchMode is SearchMode.Idle || transport.IsRunning is false)
        {
            return;
        }

        try
        {
            await StopSearchAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            isUnusable = true;
            logger.LogWarning(exception, "Could not synchronize the stopped UCI search; the engine must be restarted");
        }
    }

    private ChessMove ParseBestMove(string line, MoveTiming? timing)
    {
        var started = Stopwatch.GetTimestamp();
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length < 2 || ChessMove.TryParse(fields[1], out var move) is false)
        {
            throw new InvalidDataException($"UCI engine returned no playable move: '{line}'.");
        }

        predictedMove = fields.Length >= 4 && fields[2] is "ponder" && ChessMove.TryParse(fields[3], out var prediction) ? prediction : null;

        if (timing is not null)
        {
            timing.EngineMoveFormat = "uci";
            timing.MoveConversionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        return move;
    }

    private static void RecordResponseTiming(EngineOutput response, MoveTiming? timing)
    {
        if (timing is not null)
        {
            timing.EngineReceived = Stopwatch.GetTimestamp();
            timing.GetMoveUtc = DateTimeOffset.UtcNow;
            timing.StdoutRead = response.ReadTimestamp;
            timing.StdoutUtc = response.ReadUtc;
        }
    }

    private Task SendPositionAsync(IReadOnlyList<ChessMove> moves, CancellationToken cancellationToken) => SendAsync(moves.Count is 0 ? "position startpos" : $"position startpos moves {string.Join(' ', moves.Select(move => move.Value))}", cancellationToken);

    private string FormatGo(ChessClock clock, bool ponder)
    {
        var white = Math.Max(0, clock.WhiteMilliseconds - (engineColor is ChessColor.White ? options.MoveOverheadMilliseconds : 0));
        var black = Math.Max(0, clock.BlackMilliseconds - (engineColor is ChessColor.Black ? options.MoveOverheadMilliseconds : 0));

        return string.Create(CultureInfo.InvariantCulture, $"go{(ponder ? " ponder" : string.Empty)} wtime {white} btime {black} winc {incrementMilliseconds} binc {incrementMilliseconds}");
    }

    private Task SendAsync(string command, CancellationToken cancellationToken)
    {
        EnsureRunning();

        return transport.SendAsync(command, cancellationToken);
    }

    private void HandleOutput(string line)
    {
        if (line.AsSpan().TrimStart().StartsWith("info", StringComparison.Ordinal) is false)
        {
            return;
        }

        var updated = Volatile.Read(ref searchInfo).Update(line);

        if (updated is null)
        {
            return;
        }

        Volatile.Write(ref searchInfo, updated);
        AnalysisUpdated?.Invoke(updated.ToAnalysis(Volatile.Read(ref analysisSign)));
        var count = Volatile.Read(ref legalMoveCount);
        var total = count is { IsCompletedSuccessfully: true } ? count.Result ?? 0 : 0;
        SearchStatusUpdated?.Invoke(updated.ToSearchStatus(total));
    }

    private void StartSearchStatus(IReadOnlyList<ChessMove> position)
    {
        var generation = Interlocked.Increment(ref searchGeneration);
        var rootColor = position.Count % 2 is 0 ? ChessColor.White : ChessColor.Black;
        Volatile.Write(ref analysisSign, rootColor == engineColor ? 1 : -1);
        Volatile.Write(ref searchInfo, new UciSearchInfo());

        if (outputPolicy?.IsQuiet is true)
        {
            Volatile.Write(ref legalMoveCount, null);

            return;
        }

        var calculation = Task.Run<int?>(() =>
        {
            try
            {
                int total;
                lock (moveCounterLock)
                {
                    if (outputPolicy?.IsQuiet is true || generation < Volatile.Read(ref searchGeneration))
                    {
                        return null;
                    }

                    total = legalMoveCounter.CountLegalMoves(position);
                }

                return total;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not determine the UCI root move count for display");

                return null;
            }
        });
        Volatile.Write(ref legalMoveCount, calculation);
        _ = PublishMoveCountAsync(calculation, generation);
    }

    private async Task PublishMoveCountAsync(Task<int?> calculation, int generation)
    {
        await Task.Yield();
        var total = await calculation;

        if (total is { } count && generation == Volatile.Read(ref searchGeneration))
        {
            SearchStatusUpdated?.Invoke(Volatile.Read(ref searchInfo).ToSearchStatus(count));
        }
    }

    private void EnsureRunning()
    {
        if (isStarted is false || isStopped || IsRunning is false)
        {
            throw new InvalidOperationException("The UCI engine is not running or requires a restart.");
        }
    }

    private TimeSpan ResponseTimeout => TimeSpan.FromSeconds(options.Uci.ResponseTimeoutSeconds);

    private enum SearchMode
    {
        Idle,
        Move,
        Ponder,
        Analysis
    }
}
