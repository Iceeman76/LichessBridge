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

    public ConsoleStatusDisplay()
    {
        clockTimer = new Timer(UpdateClocks, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void ShowHeader(StatusHeader header)
    {
        lock (writeLock)
        {
            this.header = header;
            Render();
        }
    }

    public void ShowGame(GameStatus game)
    {
        lock (writeLock)
        {
            this.game = game;
            gameUpdatedAt = Stopwatch.GetTimestamp();
            Render();
        }
    }

    public void ClearGame()
    {
        lock (writeLock)
        {
            game = null;
            Render();
        }
    }

    public void Show(string status)
    {
        lock (writeLock)
        {
            this.status = status;
            Render();
        }
    }

    public void Dispose()
    {
        clockTimer.Dispose();
    }

    private void Render()
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(status);

            return;
        }

        Console.Clear();
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
            var elapsed = Stopwatch.GetElapsedTime(gameUpdatedAt);
            var whiteTime = AdjustClock(game.WhiteTimeMilliseconds, game.ActiveColor is ChessColor.White, elapsed);
            var blackTime = AdjustClock(game.BlackTimeMilliseconds, game.ActiveColor is ChessColor.Black, elapsed);
            Console.WriteLine();
            Console.WriteLine($"Game       : {FormatSpeed(game.Mode)} {FormatSpeed(game.Speed)} ({game.TimeControl})");
            Console.WriteLine($"White      : {FormatPlayer(game.WhiteName, game.WhiteTitle, game.WhiteRating)}");
            Console.WriteLine($"Black      : {FormatPlayer(game.BlackName, game.BlackTitle, game.BlackRating)}");
            Console.WriteLine($"Last move  : {game.LastMove}");
            Console.WriteLine($"Clocks     : {game.WhiteName} {FormatClock(whiteTime)} | {game.BlackName} {FormatClock(blackTime)}");
        }

        Console.WriteLine();
        Console.Write(status);
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
                Render();
            }
        }
    }
}
