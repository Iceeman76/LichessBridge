using LichessBridge.Domain;

namespace LichessBridge.Tests;

public sealed class ChessTypesTests
{
    [TestCase("e2e4", "e2e4")]
    [TestCase("a7a8Q", "a7a8q")]
    public void AcceptsAndNormalizesUciMoves(string value, string expected)
    {
        Assert.That(new ChessMove(value).Value, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("e9e4")]
    [TestCase("e2e4x")]
    public void RejectsInvalidUciMoves(string? value)
    {
        Assert.That(ChessMove.TryParse(value, out _), Is.False);

        if (value is not null)
        {
            Assert.Throws<ArgumentException>(() => _ = new ChessMove(value));
        }
    }

    [Test]
    public void SelectsClockForColor()
    {
        var clock = new ChessClock(100, 200);
        Assert.Multiple(() =>
        {
            Assert.That(clock.For(ChessColor.White), Is.EqualTo(100));
            Assert.That(clock.For(ChessColor.Black), Is.EqualTo(200));
        });
    }
}
