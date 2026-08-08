using LichessBridge.Configuration;
using Microsoft.Extensions.Options;

namespace LichessBridge.Lichess;

public sealed record ChallengeDecision(bool Accept, string DeclineReason)
{
    public static ChallengeDecision Accepted { get; } = new(true, string.Empty);
    public static ChallengeDecision Declined(string reason) => new(false, reason);
}

public interface IChallengeFilter
{
    ChallengeDecision Evaluate(LichessChallenge challenge, bool isBusy);
}

public sealed class ChallengeFilter(IOptions<LichessOptions> options) : IChallengeFilter
{
    private readonly ChallengeFilterOptions _filter = options.Value.ChallengeFilter;

    public ChallengeDecision Evaluate(LichessChallenge challenge, bool isBusy)
    {
        if (isBusy)
        {
            return ChallengeDecision.Declined("later");
        }

        if ((challenge.Rated == _filter.Rated) is false)
        {
            return ChallengeDecision.Declined(_filter.Rated ? "rated" : "casual");
        }

        if (_filter.Variants.Contains(challenge.Variant, StringComparer.OrdinalIgnoreCase) is false)
        {
            return ChallengeDecision.Declined("variant");
        }

        if (_filter.Speeds.Contains(challenge.Speed, StringComparer.OrdinalIgnoreCase) is false)
        {
            return ChallengeDecision.Declined("timeControl");
        }

        return ChallengeDecision.Accepted;
    }
}
