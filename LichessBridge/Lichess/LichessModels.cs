using LichessBridge.Domain;

namespace LichessBridge.Lichess;

public abstract record AccountEvent;
public sealed record ChallengeEvent(LichessChallenge Challenge) : AccountEvent;
public sealed record GameStartEvent(string GameId) : AccountEvent;
public sealed record GameFinishEvent(string GameId) : AccountEvent;
public sealed record UnknownAccountEvent(string Type) : AccountEvent;

public sealed record LichessChallenge(string Id, string Challenger, bool Rated, string Variant, string Speed, string ChallengerId = "");

public abstract record GameStreamEvent;
public sealed record GameFullEvent(string GameId, string WhiteId, string WhiteName, string BlackId, string BlackName, string InitialFen, int InitialSeconds, int IncrementSeconds, GameState State) : GameStreamEvent;
public sealed record GameStateEvent(GameState State) : GameStreamEvent;
public sealed record UnknownGameEvent(string Type) : GameStreamEvent;

public sealed record GameState(IReadOnlyList<ChessMove> Moves, int WhiteTimeMilliseconds, int BlackTimeMilliseconds, string Status, ChessColor? Winner)
{
    public bool IsFinished => Status is not ("created" or "started");
}

public sealed record LichessAccount(string Id, string Username);

public interface ILichessClient
{
    Task<LichessAccount> GetAccountAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<AccountEvent> StreamAccountEventsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<GameStreamEvent> StreamGameAsync(string gameId, CancellationToken cancellationToken);
    Task AcceptChallengeAsync(string challengeId, CancellationToken cancellationToken);
    Task DeclineChallengeAsync(string challengeId, string reason, CancellationToken cancellationToken);
    Task PlayMoveAsync(string gameId, ChessMove move, CancellationToken cancellationToken);
    Task<string> ExportPgnAsync(string gameId, CancellationToken cancellationToken);
}
