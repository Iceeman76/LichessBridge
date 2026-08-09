using System.Globalization;

namespace LichessBridge.Engine;

public sealed record EngineAnalysis(int Depth, int ScoreCentipawns, int TimeCentiseconds, long Nodes, string PrincipalVariation)
{
    public static bool TryParse(string line, out EngineAnalysis? analysis)
    {
        analysis = null;
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length < 5 || int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth) is false || int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var score) is false || int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var time) is false || long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nodes) is false)
        {
            return false;
        }

        analysis = new EngineAnalysis(depth, score, time, nodes, string.Join(' ', fields[4..]));

        return true;
    }
}
