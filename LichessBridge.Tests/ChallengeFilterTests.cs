using LichessBridge.Configuration;
using LichessBridge.Lichess;
using Microsoft.Extensions.Options;

namespace LichessBridge.Tests;

public sealed class ChallengeFilterTests
{
    private readonly ChallengeFilter _filter = new(Options.Create(new LichessOptions
    {
        ChallengeFilter = new ChallengeFilterOptions
        {
            Rated = false,
            Variants = ["standard"],
            Speeds = ["blitz", "rapid"]
        }
    }));

    [Test]
    public void AcceptsCasualStandardBlitz()
    {
        var result = _filter.Evaluate(Challenge(rated: false), isBusy: false);
        Assert.That(result.Accept, Is.True);
    }

    [TestCase(true, "standard", "blitz", false, "casual")]
    [TestCase(false, "chess960", "blitz", false, "variant")]
    [TestCase(false, "standard", "bullet", false, "timeControl")]
    [TestCase(false, "standard", "blitz", true, "later")]
    public void DeclinesUnsupportedChallenges(bool rated, string variant, string speed, bool busy, string reason)
    {
        var result = _filter.Evaluate(Challenge(rated, variant, speed), busy);
        Assert.That(result.Accept, Is.False);
        Assert.That(result.DeclineReason, Is.EqualTo(reason));
    }

    private static LichessChallenge Challenge(bool rated, string variant = "standard", string speed = "blitz") => new("challenge-id", "Opponent", rated, variant, speed);
}
