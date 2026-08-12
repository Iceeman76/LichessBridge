using LichessBridge.Status;

namespace LichessBridge.Tests;

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
}
