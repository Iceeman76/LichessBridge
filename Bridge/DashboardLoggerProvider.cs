using System.Threading.Channels;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DotNetLab.Bridge;

internal sealed class DashboardLoggerProvider : ILoggerProvider
{
    private readonly IStatusDisplay statusDisplay;
    private readonly ConsoleOutputPolicy outputPolicy;
    private readonly int capacity;
    private readonly LogLevel minimumLevel;
    private readonly Channel<PendingEntry> pending = Channel.CreateBounded<PendingEntry>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task processing;

    public DashboardLoggerProvider(IStatusDisplay statusDisplay, IConfiguration configuration, ConsoleOutputPolicy outputPolicy)
    {
        this.statusDisplay = statusDisplay;
        this.outputPolicy = outputPolicy;
        capacity = Math.Max(1, configuration.GetValue("Logging:ConsoleEntries", 6));
        var configuredLevel = configuration.GetValue("Logging:ConsoleLogLevel", nameof(LogLevel.Information));

        if (Enum.TryParse<LogLevel>(configuredLevel, ignoreCase: true, out var parsedLevel) is false || Enum.IsDefined(parsedLevel) is false)
        {
            throw new InvalidOperationException($"Logging:ConsoleLogLevel '{configuredLevel}' is invalid.");
        }

        minimumLevel = parsedLevel;
        processing = Task.Run(ProcessEntriesAsync);
    }

    public ILogger CreateLogger(string categoryName) => new DashboardLogger(categoryName, minimumLevel, outputPolicy, entry => pending.Writer.TryWrite(entry));

    public void Dispose()
    {
        pending.Writer.TryComplete();
        processing.GetAwaiter().GetResult();
    }

    private async Task ProcessEntriesAsync()
    {
        var entries = new Queue<DashboardLogEntry>();

        await foreach (var entry in pending.Reader.ReadAllAsync())
        {
            if (outputPolicy.IsQuiet)
            {
                continue;
            }

            entries.Enqueue(new DashboardLogEntry(entry.Timestamp, entry.Level, entry.Category, StatusFormatter.NormalizeDateTimes(entry.Format()), entry.Exception));

            while (entries.Count > capacity)
            {
                entries.Dequeue();
            }

            statusDisplay.ShowLogs([.. entries]);
        }
    }

    private sealed record PendingEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, Func<string> Format, Exception? Exception);

    private sealed class DashboardLogger(string categoryName, LogLevel minimumLevel, ConsoleOutputPolicy outputPolicy, Action<PendingEntry> enqueue) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel is not LogLevel.None && outputPolicy.IsQuiet is false && (categoryName is not "LichessBridge.Lichess.LichessClient" || logLevel >= LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) is false)
            {
                return;
            }

            enqueue(new PendingEntry(DateTimeOffset.Now, logLevel, categoryName, () => formatter(state, exception), exception));
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
