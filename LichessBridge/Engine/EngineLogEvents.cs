using Microsoft.Extensions.Logging;

namespace DotNetLab.LichessBridge.Engine;

public static class EngineLogEvents
{
    public static readonly EventId UciInfoString = new(2001, nameof(UciInfoString));
}
