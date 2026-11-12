using System.Globalization;

namespace DotNetLab.LichessBridge.Engine;

internal sealed record UciSearchInfo(int Depth = 0, int Score = 0, int? Mate = null, int Milliseconds = 0, long Nodes = 0, string Variation = "", string CurrentMove = "", int? CurrentMoveNumber = null, long? TablebaseHits = null, int? HashFullPermille = null)
{
    public UciSearchInfo? Update(string line)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length < 2 || fields[0] is not "info" || fields[1] is "string")
        {
            return null;
        }

        var depth = Depth;
        var score = Score;
        var mate = Mate;
        var milliseconds = Milliseconds;
        var nodes = Nodes;
        var variation = Variation;
        var currentMove = CurrentMove;
        var currentMoveNumber = CurrentMoveNumber;
        var tablebaseHits = TablebaseHits;
        var hashFullPermille = HashFullPermille;

        for (var index = 1; index + 1 < fields.Length; index++)
        {
            var value = fields[index + 1];

            if (fields[index] is "multipv" && value is not "1")
            {
                return null;
            }

            switch (fields[index])
            {
                case "depth" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDepth):
                    depth = parsedDepth;
                    break;
                case "time" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTime):
                    milliseconds = parsedTime;
                    break;
                case "nodes" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNodes):
                    nodes = parsedNodes;
                    break;
                case "tbhits" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits):
                    tablebaseHits = hits;
                    break;
                case "hashfull" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fullness) && fullness is >= 0 and <= 1000:
                    hashFullPermille = fullness;
                    break;
                case "currmove":
                    currentMove = value;
                    break;
                case "currmovenumber" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moveNumber):
                    currentMoveNumber = moveNumber;
                    break;
                case "score" when index + 2 < fields.Length && int.TryParse(fields[index + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var evaluation):
                    if (value is "cp")
                    {
                        score = evaluation;
                        mate = null;
                    }
                    else if (value is "mate")
                    {
                        score = 0;
                        mate = evaluation;
                    }

                    break;
                case "pv":
                    variation = string.Join(' ', fields[(index + 1)..]);
                    index = fields.Length;
                    break;
            }
        }

        return new UciSearchInfo(depth, score, mate, milliseconds, nodes, variation, currentMove, currentMoveNumber, tablebaseHits, hashFullPermille);
    }

    public EngineAnalysis ToAnalysis() => new(Depth, Score, Milliseconds / 10, Nodes, Variation) { MateInMoves = Mate, TablebaseHits = TablebaseHits };
    public EngineSearchStatus ToSearchStatus(int totalMoves = 0) => new(Milliseconds / 10, Nodes, Depth, Math.Max(0, totalMoves - (CurrentMoveNumber ?? 0)), totalMoves, CurrentMove) { CurrentMoveNumber = CurrentMoveNumber, TablebaseHits = TablebaseHits, HashFullPermille = HashFullPermille };
}
