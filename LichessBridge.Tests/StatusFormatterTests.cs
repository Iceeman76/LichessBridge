using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Status;

namespace DotNetLab.LichessBridge.Tests;

public sealed class StatusFormatterTests
{
    [Test]
    public void FormatsDateAndTimeInLocalTimeWithoutFractionOrOffset()
    {
        var value = new DateTimeOffset(2026, 8, 10, 7, 0, 49, 849, TimeSpan.Zero);
        var expected = value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        Assert.That(StatusFormatter.FormatDateTime(value), Is.EqualTo(expected));
    }

    [Test]
    public void NormalizesIsoDateTimesInsideMessages()
    {
        const string message = "Please wait until 2026-08-10T07:00:49.849Z to continue.";
        var expectedTime = DateTimeOffset.Parse("2026-08-10T07:00:49.849Z").ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        Assert.That(StatusFormatter.NormalizeDateTimes(message), Is.EqualTo($"Please wait until {expectedTime} to continue."));
    }

    [Test]
    public void UsesSwedishThousandsSeparatorsForIntegerMetrics()
    {
        var analysis = new EngineAnalysis(14, 25, 100, 12_345_678, "Nf6");
        var searchStatus = new EngineSearchStatus(100, 12_345_678, 14, 1, 20, "Nf6");

        Assert.Multiple(() =>
        {
            Assert.That(StatusFormatter.FormatAnalysis(analysis), Does.Contain("12\u00A0345\u00A0678 nodes"));
            Assert.That(StatusFormatter.FormatSearchStatus(searchStatus), Does.Contain("12\u00A0345\u00A0678 nodes • 12\u00A0346 kNPS"));
        });
    }

    [Test]
    public void IncludesCurrentMoveWhenPublished()
    {
        var searchStatus = new EngineSearchStatus(142, 123456, 12, 4, 27, "Nf6");

        Assert.That(StatusFormatter.FormatSearchStatus(searchStatus), Is.EqualTo("depth 12 • 1.42s • 123\u00A0456 nodes • 87 kNPS • Nf6 • 23/27"));
    }

    [Test]
    public void OmitsEmptyCurrentMoveCleanly()
    {
        var searchStatus = new EngineSearchStatus(142, 123456, 12, 4, 27, string.Empty);

        Assert.That(StatusFormatter.FormatSearchStatus(searchStatus), Is.EqualTo("depth 12 • 1.42s • 123\u00A0456 nodes • 87 kNPS • 23/27"));
    }
}
