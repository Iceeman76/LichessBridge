using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LichessBridge.Configuration;

public sealed class LichessOptions
{
    public const string SectionName = "Lichess";

    public string Token { get; init; } = string.Empty;
    public Uri BaseUri { get; init; } = new("https://lichess.org");
    [ValidateObjectMembers]
    public ChallengeFilterOptions ChallengeFilter { get; init; } = new();
}

public sealed class ChallengeFilterOptions
{
    public string[] Modes { get; init; } = ["casual"];
    public string[] Opponents { get; init; } = ["human", "bot"];
    public string[] Variants { get; init; } = ["standard"];
    public string[] Speeds { get; init; } = ["blitz", "rapid"];
    [Range(0, 10_800)]
    public int? MinBaseSeconds { get; init; }
    [Range(0, 10_800)]
    public int? MaxBaseSeconds { get; init; }
    [Range(0, 180)]
    public int? MinIncrementSeconds { get; init; }
    [Range(0, 180)]
    public int? MaxIncrementSeconds { get; init; }
    [Range(0, 4000)]
    public int? MinRating { get; init; }
    [Range(0, 4000)]
    public int? MaxRating { get; init; }
    [Range(0, 4000)]
    public int? MaxRatingDifference { get; init; }
    public string[] AllowList { get; init; } = [];
    public string[] BlockList { get; init; } = [];
    [Range(1, int.MaxValue)]
    public int MaxSimultaneousGamesPerUser { get; init; } = 1;
    public bool BulletRequiresIncrement { get; init; }
}

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    [Required]
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = "Chess Engine";
    public bool Ponder { get; init; }
    [Range(0, 60_000)]
    public int MoveOverheadMilliseconds { get; init; }
    [Range(0, 60_000)]
    public int SearchStatusPollMilliseconds { get; init; } = 1000;
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
