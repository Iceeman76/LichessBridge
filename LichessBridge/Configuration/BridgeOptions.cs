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
    public bool Rated { get; init; }
    public string[] Variants { get; init; } = ["standard"];
    public string[] Speeds { get; init; } = ["blitz", "rapid"];
}

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    [Required]
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = "Chess Engine";
    public string Command { get; init; } = "xboard";
    [Range(1, 600)]
    public int MoveTimeoutSeconds { get; init; } = 30;
    [Range(1, 60)]
    public int StartupTimeoutSeconds { get; init; } = 10;
}

public sealed class GamesOptions
{
    public const string SectionName = "Games";
    public string PgnDirectory { get; init; } = "pgn";
}
