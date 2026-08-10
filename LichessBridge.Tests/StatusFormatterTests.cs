using LichessBridge.Status;

namespace LichessBridge.Tests;

public sealed class StatusFormatterTests
{
    [Test]
    public void FormatsDateAndTimeWithoutFractionOrOffset()
    {
        var value = new DateTimeOffset(2026, 8, 10, 7, 0, 49, 849, TimeSpan.Zero);

        Assert.That(StatusFormatter.FormatDateTime(value), Is.EqualTo("2026-08-10 07:00:49"));
    }

    [Test]
    public void NormalizesIsoDateTimesInsideMessages()
    {
        const string message = "Please wait until 2026-08-10T07:00:49.849Z to continue.";

        Assert.That(StatusFormatter.NormalizeDateTimes(message), Is.EqualTo("Please wait until 2026-08-10 07:00:49 to continue."));
    }
}
