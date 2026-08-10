using LichessBridge.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bridge;

internal sealed class DashboardLoggerProvider : ILoggerProvider
{
    private readonly IStatusDisplay statusDisplay;
    private readonly int capacity;
    private readonly LogLevel minimumLevel;
    private readonly Lock entryLock = new();
    private readonly Queue<DashboardLogEntry> entries = [];

    public DashboardLoggerProvider(IStatusDisplay statusDisplay, IConfiguration configuration)
    {
        this.statusDisplay = statusDisplay;
        capacity = Math.Max(1, configuration.GetValue("Logging:ConsoleEntries", 6));
        var configuredLevel = configuration.GetValue("Logging:ConsoleLogLevel", nameof(LogLevel.Information));

        if (Enum.TryParse<LogLevel>(configuredLevel, ignoreCase: true, out var parsedLevel) is false || Enum.IsDefined(parsedLevel) is false)
        {
            throw new InvalidOperationException($"Logging:ConsoleLogLevel '{configuredLevel}' is invalid.");
        }

        minimumLevel = parsedLevel;
    }

    public ILogger CreateLogger(string categoryName) => new DashboardLogger(categoryName, minimumLevel, Add);

    public void Dispose()
    {
    }

    private void Add(DashboardLogEntry entry)
    {
        lock (entryLock)
        {
            entries.Enqueue(entry);

            while (entries.Count > capacity)
            {
                entries.Dequeue();
            }

            statusDisplay.ShowLogs([.. entries]);
        }
    }

    private sealed class DashboardLogger(string categoryName, LogLevel minimumLevel, Action<DashboardLogEntry> add) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel is not LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) is false)
            {
                return;
            }

            add(new DashboardLogEntry(DateTimeOffset.Now, logLevel, categoryName, StatusFormatter.NormalizeDateTimes(formatter(state, exception)), exception));
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
