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
    public string[] Modes { get; set; } = ["casual"];
    public string[] Opponents { get; set; } = ["human", "bot"];
    public string[] Variants { get; set; } = ["standard"];
    public string[] Speeds { get; set; } = ["blitz", "rapid"];
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

public sealed class AutoBotChallengeOptions
{
    public const string SectionName = "AutoBotChallenge";

    public bool Enabled { get; init; }
    [Range(1, 86_400)]
    public int IdleDelaySeconds { get; init; } = 60;
    [Range(5, 300)]
    public int ChallengeTimeoutSeconds { get; init; } = 30;
    [Range(1, 10_080)]
    public int OpponentCooldownMinutes { get; init; } = 60;
    [Range(1, 720)]
    public int DeclineHistoryRetentionHours { get; init; } = 24;
    [Required]
    public string AutoChallengeStatePath { get; init; } = "data/auto-bot-challenge-history.json";
    public string[] AllowList { get; init; } = [];
    public string[] BlockList { get; init; } = [];
    [Range(0, 4000)]
    public int? MinRating { get; init; }
    [Range(0, 4000)]
    public int? MaxRating { get; init; }
    [Range(0, 4000)]
    public int? MaxRatingDifference { get; init; }
    [ValidateEnumeratedItems]
    public AutoBotChallengeProfileOptions[] Profiles { get; init; } = [];
}

public sealed class AutoBotChallengeProfileOptions
{
    public bool Enabled { get; init; }
    [Required]
    public string Name { get; init; } = string.Empty;
    [Required]
    public string Mode { get; init; } = "casual";
    [Required]
    public string Variant { get; init; } = "standard";
    [Range(0, 10_800)]
    public int InitialSeconds { get; init; } = 180;
    [Range(0, 180)]
    public int IncrementSeconds { get; init; } = 2;
    [Range(0, 4000)]
    public int? MinRating { get; init; }
    [Range(0, 4000)]
    public int? MaxRating { get; init; }
    [Range(0, 4000)]
    public int? MaxRatingDifference { get; init; }
}

public sealed class GamesOptions
{
    public const string SectionName = "Games";
    [Required]
    public string PgnPath { get; init; } = "games.pgn";
    [Range(0, 3600)]
    public int AbortIfNoMoveSeconds { get; init; } = 60;
}

public sealed class ChatOptions
{
    public const string SectionName = "Chat";
    [StringLength(140)]
    public string StartMessage { get; init; } = string.Empty;
    [StringLength(140)]
    public string EndMessage { get; init; } = string.Empty;
}
