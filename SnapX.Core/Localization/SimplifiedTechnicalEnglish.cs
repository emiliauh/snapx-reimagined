using System.Text.RegularExpressions;

namespace SnapX.Core.Localization;

/// <summary>
/// Small, deterministic checks for user-facing English text.
/// This is a writing aid. It does not claim to be a complete ASD-STE100 validator.
/// </summary>
public static partial class SimplifiedTechnicalEnglish
{
    private static readonly string[] ForbiddenPhrases =
    [
        "and so on",
        "and whatnot",
        "click here",
        "coming soon",
        "next generation",
        "no compromises",
        "pull requests welcome",
        "still working",
        "under construction",
        "whatnot"
    ];

    public static bool IsAcceptable(string? text) => Analyze(text).Count == 0;

    public static IReadOnlyList<string> Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var issues = new List<string>();
        var value = text.Trim();
        var searchable = Regex.Replace(value, @"\{[^{}]*\}", "value");

        if (ContractionPattern().IsMatch(searchable))
            issues.Add("Use a full form instead of a contraction.");

        foreach (var phrase in ForbiddenPhrases)
        {
            if (searchable.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                issues.Add($"Avoid the vague or idiomatic phrase '{phrase}'.");
        }

        if (searchable.Contains("...", StringComparison.Ordinal))
            issues.Add("Use a complete sentence instead of an ellipsis.");

        if (searchable.Contains('!'))
            issues.Add("Use a full stop instead of an exclamation mark.");

        foreach (var sentence in SentencePattern().Split(searchable))
        {
            int wordCount = WordPattern().Matches(sentence).Count;
            if (wordCount > 24)
            {
                issues.Add("Limit each sentence to 24 words.");
                break;
            }
        }

        return issues;
    }

    [GeneratedRegex(@"\b(?:can't|cannot|couldn't|didn't|doesn't|don't|isn't|it's|shouldn't|that's|there's|wasn't|weren't|won't|wouldn't|you'll|you're|you've)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContractionPattern();

    [GeneratedRegex(@"[^.!?]+[.!?]", RegexOptions.CultureInvariant)]
    private static partial Regex SentencePattern();

    [GeneratedRegex(@"[A-Za-z0-9]+(?:[-'][A-Za-z0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
