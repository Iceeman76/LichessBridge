using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Bridge;

internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly StreamWriter writer = CreateWriter(path);
    private readonly Lock writeLock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, Write);

    public void Dispose()
    {
        writer.Dispose();
    }

    private void Write(string line)
    {
        lock (writeLock)
        {
            writer.WriteLine(line);
        }
    }

    private static StreamWriter CreateWriter(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The log directory could not be determined."));
        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        return new StreamWriter(stream) { AutoFlush = true };
    }

    private sealed class FileLogger(string categoryName, Action<string> write) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {categoryName}: {message}";

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
