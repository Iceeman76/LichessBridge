using DotNetLab.LichessBridge.Configuration;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Lichess;

public sealed record ChallengeDecision(bool Accept, string DeclineReason)
{
    public static ChallengeDecision Accepted { get; } = new(true, string.Empty);
    public static ChallengeDecision Declined(string reason) => new(false, reason);
}

public interface IChallengeFilter
{
    ChallengeDecision Evaluate(LichessChallenge challenge, bool isBusy, int activeGamesAgainstChallenger = 0, int? botRating = null);
}

public sealed class ChallengeFilter(IOptions<LichessOptions> options) : IChallengeFilter
{
    private readonly ChallengeFilterOptions _filter = options.Value.ChallengeFilter;

    public ChallengeDecision Evaluate(LichessChallenge challenge, bool isBusy, int activeGamesAgainstChallenger = 0, int? botRating = null)
    {
        if (Matches(_filter.BlockList, challenge))
        {
            return ChallengeDecision.Declined("generic");
        }

        if (_filter.AllowList.Length > 0 && Matches(_filter.AllowList, challenge) is false)
        {
            return ChallengeDecision.Declined("generic");
        }

        if (activeGamesAgainstChallenger >= _filter.MaxSimultaneousGamesPerUser)
        {
            return ChallengeDecision.Declined("later");
        }

        if (isBusy)
        {
            return ChallengeDecision.Declined("later");
        }

        var mode = challenge.Rated ? "rated" : "casual";

        if (_filter.Modes.Contains(mode, StringComparer.OrdinalIgnoreCase) is false)
        {
            return ChallengeDecision.Declined(challenge.Rated ? "casual" : "rated");
        }

        var opponent = challenge.ChallengerIsBot ? "bot" : "human";

        if (_filter.Opponents.Contains(opponent, StringComparer.OrdinalIgnoreCase) is false)
        {
            return ChallengeDecision.Declined(challenge.ChallengerIsBot ? "noBot" : "onlyBot");
        }

        if (_filter.Variants.Contains(challenge.Variant, StringComparer.OrdinalIgnoreCase) is false)
        {
            return ChallengeDecision.Declined("variant");
        }

        if (_filter.Speeds.Contains(challenge.Speed, StringComparer.OrdinalIgnoreCase) is false)
        {
            return ChallengeDecision.Declined("timeControl");
        }

        if (_filter.BulletRequiresIncrement && challenge.Speed.Equals("bullet", StringComparison.OrdinalIgnoreCase) && challenge.IncrementSeconds is null or 0)
        {
            return ChallengeDecision.Declined("noIncrement");
        }

        var timeDecision = EvaluateTimeControl(challenge);

        if (timeDecision is not null)
        {
            return timeDecision;
        }

        if (HasRatingConstraint() && challenge.ChallengerRating is null)
        {
            return ChallengeDecision.Declined("generic");
        }

        if ((_filter.MinRating is not null && challenge.ChallengerRating < _filter.MinRating) || (_filter.MaxRating is not null && challenge.ChallengerRating > _filter.MaxRating))
        {
            return ChallengeDecision.Declined("generic");
        }

        if (_filter.MaxRatingDifference is not null && botRating is null)
        {
            return ChallengeDecision.Declined("generic");
        }

        if (_filter.MaxRatingDifference is int configuredDifference && challenge.ChallengerRating is int challengerRating && botRating is int configuredBotRating && Math.Abs(challengerRating - configuredBotRating) > configuredDifference)
        {
            return ChallengeDecision.Declined("generic");
        }

        return ChallengeDecision.Accepted;
    }

    private ChallengeDecision? EvaluateTimeControl(LichessChallenge challenge)
    {
        var hasBaseConstraint = _filter.MinBaseSeconds is not null || _filter.MaxBaseSeconds is not null;
        var hasIncrementConstraint = _filter.MinIncrementSeconds is not null || _filter.MaxIncrementSeconds is not null;

        if ((hasBaseConstraint && challenge.InitialSeconds is null) || (hasIncrementConstraint && challenge.IncrementSeconds is null))
        {
            return ChallengeDecision.Declined("timeControl");
        }

        if (_filter.MinBaseSeconds is not null && challenge.InitialSeconds < _filter.MinBaseSeconds)
        {
            return ChallengeDecision.Declined("tooFast");
        }

        if (_filter.MaxBaseSeconds is not null && challenge.InitialSeconds > _filter.MaxBaseSeconds)
        {
            return ChallengeDecision.Declined("tooSlow");
        }

        if ((_filter.MinIncrementSeconds is not null && challenge.IncrementSeconds < _filter.MinIncrementSeconds) || (_filter.MaxIncrementSeconds is not null && challenge.IncrementSeconds > _filter.MaxIncrementSeconds))
        {
            return ChallengeDecision.Declined("timeControl");
        }

        return null;
    }

    private bool HasRatingConstraint() => _filter.MinRating is not null || _filter.MaxRating is not null || _filter.MaxRatingDifference is not null;

    private static bool Matches(IEnumerable<string> users, LichessChallenge challenge) => users.Any(user => user.Equals(challenge.ChallengerId, StringComparison.OrdinalIgnoreCase) || user.Equals(challenge.Challenger, StringComparison.OrdinalIgnoreCase));
}
