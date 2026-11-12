using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Engine;

public sealed class ChessEngineFactory(IOptions<EngineOptions> options, ILoggerFactory loggerFactory, ConsoleOutputPolicy? outputPolicy = null, IOptions<EngineLoggingOptions>? loggingOptions = null) : IChessEngineFactory
{
    public IChessEngine Create() => options.Value.Protocol switch
    {
        EngineProtocol.WinBoard => new WinBoardEngineFactory(options, loggerFactory).Create(),
        EngineProtocol.Uci => new UciEngineFactory(options, loggerFactory, outputPolicy, loggingOptions).Create(),
        _ => throw new InvalidOperationException($"Unsupported engine protocol '{options.Value.Protocol}'.")
    };
}
