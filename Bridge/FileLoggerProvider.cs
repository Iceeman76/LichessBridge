using System.Diagnostics.CodeAnalysis;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Logging;

namespace DotNetLab.Bridge;

internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly Lock writeLock = new();
    private StreamWriter? writer;
    private DateOnly writerDate;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, Write);

    public void Dispose()
    {
        lock (writeLock)
        {
            writer?.Dispose();
            writer = null;
        }
    }

    private void Write(string line)
    {
        lock (writeLock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            if (writer is null || writerDate != today)
            {
                writer?.Dispose();
                writer = CreateWriter(path, today);
                writerDate = today;
            }

            writer.WriteLine(line);
        }
    }

    private static StreamWriter CreateWriter(string path, DateOnly date)
    {
        var fullPath = Path.GetFullPath(BuildDailyPath(path, date));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The log directory could not be determined."));
        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        return new StreamWriter(stream) { AutoFlush = true };
    }

    internal static string BuildDailyPath(string path, DateOnly date)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var dailyFileName = $"{fileName}-{date:yyyy-MM-dd}{extension}";

        return string.IsNullOrEmpty(directory)
            ? dailyFileName
            : Path.Combine(directory, dailyFileName);
    }

    private sealed class FileLogger(string categoryName, Action<string> write) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = StatusFormatter.NormalizeDateTimes(formatter(state, exception));
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {categoryName}: {message}";

            if (exception is not null)
            {
                line = $"{line}{Environment.NewLine}{exception}";
            }

            write(line);
        }
    }

    [ExcludeFromCodeCoverage]
    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
