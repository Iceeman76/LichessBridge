namespace LichessBridge.Domain;

public enum ChessColor
{
    White,
    Black
}

public readonly record struct ChessMove
{
    public ChessMove(string value)
    {
        if (IsValid(value) is false)
        {
            throw new ArgumentException($"Invalid UCI move '{value}'.", nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public string Value { get; }
    public override string ToString() => Value;

    public static bool TryParse(string? value, out ChessMove move)
    {
        if (value is not null && IsValid(value))
        {
            move = new ChessMove(value);

            return true;
        }

        move = default;

        return false;
    }

    private static bool IsValid(string value) =>
        value.Length is 4 or 5 &&
        IsFile(value[0]) && IsRank(value[1]) &&
        IsFile(value[2]) && IsRank(value[3]) &&
        (value.Length is 4 || "qrbnQRBN".Contains(value[4]));

    private static bool IsFile(char value) => value is >= 'a' and <= 'h';
    private static bool IsRank(char value) => value is >= '1' and <= '8';
}

public readonly record struct ChessClock(int WhiteMilliseconds, int BlackMilliseconds)
{
    public int For(ChessColor color) => color is ChessColor.White ? WhiteMilliseconds : BlackMilliseconds;
}

public sealed record GameResult(string Result, string Reason);
