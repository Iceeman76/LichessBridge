using DotNetLab.LichessBridge.Engine;

namespace DotNetLab.LichessBridge.Tests;

public sealed class EngineSearchStatusTests
{
    [Test]
    public void ParsesStat01WithoutCurrentMove()
    {
        var parsed = EngineSearchStatus.TryParse("stat01: 142 123456 12 4 27", out var status);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(status, Is.EqualTo(new EngineSearchStatus(142, 123456, 12, 4, 27, string.Empty)));
        });
    }

    [Test]
    public void ParsesStat01WithCurrentMove()
    {
        var parsed = EngineSearchStatus.TryParse("stat01: 142 123456 12 4 27 Nf6", out var status);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(status, Is.EqualTo(new EngineSearchStatus(142, 123456, 12, 4, 27, "Nf6")));
        });
    }

    [TestCase("stat01: 142 123456 12 4")]
    [TestCase("stat01: nope 123456 12 4 27")]
    public void RejectsMalformedStat01(string line)
    {
        Assert.That(EngineSearchStatus.TryParse(line, out _), Is.False);
    }
}
