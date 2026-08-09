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

    [TestCase(59, 2, "tooFast")]
    [TestCase(301, 2, "tooSlow")]
    [TestCase(120, 0, "timeControl")]
    [TestCase(120, 4, "timeControl")]
    public void AppliesExactTimeControlLimits(int initialSeconds, int incrementSeconds, string reason)
    {
        var filter = CreateFilter(new ChallengeFilterOptions { MinBaseSeconds = 60, MaxBaseSeconds = 300, MinIncrementSeconds = 1, MaxIncrementSeconds = 3 });
        var challenge = Challenge(rated: false) with { InitialSeconds = initialSeconds, IncrementSeconds = incrementSeconds };

        var result = filter.Evaluate(challenge, isBusy: false);

        Assert.That(result.DeclineReason, Is.EqualTo(reason));
    }

    [Test]
    public void RequiresIncrementForBulletWhenConfigured()
    {
        var filter = CreateFilter(new ChallengeFilterOptions { Speeds = ["bullet"], BulletRequiresIncrement = true });
        var challenge = Challenge(rated: false, speed: "bullet") with { InitialSeconds = 60, IncrementSeconds = 0 };

        var result = filter.Evaluate(challenge, isBusy: false);

        Assert.That(result.DeclineReason, Is.EqualTo("noIncrement"));
    }

    [TestCase(999, 1100, "generic")]
    [TestCase(2001, 1900, "generic")]
    [TestCase(1500, 1801, "generic")]
    [TestCase(1500, 1600, "")]
    public void AppliesRatingLimits(int challengerRating, int botRating, string reason)
    {
        var filter = CreateFilter(new ChallengeFilterOptions { MinRating = 1000, MaxRating = 2000, MaxRatingDifference = 200 });
        var challenge = Challenge(rated: false) with { ChallengerRating = challengerRating };

        var result = filter.Evaluate(challenge, isBusy: false, botRating: botRating);

        Assert.That(result.DeclineReason, Is.EqualTo(reason));
    }

    [Test]
    public void AppliesAllowAndBlockLists()
    {
        var filter = CreateFilter(new ChallengeFilterOptions { AllowList = ["friend", "blocked"], BlockList = ["blocked"] });

        Assert.Multiple(() =>
        {
            Assert.That(filter.Evaluate(Challenge(rated: false) with { ChallengerId = "friend" }, isBusy: false).Accept, Is.True);
            Assert.That(filter.Evaluate(Challenge(rated: false) with { ChallengerId = "stranger" }, isBusy: false).DeclineReason, Is.EqualTo("generic"));
            Assert.That(filter.Evaluate(Challenge(rated: false) with { ChallengerId = "blocked" }, isBusy: false).DeclineReason, Is.EqualTo("generic"));
        });
    }

    [Test]
    public void LimitsSimultaneousGamesAgainstSameUser()
    {
        var filter = CreateFilter(new ChallengeFilterOptions { MaxSimultaneousGamesPerUser = 1 });

        var result = filter.Evaluate(Challenge(rated: false), isBusy: true, activeGamesAgainstChallenger: 1);

        Assert.That(result.DeclineReason, Is.EqualTo("later"));
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

    private static ChallengeFilter CreateFilter(ChallengeFilterOptions filter) => new(Options.Create(new LichessOptions
    {
        ChallengeFilter = filter
    }));
}
