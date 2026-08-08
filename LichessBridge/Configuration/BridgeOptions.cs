using System.ComponentModel.DataAnnotations;

namespace LichessBridge.Configuration;

public sealed class LichessOptions
{
    public const string SectionName = "Lichess";

    public string Token { get; init; } = string.Empty;
    public Uri BaseUri { get; init; } = new("https://lichess.org");
    public ChallengeFilterOptions ChallengeFilter { get; init; } = new();
}

public sealed class ChallengeFilterOptions
{
    public string[] Modes { get; init; } = ["casual"];
    public string[] Opponents { get; init; } = ["human", "bot"];
    public string[] Variants { get; init; } = ["standard"];
    public string[] Speeds { get; init; } = ["blitz", "rapid"];
}

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    [Required]
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = "Alarm";
    public bool Ponder { get; init; }
    public string[] StartupCommands { get; init; } = [];
}

public sealed class GamesOptions
{
    public const string SectionName = "Games";
    public string PgnDirectory { get; init; } = "pgn";
}

public sealed class ChatOptions
{
    public const string SectionName = "Chat";
    [StringLength(140)]
    public string StartMessage { get; init; } = string.Empty;
    [StringLength(140)]
    public string EndMessage { get; init; } = string.Empty;
}
