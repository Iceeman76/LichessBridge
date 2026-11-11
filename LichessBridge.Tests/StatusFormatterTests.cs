using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Status;

namespace DotNetLab.LichessBridge.Tests;

public sealed class StatusFormatterTests
{
    [TestCase(0, "0%")]
    [TestCase(1, "0.1%")]
    [TestCase(375, "37.5%")]
    [TestCase(1000, "100%")]
    public void DisplaysHashFullnessAsPercentAndTablebaseHits(int permille, string expectedPercent)
    {
        var status = new EngineSearchStatus(100, 1000, 14, 0, 0, string.Empty) { HashFullPermille = permille, TablebaseHits = 1234 };

        var formatted = StatusFormatter.FormatSearchStatus(status);

        Assert.That(formatted, Does.Contain($"Hash {expectedPercent} • TB 1\u00A0234"));
    }

    [Test]
    public void DistinguishesZeroMetricsFromUnavailableMetrics()
    {
        var unknown = new EngineSearchStatus(100, 1000, 14, 0, 0, string.Empty);
        var reportedZero = unknown with { HashFullPermille = 0, TablebaseHits = 0 };

        Assert.Multiple(() =>
        {
            Assert.That(StatusFormatter.FormatSearchStatus(unknown), Does.Not.Contain("Hash").And.Not.Contain("TB"));
            Assert.That(StatusFormatter.FormatSearchStatus(reportedZero), Does.Contain("Hash 0% • TB 0"));
        });
    }

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
