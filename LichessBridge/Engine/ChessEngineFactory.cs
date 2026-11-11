using DotNetLab.LichessBridge.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Engine;

public sealed class ChessEngineFactory(IOptions<EngineOptions> options, ILoggerFactory loggerFactory) : IChessEngineFactory
{
    public IChessEngine Create() => options.Value.Protocol switch
    {
        EngineProtocol.WinBoard => new WinBoardEngineFactory(options, loggerFactory).Create(),
        EngineProtocol.Uci => new UciEngineFactory(options, loggerFactory).Create(),
        _ => throw new InvalidOperationException($"Unsupported engine protocol '{options.Value.Protocol}'.")
    };
}
