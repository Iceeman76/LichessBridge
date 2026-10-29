using System.Net;
using DotNetLab.LichessBridge.Domain;

namespace DotNetLab.LichessBridge.Lichess;

public abstract record AccountEvent;
public sealed record ChallengeEvent(LichessChallenge Challenge) : AccountEvent;
public sealed record ChallengeEndedEvent(string ChallengeId, string Reason, string DeclineReasonKey = "") : AccountEvent;
public sealed record GameStartEvent(string GameId) : AccountEvent;
public sealed record GameFinishEvent(string GameId) : AccountEvent;
public sealed record UnknownAccountEvent(string Type) : AccountEvent;

public sealed record LichessChallenge(string Id, string Challenger, bool Rated, string Variant, string Speed, string ChallengerId = "", string Destination = "", string TimeControl = "", bool ChallengerIsBot = false, int? InitialSeconds = null, int? IncrementSeconds = null, int? ChallengerRating = null);

public abstract record GameStreamEvent;
public sealed record GameFullEvent(string GameId, string WhiteId, string WhiteName, string BlackId, string BlackName, string InitialFen, int InitialSeconds, int IncrementSeconds, GameState State, bool Rated = false, string Speed = "unknown", int? WhiteRating = null, int? BlackRating = null, string? WhiteTitle = null, string? BlackTitle = null) : GameStreamEvent;
public sealed record GameStateEvent(GameState State) : GameStreamEvent;
public sealed record UnknownGameEvent(string Type) : GameStreamEvent;

public sealed record GameState(IReadOnlyList<ChessMove> Moves, int WhiteTimeMilliseconds, int BlackTimeMilliseconds, string Status, ChessColor? Winner)
{
    public bool IsFinished => Status is not ("created" or "started");
}

public sealed record LichessRating(string Speed, int Rating, int? Games = null);

public sealed record LichessAccount(string Id, string Username)
{
    public IReadOnlyList<LichessRating> Ratings { get; init; } = [];
}

public sealed record OnlineBot(string Id, string Username)
{
    public IReadOnlyList<LichessRating> Ratings { get; init; } = [];
}
public sealed record BotChallengeRequest(bool Rated, string Variant, int InitialSeconds, int IncrementSeconds);

public sealed class ChallengeCreationException(HttpStatusCode statusCode, string apiMessage) : HttpRequestException($"Lichess rejected the challenge request: {apiMessage}", null, statusCode)
{
    public string ApiMessage { get; } = apiMessage;
}

public interface ILichessClient
{
    Task<LichessAccount> GetAccountAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<AccountEvent> StreamAccountEventsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<GameStreamEvent> StreamGameAsync(string gameId, CancellationToken cancellationToken);
    Task AcceptChallengeAsync(string challengeId, CancellationToken cancellationToken);
    Task DeclineChallengeAsync(string challengeId, string reason, CancellationToken cancellationToken);
    Task SendChatMessageAsync(string gameId, string message, CancellationToken cancellationToken);
    Task PlayMoveAsync(string gameId, ChessMove move, CancellationToken cancellationToken);
    Task AbortGameAsync(string gameId, CancellationToken cancellationToken);
    Task ResignGameAsync(string gameId, CancellationToken cancellationToken);
    Task<string> ExportPgnAsync(string gameId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OnlineBot>> GetOnlineBotsAsync(CancellationToken cancellationToken);
    Task<string> CreateChallengeAsync(string username, BotChallengeRequest challenge, CancellationToken cancellationToken);
    Task CancelChallengeAsync(string challengeId, CancellationToken cancellationToken);
}
