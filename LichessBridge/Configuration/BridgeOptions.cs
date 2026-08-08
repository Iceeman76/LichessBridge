using System.ComponentModel.DataAnnotations;

namespace LichessBridge.Configuration;

public sealed class LichessOptions
{
    public const string SectionName = "Lichess";

    public string Token { get; set; } = string.Empty;
    public Uri BaseUri { get; set; } = new("https://lichess.org");
    public ChallengeFilterOptions ChallengeFilter { get; set; } = new();
}

public sealed class ChallengeFilterOptions
{
    public bool Rated { get; set; }
    public string[] Variants { get; set; } = ["standard"];
    public string[] Speeds { get; set; } = ["blitz", "rapid"];
}

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    [Required]
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = "Alarm";
    public string Command { get; set; } = "xboard";
    [Range(1, 600)]
    public int MoveTimeoutSeconds { get; set; } = 30;
    [Range(1, 60)]
    public int StartupTimeoutSeconds { get; set; } = 10;
}

public sealed class GamesOptions
{
    public const string SectionName = "Games";
    public string PgnDirectory { get; set; } = "pgn";
}
