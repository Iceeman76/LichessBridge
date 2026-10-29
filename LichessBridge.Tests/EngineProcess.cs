using System.Collections.Concurrent;
using System.Text.Json;
using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Tests;

internal sealed class EngineProcess : IAsyncDisposable, ILogger
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"LichessBridge-engine-{Guid.NewGuid():N}");
    private readonly ConcurrentQueue<string> commands = new();
    private readonly ILoggerFactory loggerFactory;

    public EngineProcess(string moveResponse = "move g1f3", string handshakeResponse = "feature myname=\"Fake\" san=1 usermove=0 done=1", string? ponderResponse = null, string? analysisResponse = null, string? hintResponse = null, bool waitForEmergencyMove = false)
    {
        Directory.CreateDirectory(directory);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "TestEngine")))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(ExecutablePath, File.GetUnixFileMode(ExecutablePath) | UnixFileMode.UserExecute);
        }

        File.WriteAllText(Path.Combine(directory, "responses.json"), JsonSerializer.Serialize(new { moveResponse, handshakeResponse, ponderResponse, analysisResponse, hintResponse, waitForEmergencyMove }));
        loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(new RecordingLoggerProvider(this)));
    }

    public IReadOnlyList<string> Commands => commands.ToArray();
    public string ExecutablePath => System.IO.Path.Combine(directory, OperatingSystem.IsWindows() ? "LichessBridge.TestEngine.exe" : "LichessBridge.TestEngine");

    public IChessEngine CreateEngine(EngineOptions? options = null) => new WinBoardEngineFactory(Options.Create(options ?? new EngineOptions { Path = ExecutablePath, Name = "Fake", SearchStatusPollMilliseconds = 60_000 }), loggerFactory).Create();

    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> properties && formatter(state, exception).StartsWith("WB > ", StringComparison.Ordinal))
        {
            commands.Enqueue((string)properties.Single(property => property.Key == "Command").Value!);
        }
    }

    public async ValueTask DisposeAsync()
    {
        loggerFactory.Dispose();
        var timeout = DateTimeOffset.UtcNow.AddSeconds(2);

        while (true)
        {
            try
            {
                Directory.Delete(directory, recursive: true);

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && DateTimeOffset.UtcNow < timeout)
            {
                await Task.Delay(20);
            }
        }
    }

    private sealed class RecordingLoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }
}
