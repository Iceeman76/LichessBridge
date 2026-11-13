using System.Net;

namespace DotNetLab.LichessBridge.Lichess;

public sealed class LichessRateLimitException(DateTimeOffset retryAt) : HttpRequestException($"Lichess API calls are paused until {retryAt:O}.", null, HttpStatusCode.TooManyRequests)
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
