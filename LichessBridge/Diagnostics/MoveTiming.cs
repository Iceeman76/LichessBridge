using System.Diagnostics;
using DotNetLab.LichessBridge.Domain;
using Microsoft.Extensions.Logging;

namespace DotNetLab.LichessBridge.Diagnostics;

public sealed class MoveTiming(string gameId, int ply, long receivedTimestamp, DateTimeOffset receivedUtc) : IDisposable
{
    private static readonly AsyncLocal<MoveTiming?> current = new();
    private readonly MoveTiming? previous = current.Value;
    private readonly List<CommandTiming> commands = [];
    public static MoveTiming? Current => current.Value;
    public string GameId { get; } = gameId;
    public int Ply { get; } = ply;
    public string CorrelationId { get; } = Guid.NewGuid().ToString("N");
    public long ReceivedTimestamp { get; } = receivedTimestamp;
    public DateTimeOffset ReceivedUtc { get; } = receivedUtc;
    public long PositionStarted { get; set; }
    public long PositionCompleted { get; set; }
    public long WaitStarted { get; set; }
    public long StdoutRead { get; set; }
    public DateTimeOffset? StdoutUtc { get; set; }
    public long EngineReceived { get; set; }
    public DateTimeOffset? GetMoveUtc { get; set; }
    public long ControllerReceived { get; set; }
    public DateTimeOffset? ControllerUtc { get; set; }
    public long HttpStarted { get; set; }
    public long Completed { get; set; }
    public DateTimeOffset CompletedUtc { get; set; }
    public double HttpMilliseconds { get; set; }
    public double HttpQueueMilliseconds { get; set; }
    public int HttpAttempts { get; set; }
    public bool PonderEnabled { get; set; }
    public bool WasPondering { get; set; }
    public bool LeftAnalysis { get; set; }
    public string Outcome { get; set; } = "http-accepted";
    public string SearchPath { get; set; } = "unavailable";
    public string EngineMoveFormat { get; set; } = "unavailable";
    public double MoveConversionMilliseconds { get; set; } = double.NaN;
    public void Activate() => current.Value = this;
    public void Dispose() => current.Value = previous;

    public void AddCommand(string command, long started, long acquired, long written, long flushed)
    {
        commands.Add(new CommandTiming(command, Milliseconds(started, acquired), Milliseconds(acquired, written), Milliseconds(written, flushed)));
    }

    public void Log(ILogger logger, ChessMove? move, int whiteTime, int blackTime, int whiteIncrement, int blackIncrement)
    {
        if (logger.IsEnabled(LogLevel.Debug) is false)
        {
            return;
        }

        logger.LogDebug("MoveTiming GameId={GameId} Ply={Ply} Move={Move} CorrelationId={CorrelationId} Outcome={Outcome} EventUtc={EventUtc:O} StdoutUtc={StdoutUtc:O} GetMoveUtc={GetMoveUtc:O} ControllerUtc={ControllerUtc:O} CompletedUtc={CompletedUtc:O} LocalTotalMs={LocalTotalMs:F3} EventHandlingMs={EventHandlingMs:F3} SetPositionMs={SetPositionMs:F3} EngineSetupMs={EngineSetupMs:F3} ResponseWaitMs={ResponseWaitMs:F3} StdoutToGetMoveMs={StdoutToGetMoveMs:F3} EnginePostResponseMs={EnginePostResponseMs:F3} ControllerPostResponseMs={ControllerPostResponseMs:F3} SubmissionMs={SubmissionMs:F3} HttpMs={HttpMs:F3} HttpQueueMs={HttpQueueMs:F3} HttpAttempts={HttpAttempts} CommandLockMs={CommandLockMs:F3} CommandWriteMs={CommandWriteMs:F3} CommandFlushMs={CommandFlushMs:F3} Commands={Commands} PonderEnabled={PonderEnabled} WasPondering={WasPondering} SearchPath={SearchPath} EngineMoveFormat={EngineMoveFormat} MoveConversionMs={MoveConversionMs:F3} LeftAnalysis={LeftAnalysis} WhiteMs={WhiteMs} BlackMs={BlackMs} WhiteIncrementMs={WhiteIncrementMs} BlackIncrementMs={BlackIncrementMs}", GameId, Ply, move, CorrelationId, Outcome, ReceivedUtc, StdoutUtc, GetMoveUtc, ControllerUtc, CompletedUtc, Milliseconds(ReceivedTimestamp, Completed), Milliseconds(ReceivedTimestamp, PositionStarted), Milliseconds(PositionStarted, PositionCompleted), Milliseconds(PositionCompleted, WaitStarted), Milliseconds(WaitStarted, EngineReceived), Milliseconds(StdoutRead, EngineReceived), Milliseconds(EngineReceived, ControllerReceived), Milliseconds(ControllerReceived, HttpStarted), Milliseconds(HttpStarted, Completed), HttpMilliseconds, HttpQueueMilliseconds, HttpAttempts, commands.Sum(command => command.LockMs), commands.Sum(command => command.WriteMs), commands.Sum(command => command.FlushMs), string.Join(";", commands.Select(command => command.Command)), PonderEnabled, WasPondering, SearchPath, EngineMoveFormat, MoveConversionMilliseconds, LeftAnalysis, whiteTime, blackTime, whiteIncrement, blackIncrement);

        foreach (var command in commands)
        {
            logger.LogDebug("EngineCommand GameId={GameId} Ply={Ply} Move={Move} CorrelationId={CorrelationId} Command={Command} LockMs={LockMs:F3} WriteMs={WriteMs:F3} FlushMs={FlushMs:F3}", GameId, Ply, move, CorrelationId, command.Command, command.LockMs, command.WriteMs, command.FlushMs);
        }
    }

    public static double Milliseconds(long start, long end) => start is 0 || end is 0 ? double.NaN : Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
    private sealed record CommandTiming(string Command, double LockMs, double WriteMs, double FlushMs);
}
