using System.Globalization;

namespace LichessBridge.Engine;

public sealed record EngineSearchStatus(int TimeCentiseconds, long Nodes, int Depth, int MovesLeft, int TotalMoves, string CurrentMove)
{
    public static bool TryParse(string line, out EngineSearchStatus? status)
    {
        status = null;

        if (line.StartsWith("stat01", StringComparison.OrdinalIgnoreCase) is false)
        {
            return false;
        }

        var separator = line.IndexOf(':');
        var values = (separator >= 0 ? line[(separator + 1)..] : line[6..]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (values.Length < 6 || int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var time) is false || long.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nodes) is false || int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth) is false || int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var movesLeft) is false || int.TryParse(values[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMoves) is false)
        {
            return false;
        }

        status = new EngineSearchStatus(time, nodes, depth, movesLeft, totalMoves, string.Join(' ', values[5..]));

        return true;
    }
}
