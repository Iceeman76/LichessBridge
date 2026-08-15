using System.Globalization;
using System.Text.RegularExpressions;
using LichessBridge.Engine;

namespace LichessBridge.Status;

public static class StatusFormatter
{
    private static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");
    private static readonly Regex IsoDateTimePattern = new(@"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string FormatDateTime(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string NormalizeDateTimes(string value) => IsoDateTimePattern.Replace(value, match =>
        DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? FormatDateTime(parsed) : match.Value);

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
        var nodes = analysis.Nodes.ToString("N0", SwedishCulture);

        return $"depth {analysis.Depth} • {score} • {nodes} nodes • {analysis.PrincipalVariation}";
    }

    public static string FormatSearchStatus(EngineSearchStatus searchStatus)
    {
        var elapsedSeconds = (searchStatus.TimeCentiseconds / 100d).ToString("0.00", CultureInfo.InvariantCulture);
        var nodes = searchStatus.Nodes.ToString("N0", SwedishCulture);
        var kiloNodesPerSecond = searchStatus.TimeCentiseconds is 0 ? 0 : searchStatus.Nodes / (searchStatus.TimeCentiseconds * 10d);
        var speed = kiloNodesPerSecond.ToString("N0", SwedishCulture);
        var searchedMoves = Math.Clamp(searchStatus.TotalMoves - searchStatus.MovesLeft, 0, searchStatus.TotalMoves);

        return $"depth {searchStatus.Depth} • {elapsedSeconds}s • {nodes} nodes • {speed} kNPS • {searchStatus.CurrentMove} • {searchedMoves}/{searchStatus.TotalMoves}";
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
