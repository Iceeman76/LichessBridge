using Chess;
using DotNetLab.LichessBridge.Domain;

namespace DotNetLab.LichessBridge.Engine;

internal sealed class SanMoveConverter
{
    private ChessBoard board = new();
    private readonly List<ChessMove> currentPosition = [];
    private string lastMoveSan = string.Empty;

    public ChessMove ConvertToUci(string san, IReadOnlyList<ChessMove> position)
    {
        SynchronizePosition(position);
        var normalizedSan = NormalizeSan(san);
        var legalMoves = board.Moves().ToArray();
        var matchingMoves = legalMoves.Where(move => NormalizeSan(move.San ?? string.Empty) == normalizedSan).ToArray();

        if (matchingMoves.Length is not 1)
        {
            var legalMoveDescription = string.Join(", ", legalMoves.Select(move => $"{ToChessMove(move).Value}={move.San ?? "?"}"));
            throw new InvalidDataException($"Engine returned SAN move '{san}' that does not identify one legal move. Legal moves: {legalMoveDescription}");
        }

        return ToChessMove(matchingMoves[0]);
    }

    public string ConvertLastMoveToSan(IReadOnlyList<ChessMove> position)
    {
        SynchronizePosition(position);

        return lastMoveSan;
    }

    private void SynchronizePosition(IReadOnlyList<ChessMove> position)
    {
        var continuesPosition = position.Count >= currentPosition.Count && currentPosition.SequenceEqual(position.Take(currentPosition.Count));

        if (continuesPosition is false)
        {
            board = new ChessBoard();
            currentPosition.Clear();
            lastMoveSan = string.Empty;
        }

        for (var ply = currentPosition.Count; ply < position.Count; ply++)
        {
            var chessMove = position[ply];
            var move = FindLegalMove(board, chessMove);
            var san = move.San ?? throw new InvalidDataException($"Move '{chessMove.Value}' has no SAN representation.");
            board.Move(move);
            currentPosition.Add(chessMove);
            lastMoveSan = san;
        }
    }

    private static Move FindLegalMove(ChessBoard board, ChessMove chessMove)
    {
        var legalMoves = board.Moves();
        var matchingMoves = legalMoves.Where(move => Matches(move, chessMove)).ToArray();

        if (matchingMoves.Length is not 1)
        {
            throw new InvalidDataException($"Position contains illegal UCI move '{chessMove.Value}'.");
        }

        return matchingMoves[0];
    }

    private static bool Matches(Move move, ChessMove chessMove)
    {
        var positionsMatch = move.OriginalPosition.ToString() == chessMove.Value[..2] && move.NewPosition.ToString() == chessMove.Value[2..4];

        if (positionsMatch is false)
        {
            return false;
        }

        var promotion = move.Promotion?.Type.AsChar;

        return chessMove.Value.Length == 5 ? promotion == chessMove.Value[4] : promotion is null;
    }

    private static ChessMove ToChessMove(Move move)
    {
        var promotion = move.Promotion?.Type.AsChar.ToString() ?? string.Empty;

        return new ChessMove($"{move.OriginalPosition}{move.NewPosition}{promotion}");
    }

    private static string NormalizeSan(string san) => san.Trim().Replace('0', 'O').TrimEnd('+', '#', '$');
}
