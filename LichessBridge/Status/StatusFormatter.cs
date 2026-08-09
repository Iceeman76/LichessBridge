using System.Globalization;
using LichessBridge.Engine;

namespace LichessBridge.Status;

public static class StatusFormatter
{
    public static int AdjustClock(int milliseconds, bool isActive, TimeSpan elapsed)
    {
        if (isActive is false)
        {
            return milliseconds;
        }

        var elapsedMilliseconds = Math.Min(int.MaxValue, (long)elapsed.TotalMilliseconds);

        return (int)Math.Max(0, milliseconds - elapsedMilliseconds);
    }

    public static string FormatAnalysis(EngineAnalysis analysis)
    {
        if (analysis is { Depth: 0, ScoreCentipawns: 0, TimeCentiseconds: 0, Nodes: 0 })
        {
            return $"book • {analysis.PrincipalVariation}";
        }

        var score = (analysis.ScoreCentipawns / 100d).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        var nodes = analysis.Nodes.ToString("N0", CultureInfo.InvariantCulture);

        return $"depth {analysis.Depth} • {score} • {nodes} nodes • {analysis.PrincipalVariation}";
    }

    public static string FormatSearchStatus(EngineSearchStatus searchStatus)
    {
        var elapsedSeconds = (searchStatus.TimeCentiseconds / 100d).ToString("0.00", CultureInfo.InvariantCulture);
        var nodes = searchStatus.Nodes.ToString("N0", CultureInfo.InvariantCulture);
        var searchedMoves = Math.Clamp(searchStatus.TotalMoves - searchStatus.MovesLeft, 0, searchStatus.TotalMoves);

        return $"depth {searchStatus.Depth} • {elapsedSeconds}s • {nodes} nodes • {searchStatus.CurrentMove} • {searchedMoves}/{searchStatus.TotalMoves}";
    }

    public static string FormatSpeed(string speed) => speed switch
    {
        "ultraBullet" => "UltraBullet",
        _ => char.ToUpperInvariant(speed[0]) + speed[1..]
    };

    public static string FormatPlayer(string name, string? title, int? rating)
    {
        var titledName = string.IsNullOrWhiteSpace(title) ? name : $"{title} {name}";

        return rating is null ? titledName : $"{titledName} ({rating})";
    }

    public static string FormatClock(int milliseconds)
    {
        var totalSeconds = Math.Max(0, milliseconds / 1000);
        var hours = totalSeconds / 3600;

        return hours > 0
            ? $"{hours}:{totalSeconds % 3600 / 60:00}:{totalSeconds % 60:00}"
            : $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }
}
