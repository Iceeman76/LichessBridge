using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Game;
using DotNetLab.LichessBridge.Lichess;
using Microsoft.Extensions.Logging;

namespace DotNetLab.LichessBridge.Status;

public interface IStatusDisplay
{
    void UpdateClock(GameClockStatus? clock);
    void ShowHeader(StatusHeader header);
    void ShowGame(GameStatus game);
    void ShowAnalysis(EngineAnalysis? analysis);
    void ShowSearchStatus(EngineSearchStatus? searchStatus);
    void ShowHint(string? hint);
    void ClearGame();
    void Show(string status);
    void ShowQuit(bool requested);
    void ShowResign(ResignationRequestResult result);
    void ShowLogs(IReadOnlyList<DashboardLogEntry> entries);
}

public sealed record DashboardLogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, Exception? Exception);
public sealed record StatusHeader(string EngineName, string AccountName, bool Ponder, bool AutoBotChallenge, IReadOnlyList<string> Modes, IReadOnlyList<string> Opponents, IReadOnlyList<string> Variants, IReadOnlyList<string> Speeds, IReadOnlyList<LichessRating> Ratings);
public sealed record GameStatus(string GameId, string Mode, string Speed, string TimeControl, string WhiteName, string? WhiteTitle, int? WhiteRating, string BlackName, string? BlackTitle, int? BlackRating, string LastMove, int WhiteTimeMilliseconds, int BlackTimeMilliseconds, ChessColor? ActiveColor)
{
    public IReadOnlyList<ChessMove>? MoveHistory { get; init; }
}
