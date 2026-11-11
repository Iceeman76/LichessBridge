using System.Threading.Channels;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace DotNetLab.Bridge;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Channel<string> lines = Channel.CreateBounded<string>(new BoundedChannelOptions(8192) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writing;
    private long droppedLines;

    public FileLoggerProvider(string path)
    {
        writing = Task.Run(() => WriteAsync(path));
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, Enqueue);

    public void Dispose()
    {
        lines.Writer.TryComplete();
        writing.GetAwaiter().GetResult();
    }

    private void Enqueue(string line)
    {
        if (lines.Writer.TryWrite(line) is false)
        {
            Interlocked.Increment(ref droppedLines);
        }
    }

    private async Task WriteAsync(string path)
    {
        StreamWriter? writer = null;
        var writerDate = default(DateOnly);
        try
        {
            while (await lines.Reader.WaitToReadAsync())
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);

                if (writer is null || writerDate != today)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync();
                    }

                    writer = CreateWriter(path, today);
                    writerDate = today;
                }

                var count = 0;

                while (count < 1024 && lines.Reader.TryRead(out var line))
                {
                    await writer.WriteLineAsync(line);
                    count++;
                }

                var dropped = Interlocked.Exchange(ref droppedLines, 0);

                if (dropped > 0)
                {
                    await writer.WriteLineAsync($"{DateTimeOffset.UtcNow:O} [Warning] File log queue full: dropped {dropped} lines");
                }

                await writer.FlushAsync();
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
        }
    }
    private static StreamWriter CreateWriter(string path, DateOnly date)
    {
        var fullPath = Path.GetFullPath(BuildDailyPath(path, date));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The log directory could not be determined."));
        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        return new StreamWriter(stream);
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
            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {categoryName}: {message}";

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
