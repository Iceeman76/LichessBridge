using LichessBridge.Domain;
using LichessBridge.Engine;

namespace LichessBridge.Tests;

public sealed class SanMoveConverterTests
{
    [Test]
    public void ConvertsBlackPawnMoveToUci()
    {
        var move = SanMoveConverter.ConvertToUci("c5", [new ChessMove("e2e4")]);

        Assert.That(move.Value, Is.EqualTo("c7c5"));
    }

    [Test]
    public void ConvertsCastlingToUci()
    {
        var position = new[] { "e2e4", "e7e5", "g1f3", "b8c6", "f1c4", "g8f6" }.Select(value => new ChessMove(value)).ToArray();
        var move = SanMoveConverter.ConvertToUci("O-O", position);

        Assert.That(move.Value, Is.EqualTo("e1g1"));
    }

    [Test]
    public void IgnoresCheckSuffixDifference()
    {
        var position = new[] { "e2e4", "f7f6", "d1h5" }.Select(value => new ChessMove(value)).ToArray();
        var move = SanMoveConverter.ConvertToUci("g6", position);

        Assert.That(move.Value, Is.EqualTo("g7g6"));
    }

    [Test]
    public void ConvertsEnPassantCaptureToUci()
    {
        var position = new[] { "e2e4", "a7a6", "e4e5", "d7d5" }.Select(value => new ChessMove(value)).ToArray();
        var move = SanMoveConverter.ConvertToUci("exd6", position);

        Assert.That(move.Value, Is.EqualTo("e5d6"));
    }

    [Test]
    public void ConvertsKxd6FromTimedOutLichessGame()
    {
        const string moves = "e2e4 c7c5 c2c3 g8f6 e4e5 f6d5 g1f3 b8c6 d2d4 c5d4 f1c4 d5b6 c4b5 d4c3 b1c3 a7a6 b5d3 d7d5 e5d6 e7e5 e1g1 f8d6 f1e1 d6c7 a2a3 c8g4 h2h3 g4f3 d1f3 e8g8 e1d1 d8e8 d3f5 a8d8 c1e3 e8e7 f5e4 c6d4 f3h5 g7g6 h5g5 e7g5 e3g5 d8b8 e4d3 f8c8 g5e7 c8e8 e7g5 c7d6 c3e4 d6e7 g5f6 b6d7 f6e7 e8e7 a1c1 d7b6 d1e1 b8d8 d3b1 b6d5 g1h1 f7f5 e4c5 d5f4 b1a2 g8f8 e1d1 f8e8 a2c4 d8c8 b2b4 c8c7 h1h2 h7h6 d1d2 g6g5 c1c3 h6h5 h2h1 h5h4 d2d1 g5g4 d1e1 g4h3 g2h3 e5e4 e1g1 e7g7 g1e1 c7e7 e1d1 d4f3 c3f3 e4f3 c4f1 e7e2 b4b5 e2f2 c5d3 f4d3 f1d3 g7g3 d3f1 f2a2 a3a4 a2a4 b5a6 b7a6 d1b1 e8e7 b1b7 e7d6 b7b6 d6c7 b6b1 a4d4 b1b2 d4d1 b2c2 c7d6 h1h2 d1f1 c2c6 d6e5 c6c5 e5e6 c5c6 e6f7 c6f6 f7e8 f6e6 e8d7 e6d6";
        var position = moves.Split(' ').Select(value => new ChessMove(value)).ToArray();

        var move = SanMoveConverter.ConvertToUci("Kxd6", position);

        Assert.That(move.Value, Is.EqualTo("d7d6"));
    }
}
