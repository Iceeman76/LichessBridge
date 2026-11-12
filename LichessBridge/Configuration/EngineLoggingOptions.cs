using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace DotNetLab.LichessBridge.Configuration;

public sealed class EngineLoggingOptions
{
    public const string SectionName = "Logging";
    [EnumDataType(typeof(LogLevel))]
    public LogLevel EngineInfoStringLogLevel { get; init; } = LogLevel.Information;
}
