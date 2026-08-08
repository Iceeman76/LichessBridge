using Chess;
using LichessBridge.Domain;

namespace LichessBridge.Engine;

internal static class SanMoveConverter
{
    public static ChessMove ConvertToUci(string san, IReadOnlyList<ChessMove> position)
    {
        var board = CreateBoard(position);
        var normalizedSan = NormalizeSan(san);
        var matchingMoves = board.Moves().Where(move => NormalizeSan(move.San ?? string.Empty) == normalizedSan).ToArray();

        if (matchingMoves.Length is not 1)
        {
            throw new InvalidDataException($"Engine returned SAN move '{san}' that does not identify one legal move.");
        }

        return ToChessMove(matchingMoves[0]);
    }

    public static string ConvertLastMoveToSan(IReadOnlyList<ChessMove> position)
    {
        if (position.Count == 0)
        {
            return string.Empty;
        }

        var previousPosition = position.Take(position.Count - 1).ToArray();
        var board = CreateBoard(previousPosition);
        var move = FindLegalMove(board, position[^1]);

        return move.San ?? throw new InvalidDataException($"Move '{position[^1].Value}' has no SAN representation.");
    }

    private static ChessBoard CreateBoard(IReadOnlyList<ChessMove> position)
    {
        var board = new ChessBoard();

        foreach (var move in position)
        {
            board.Move(FindLegalMove(board, move));
        }

        return board;
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

    private static string NormalizeSan(string san) => san.Trim().Replace('0', 'O').TrimEnd('+', '#');
}
