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
            Modes = ["casual"],
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

    [TestCase(false)]
    [TestCase(true)]
    public void AcceptsBothConfiguredModes(bool rated)
    {
        var filter = CreateFilter(["casual", "rated"]);

        var result = filter.Evaluate(Challenge(rated), isBusy: false);

        Assert.That(result.Accept, Is.True);
    }

    [Test]
    public void RatedOnlyDeclinesCasualChallenge()
    {
        var filter = CreateFilter(["rated"]);

        var result = filter.Evaluate(Challenge(rated: false), isBusy: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Accept, Is.False);
            Assert.That(result.DeclineReason, Is.EqualTo("rated"));
        });
    }

    [TestCase(new[] { "human" }, true, "noBot")]
    [TestCase(new[] { "bot" }, false, "onlyBot")]
    public void DeclinesUnsupportedOpponentType(string[] opponents, bool challengerIsBot, string reason)
    {
        var filter = CreateFilter(["casual"], opponents);
        var challenge = Challenge(rated: false) with { ChallengerIsBot = challengerIsBot };

        var result = filter.Evaluate(challenge, isBusy: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Accept, Is.False);
            Assert.That(result.DeclineReason, Is.EqualTo(reason));
        });
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

    private static ChallengeFilter CreateFilter(string[] modes, string[]? opponents = null) => new(Options.Create(new LichessOptions
    {
        ChallengeFilter = new ChallengeFilterOptions
        {
            Modes = modes,
            Opponents = opponents ?? ["human", "bot"],
            Variants = ["standard"],
            Speeds = ["blitz", "rapid"]
        }
    }));
}
