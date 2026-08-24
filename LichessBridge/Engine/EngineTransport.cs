using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using LichessBridge.Configuration;
using Microsoft.Extensions.Logging;

namespace LichessBridge.Engine;

internal interface IEngineTransport : IAsyncDisposable
{
    event Action<string>? OutputReceived;
    bool IsRunning { get; }
    int? ExitCode { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task SendAsync(string command, CancellationToken cancellationToken);
    ValueTask<string> ReceiveAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

[ExcludeFromCodeCoverage]
internal sealed class ProcessEngineTransport(EngineOptions options, ILogger logger) : IEngineTransport
{
    private readonly Channel<string> output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim inputLock = new(1, 1);
    private Process? process;
    private Task? standardOutputTask;
    private Task? standardErrorTask;
    private int expectedExit;
    private int exitHandled;

    public event Action<string>? OutputReceived;

    public bool IsRunning => process is { HasExited: false };
    public int? ExitCode => process is { HasExited: true } ? process.ExitCode : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo();
        process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (process.Start() is false)
        {
            throw new InvalidOperationException($"Could not start engine '{options.Path}'.");
        }

        process.Exited += HandleProcessExit;
        standardOutputTask = PumpStandardOutputAsync(process);
        standardErrorTask = PumpStandardErrorAsync(process);
        logger.LogInformation("Started chess engine {Engine} (PID {ProcessId})", options.Name, process.Id);

        return Task.CompletedTask;
    }

    public async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        await inputLock.WaitAsync(cancellationToken);
        try
        {
            if (command.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref expectedExit, 1);
            }

            logger.LogDebug("WB > {Command}", command);
            await EngineProcess.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
            await EngineProcess.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            inputLock.Release();
        }
    }

    public async ValueTask<string> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await output.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (IsRunning is false)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await EngineProcess.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
        {
            logger.LogWarning("Engine did not stop normally; terminating process");
            EngineProcess.Kill(entireProcessTree: true);
            await EngineProcess.WaitForExitAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);

        if (standardOutputTask is not null)
        {
            await IgnoreFailureAsync(standardOutputTask);
        }

        if (standardErrorTask is not null)
        {
            await IgnoreFailureAsync(standardErrorTask);
        }

        if (process is not null)
        {
            process.Exited -= HandleProcessExit;
            process.Dispose();
        }

        inputLock.Dispose();
    }

    private ProcessStartInfo CreateStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = options.Path,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Path)) ?? throw new InvalidOperationException("The engine working directory could not be determined."),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private async Task PumpStandardOutputAsync(Process engineProcess)
    {
        try
        {
            while (await engineProcess.StandardOutput.ReadLineAsync() is { } line)
            {
                logger.LogDebug("WB < {Line}", line);
                OutputReceived?.Invoke(line);

                if (IsAwaitedResponse(line))
                {
                    await output.Writer.WriteAsync(line);
                }
            }

            if (engineProcess.HasExited)
            {
                CompleteForExit(engineProcess);
            }
            else
            {
                output.Writer.TryComplete();
            }
        }
        catch (Exception exception)
        {
            output.Writer.TryComplete(exception);
        }
    }

    private async Task PumpStandardErrorAsync(Process engineProcess)
    {
        while (await engineProcess.StandardError.ReadLineAsync() is { } line)
        {
            logger.LogWarning("Engine stderr: {Line}", line);
        }
    }

    private void HandleProcessExit(object? sender, EventArgs eventArguments)
    {
        if (sender is Process exitedProcess)
        {
            CompleteForExit(exitedProcess);
        }
    }

    private void CompleteForExit(Process exitedProcess)
    {
        if (Interlocked.Exchange(ref exitHandled, 1) != 0)
        {
            return;
        }

        var exitCode = exitedProcess.ExitCode;
        var wasExpected = Volatile.Read(ref expectedExit) != 0;

        if (wasExpected && exitCode == 0)
        {
            logger.LogDebug("Chess engine {Engine} (PID {ProcessId}) exited normally with code 0", options.Name, exitedProcess.Id);
            output.Writer.TryComplete();
            return;
        }

        logger.LogError("Chess engine {Engine} (PID {ProcessId}) exited unexpectedly with code {ExitCode} (0x{ExitCodeHex:X8})", options.Name, exitedProcess.Id, exitCode, unchecked((uint)exitCode));
        output.Writer.TryComplete(new InvalidOperationException($"Engine '{options.Name}' (PID {exitedProcess.Id}) exited with code {exitCode} (0x{unchecked((uint)exitCode):X8})."));
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static bool IsAwaitedResponse(string line) => line.StartsWith("feature ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("move ", StringComparison.OrdinalIgnoreCase);

    private Process EngineProcess => process ?? throw new InvalidOperationException("The engine process has not been started.");
}
