namespace DotNetLab.LichessBridge.Tests;

internal sealed record UciTestSettings
{
    public string[] Options { get; init; } =
    [
        "option name Threads type spin default 1 min 1 max 16",
        "option name Hash type spin default 16 min 1 max 2048",
        "option name Ponder type check default false",
        "option name SyzygyPath type string default <empty>",
        "option name Style type combo default Normal var Normal var Very Aggressive",
        "option name Clear Hash type button"
    ];
    public string[] BestMoves { get; init; } = ["bestmove g1f3 ponder g8f6"];
    public string[] Info { get; init; } = [];
    public string[][] InfoBySearch { get; init; } = [];
    public string StopMove { get; init; } = "bestmove a2a3";
    public bool WaitForStop { get; init; }
    public bool OmitUciOk { get; init; }
    public bool OmitReadyOk { get; init; }
    public bool OmitStopReply { get; init; }
    public int StopDelayMilliseconds { get; init; }
}
