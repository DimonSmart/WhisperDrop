using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WhisperDrop.Models;

namespace WhisperDrop.State;

// Suggestions from the raw transcript only; never an authoritative replacement dictionary.
internal static class TranscriptReferenceTerms
{
    private static readonly Regex CapitalizedWord = new(@"\p{Lu}[\p{L}]{3,}", RegexOptions.Compiled);

    internal static IReadOnlyList<string> Collect(IReadOnlyList<RecognitionSegment> segments)
    {
        var occurrences = new Dictionary<string, (string Spelling, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in CapitalizedWord.Matches(segment.Text))
            {
                var before = segment.Text[..match.Index].TrimEnd();
                if (before.Length == 0 || ".?!¡¿".Contains(before[^1]))
                {
                    continue;
                }

                if (!seen.Add(match.Value))
                {
                    continue;
                }

                if (occurrences.TryGetValue(match.Value, out var entry))
                {
                    occurrences[match.Value] = (entry.Spelling, entry.Count + 1);
                }
                else
                {
                    occurrences.Add(match.Value, (match.Value, 1));
                }
            }
        }

        return occurrences.Values
            .Where(entry => entry.Count >= 2)
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Spelling, StringComparer.Ordinal)
            .Take(12)
            .Select(entry => entry.Spelling)
            .ToArray();
    }
}

public sealed record TranscriptCorrectionPreview(string Before, string After);

// Show only changed words, not punctuation-only/format-only edits.
// LCS chooses the last contiguous changed word group even when a segment contains multiple changes.
public static class TranscriptCorrectionPreviewBuilder
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private const int MaximumDiffTokens = 180;

    public static TranscriptCorrectionPreview? Create(string original, string corrected)
    {
        var before = Words.Matches(original).Select(match => match.Value).ToArray();
        var after = Words.Matches(corrected).Select(match => match.Value).ToArray();
        if (before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        if (before.Length > MaximumDiffTokens || after.Length > MaximumDiffTokens)
        {
            return CreateBySharedEdges(before, after);
        }

        var suffix = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
        {
            for (var j = after.Length - 1; j >= 0; j--)
            {
                suffix[i, j] = Equal(before[i], after[j])
                    ? suffix[i + 1, j + 1] + 1
                    : Math.Max(suffix[i + 1, j], suffix[i, j + 1]);
            }
        }

        var b = 0;
        var a = 0;
        var previousB = 0;
        var previousA = 0;
        TranscriptCorrectionPreview? latest = null;
        while (b < before.Length || a < after.Length)
        {
            if (b < before.Length && a < after.Length && Equal(before[b], after[a]))
            {
                if (b > previousB || a > previousA)
                {
                    latest = Preview(before[previousB..b], after[previousA..a]);
                }

                b++;
                a++;
                previousB = b;
                previousA = a;
                continue;
            }

            if (b < before.Length && (a == after.Length || suffix[b + 1, a] >= suffix[b, a + 1]))
            {
                b++;
            }
            else
            {
                a++;
            }
        }

        return b > previousB || a > previousA
            ? Preview(before[previousB..b], after[previousA..a])
            : latest;
    }

    private static TranscriptCorrectionPreview CreateBySharedEdges(string[] before, string[] after)
    {
        var start = 0;
        while (start < before.Length && start < after.Length && Equal(before[start], after[start]))
        {
            start++;
        }

        var endBefore = before.Length;
        var endAfter = after.Length;
        while (endBefore > start && endAfter > start && Equal(before[endBefore - 1], after[endAfter - 1]))
        {
            endBefore--;
            endAfter--;
        }

        return Preview(before[start..endBefore], after[start..endAfter]);
    }

    private static bool Equal(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(left, right);

    private static TranscriptCorrectionPreview Preview(string[] before, string[] after) =>
        new(Compact(before), Compact(after));

    private static string Compact(string[] words)
    {
        if (words.Length == 0)
        {
            return "_";
        }

        var text = string.Join(" ", words);
        if (text.Length <= 72)
        {
            return text;
        }

        if (words.Length <= 2)
        {
            return text; // Preserve the complete changed word, even when unusually long.
        }

        return $"{string.Join(" ", words.Take(2))} … {string.Join(" ", words.TakeLast(2))}";
    }
}
