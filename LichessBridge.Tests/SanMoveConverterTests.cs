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
}
