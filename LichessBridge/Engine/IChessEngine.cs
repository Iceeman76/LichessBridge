using LichessBridge.Domain;

namespace LichessBridge.Engine;

public interface IChessEngine : IAsyncDisposable
{
    bool IsRunning { get; }
    event Action<EngineAnalysis>? AnalysisUpdated;
    event Action<EngineSearchStatus>? SearchStatusUpdated;
    event Action<string>? HintUpdated;
    Task StartAsync(CancellationToken cancellationToken);
    Task SetPositionAsync(IReadOnlyList<ChessMove> moves, int initialSeconds, int incrementSeconds, CancellationToken cancellationToken);
    Task StartPonderingAsync(CancellationToken cancellationToken);
    Task<ChessMove> GetMoveAsync(ChessColor engineColor, ChessClock clock, CancellationToken cancellationToken);
    Task SendResultAsync(GameResult result, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IChessEngineFactory
{
    IChessEngine Create();
}
