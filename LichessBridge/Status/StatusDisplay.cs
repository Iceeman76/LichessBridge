using System.Diagnostics;
using LichessBridge.Domain;
using LichessBridge.Lichess;

namespace LichessBridge.Status;

public interface IStatusDisplay
{
    void ShowHeader(StatusHeader header);
    void ShowGame(GameStatus game);
    void ClearGame();
    void Show(string status);
}

public sealed record StatusHeader(string EngineName, string AccountName, bool Ponder, IReadOnlyList<string> Modes, IReadOnlyList<string> Opponents, IReadOnlyList<string> Variants, IReadOnlyList<string> Speeds, IReadOnlyList<LichessRating> Ratings);
public sealed record GameStatus(string Mode, string Speed, string TimeControl, string WhiteName, string? WhiteTitle, int? WhiteRating, string BlackName, string? BlackTitle, int? BlackRating, string LastMove, int WhiteTimeMilliseconds, int BlackTimeMilliseconds, ChessColor? ActiveColor);

public sealed class ConsoleStatusDisplay : IStatusDisplay, IDisposable
{
    private readonly Lock writeLock = new();
    private readonly Timer clockTimer;
    private StatusHeader? header;
    private GameStatus? game;
    private string status = string.Empty;
    private long gameUpdatedAt;
    private int lastMoveRow = -1;
    private int clockRow = -1;
    private int statusRow = -1;
    private int lastMoveLength;
    private int clockLength;
    private int statusLength;

    public ConsoleStatusDisplay()
    {
        clockTimer = new Timer(UpdateClocks, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void ShowHeader(StatusHeader header)
    {
        lock (writeLock)
        {
            this.header = header;
            RenderAll();
        }
    }

    public void ShowGame(GameStatus game)
    {
        lock (writeLock)
        {
            var previousLastMove = this.game?.LastMove;
            var startsGame = this.game is null;
            this.game = game;
            gameUpdatedAt = Stopwatch.GetTimestamp();

            if (startsGame || Console.IsOutputRedirected)
            {
                RenderAll();

                return;
            }

            if (string.Equals(game.LastMove, previousLastMove, StringComparison.Ordinal) is false)
            {
                WriteLastMove();
            }

            WriteClocks();
        }
    }

    public void ClearGame()
    {
        lock (writeLock)
        {
            game = null;
            RenderAll();
        }
    }

    public void Show(string status)
    {
        lock (writeLock)
        {
            if (this.status == status)
            {
                return;
            }

            this.status = status;

            if (Console.IsOutputRedirected)
            {
                Console.WriteLine(status);

                return;
            }

            if (statusRow < 0)
            {
                RenderAll();

                return;
            }

            WriteAt(statusRow, status, ref statusLength);
        }
    }

    public void Dispose()
    {
        clockTimer.Dispose();
    }

    private void RenderAll()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        Console.Clear();
        lastMoveRow = -1;
        clockRow = -1;
        statusRow = -1;
        lastMoveLength = 0;
        clockLength = 0;
        statusLength = 0;
        Console.WriteLine("LichessBridge");
        Console.WriteLine(new string('─', 28));

        if (header is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Engine     : {header.EngineName} playing as {header.AccountName} on Lichess");
            Console.WriteLine($"Pondering  : {(header.Ponder ? "On" : "Off")}");
            Console.WriteLine($"Challenges : {JoinDistinct(header.Modes)} | {JoinDistinct(header.Variants)} | {JoinDistinct(header.Speeds)}");
            Console.WriteLine($"Opponents  : {JoinDistinct(header.Opponents)}");

            foreach (var rating in header.Ratings)
            {
                Console.WriteLine($"{FormatSpeed(rating.Speed),-11}: {rating.Rating}");
            }
        }

        if (game is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Game       : {FormatSpeed(game.Mode)} {FormatSpeed(game.Speed)} ({game.TimeControl})");
            Console.WriteLine($"White      : {FormatPlayer(game.WhiteName, game.WhiteTitle, game.WhiteRating)}");
            Console.WriteLine($"Black      : {FormatPlayer(game.BlackName, game.BlackTitle, game.BlackRating)}");
            lastMoveRow = Console.CursorTop;
            var lastMove = CreateLastMoveLine();
            Console.WriteLine(lastMove);
            lastMoveLength = lastMove.Length;
            clockRow = Console.CursorTop;
            var clocks = CreateClockLine();
            Console.WriteLine(clocks);
            clockLength = clocks.Length;
        }

        Console.WriteLine();
        statusRow = Console.CursorTop;
        Console.Write(status);
        statusLength = status.Length;
    }

    private string CreateLastMoveLine() => $"Last move  : {game?.LastMove}";

    private string CreateClockLine()
    {
        var currentGame = game ?? throw new InvalidOperationException("No game is displayed.");
        var elapsed = Stopwatch.GetElapsedTime(gameUpdatedAt);
        var whiteTime = AdjustClock(currentGame.WhiteTimeMilliseconds, currentGame.ActiveColor is ChessColor.White, elapsed);
        var blackTime = AdjustClock(currentGame.BlackTimeMilliseconds, currentGame.ActiveColor is ChessColor.Black, elapsed);

        return $"Clocks     : {currentGame.WhiteName} {FormatClock(whiteTime)} | {currentGame.BlackName} {FormatClock(blackTime)}";
    }

    private void WriteLastMove()
    {
        WriteAt(lastMoveRow, CreateLastMoveLine(), ref lastMoveLength);
    }

    private void WriteClocks()
    {
        WriteAt(clockRow, CreateClockLine(), ref clockLength);
    }

    private static void WriteAt(int row, string text, ref int previousLength)
    {
        if (row < 0)
        {
            return;
        }

        Console.SetCursorPosition(0, row);
        Console.Write(text.PadRight(previousLength));
        previousLength = text.Length;
    }

    private static string FormatSpeed(string speed) => speed switch
    {
        "ultraBullet" => "UltraBullet",
        _ => char.ToUpperInvariant(speed[0]) + speed[1..]
    };

    private static string FormatPlayer(string name, string? title, int? rating)
    {
        var titledName = string.IsNullOrWhiteSpace(title) ? name : $"{title} {name}";

        return rating is null ? titledName : $"{titledName} ({rating})";
    }

    private static string JoinDistinct(IEnumerable<string> values) => string.Join(", ", values.Distinct(StringComparer.OrdinalIgnoreCase));

    private static string FormatClock(int milliseconds)
    {
        var totalSeconds = Math.Max(0, milliseconds / 1000);
        var hours = totalSeconds / 3600;

        return hours > 0
            ? $"{hours}:{totalSeconds % 3600 / 60:00}:{totalSeconds % 60:00}"
            : $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }

    internal static int AdjustClock(int milliseconds, bool isActive, TimeSpan elapsed)
    {
        if (isActive is false)
        {
            return milliseconds;
        }

        var elapsedMilliseconds = Math.Min(int.MaxValue, (long)elapsed.TotalMilliseconds);

        return (int)Math.Max(0, milliseconds - elapsedMilliseconds);
    }

    private void UpdateClocks(object? state)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        lock (writeLock)
        {
            if (game is not null)
            {
                WriteClocks();
            }
        }
    }
}
