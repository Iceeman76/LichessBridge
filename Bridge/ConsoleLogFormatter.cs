using System.Text.RegularExpressions;

namespace DotNetLab.Bridge;

internal static class ConsoleLogFormatter
{
    private static readonly Regex IdentifierSuffix = new(@" \[(?:GameId|ChallengeId): [^\]]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ApiIdentifier = new(@"(/api/(?:bot/game/(?:stream/)?|challenge/|game/export/))[A-Za-z0-9]{8,12}(?=[/\s?'""\)]|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuotedGameIdentifier = new(@"\b([Gg]ame) '[A-Za-z0-9]{8,12}'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Format(string message)
    {
        var withoutSuffix = IdentifierSuffix.Replace(message, string.Empty);
        var withoutApiIdentifiers = ApiIdentifier.Replace(withoutSuffix, "$1…");

        return QuotedGameIdentifier.Replace(withoutApiIdentifiers, "$1");
    }
}
