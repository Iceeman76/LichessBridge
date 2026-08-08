using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LichessBridge.Engine;

public sealed class WinBoardEngineFactory(
    IOptions<EngineOptions> options,
    ILoggerFactory loggerFactory) : IChessEngineFactory
{
    public IChessEngine Create() => new WinBoardEngine(options.Value, loggerFactory.CreateLogger<WinBoardEngine>());
}

public sealed class WinBoardEngine(EngineOptions options, ILogger<WinBoardEngine> logger) : IChessEngine
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private bool _stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is not null)
            throw new InvalidOperationException("The engine has already been started.");
        if (!File.Exists(options.Path))
            throw new FileNotFoundException("Chess engine executable was not found.", options.Path);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Path,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Path))!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
            throw new InvalidOperationException($"Could not start engine '{options.Path}'.");

        _process.Exited += (_, _) => _output.Writer.TryComplete(
            new InvalidOperationException($"Engine exited with code {_process.ExitCode}."));
        _stdoutTask = PumpStdoutAsync(_process);
        _stderrTask = PumpStderrAsync(_process);

        logger.LogInformation("Started chess engine {Engine} (PID {ProcessId})", options.Name, _process.Id);
        if (!string.IsNullOrWhiteSpace(options.Command))
            await SendAsync(options.Command, cancellationToken);
        await SendAsync("protover 2", cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
        try
        {
            await WaitForFeaturesAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The engine did not complete its WB/2 feature handshake.");
        }
    }

    public async Task SetPositionAsync(
        IReadOnlyList<ChessMove> moves,
        int initialSeconds,
        int incrementSeconds,
        CancellationToken cancellationToken)
    {
        EnsureRunning();
        await SendAsync("new", cancellationToken);
        await SendAsync("force", cancellationToken);
        await SendAsync($"level 0 {FormatBaseTime(initialSeconds)} {incrementSeconds}", cancellationToken);
        foreach (var move in moves)
            await SendAsync($"usermove {move.Value}", cancellationToken);
    }

    public async Task<ChessMove> GetMoveAsync(
        ChessColor engineColor,
        ChessClock clock,
        CancellationToken cancellationToken)
    {
        EnsureRunning();
        var ownCentiseconds = MillisecondsToCentiseconds(clock.For(engineColor));
        var opponent = engineColor == ChessColor.White ? ChessColor.Black : ChessColor.White;
        var opponentCentiseconds = MillisecondsToCentiseconds(clock.For(opponent));
        await SendAsync($"time {ownCentiseconds}", cancellationToken);
        await SendAsync($"otim {opponentCentiseconds}", cancellationToken);
        await SendAsync("go", cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.MoveTimeoutSeconds));
        try
        {
            while (await _output.Reader.WaitToReadAsync(timeout.Token))
            {
                while (_output.Reader.TryRead(out var line))
                {
                    if (!line.StartsWith("move ", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var value = line[5..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    if (!ChessMove.TryParse(value, out var move))
                        throw new InvalidDataException($"Engine returned invalid move '{value}'.");
                    await SendAsync("force", cancellationToken);
                    return move;
                }
            }

            throw new EndOfStreamException("Engine output closed before a move was returned.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Engine did not return a move within {options.MoveTimeoutSeconds} seconds.");
        }
    }

    public Task SendResultAsync(GameResult result, CancellationToken cancellationToken)
    {
        var reason = result.Reason.Replace('{', '(').Replace('}', ')');
        return SendAsync($"result {result.Result} {{{reason}}}", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is null || _stopped)
            return;
        _stopped = true;

        if (!_process.HasExited)
        {
            try
            {
                await SendAsync("quit", cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                if (!_process.HasExited)
                {
                    logger.LogWarning("Engine did not stop normally; terminating process");
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }

        logger.LogInformation("Chess engine stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        if (_stdoutTask is not null)
            await IgnorePumpFailureAsync(_stdoutTask);
        if (_stderrTask is not null)
            await IgnorePumpFailureAsync(_stderrTask);
        _process?.Dispose();
        _inputLock.Dispose();
    }

    private async Task WaitForFeaturesAsync(CancellationToken cancellationToken)
    {
        while (await _output.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_output.Reader.TryRead(out var line))
            {
                if (!line.StartsWith("feature ", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (line.Contains("myname=", StringComparison.OrdinalIgnoreCase))
                    logger.LogInformation("Engine features: {Features}", line[8..]);
                if (line.Contains("done=1", StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        throw new EndOfStreamException("Engine output closed during WB/2 handshake.");
    }

    private async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        EnsureRunning();
        await _inputLock.WaitAsync(cancellationToken);
        try
        {
            logger.LogDebug("WB > {Command}", command);
            await _process!.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _inputLock.Release();
        }
    }

    private async Task PumpStdoutAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                logger.LogDebug("WB < {Line}", line);
                await _output.Writer.WriteAsync(line);
            }
            _output.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            _output.Writer.TryComplete(exception);
        }
    }

    private async Task PumpStderrAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
            logger.LogWarning("Engine stderr: {Line}", line);
    }

    private void EnsureRunning()
    {
        if (_process is null)
            throw new InvalidOperationException("The engine has not been started.");
        if (_process.HasExited)
            throw new InvalidOperationException($"The engine exited with code {_process.ExitCode}.");
    }

    private static string FormatBaseTime(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");

    internal static int MillisecondsToCentiseconds(int milliseconds) => Math.Max(0, milliseconds / 10);

    private static async Task IgnorePumpFailureAsync(Task task)
    {
        try { await task; }
        catch { /* The process exit is reported through the main engine operation. */ }
    }
}
