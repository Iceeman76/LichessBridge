using System.ComponentModel.DataAnnotations;

namespace DotNetLab.LichessBridge.Configuration;

public sealed class ConsoleOutputOptions
{
    public const string SectionName = "Logging";
    [Range(0, int.MaxValue)]
    public int ConsoleQuietBelowMilliseconds { get; init; } = 10_000;
    [Range(0, int.MaxValue)]
    public int ConsoleResumeAboveMilliseconds { get; init; } = 15_000;
}
