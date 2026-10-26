using System;
using System.Text.RegularExpressions;
namespace WhisperDrop.State;

// Detect consecutive repeated speech or non-speech markers in native callbacks.
internal static class WhisperRepetitionGuard
{
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex NonSpeechMarker = new(
        @"\[(?:m[uú]sica|music|speaking\s+(?:spanish|english)|hablando\s+en\s+(?:español|ingl[eé]s)|applause|aplausos)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static int MarkerCount(string text) => NonSpeechMarker.Matches(text).Count;

    internal static int MeaningfulWords(string text) =>
        CountWords(NonSpeechMarker.Replace(text, " "));

    internal static bool IsSuspiciousRepair(string text, double seconds)
    {
        var words = MeaningfulWords(text);
        var markers = MarkerCount(text);
        // Real silence is possible; this is a reason to try a shorter decode,
        // not evidence that actual speech exists.
        return CountRepeatedTokens(text) > 0 ||
            HasMarkerLoop(text) ||
            markers > 0 && words < 8 ||
            seconds >= 10 && words < Math.Max(6, seconds * 0.5);
    }

    internal static bool HasMarkerLoop(string text)
    {
        var matches = NonSpeechMarker.Matches(text);
        for (var i = 1; i < matches.Count; i++)
        {
            var previousEnd = matches[i - 1].Index + matches[i - 1].Length;
            if (string.IsNullOrWhiteSpace(text.Substring(previousEnd, matches[i].Index - previousEnd)))
            {
                return true;
            }
        }
        return false;
    }

    internal static int RepairScore(string text) =>
        MeaningfulWords(text) - CountRepeatedTokens(text) * 2 - MarkerCount(text) * 3;

    internal static bool PreferShortRecovery(string beam, string shortRecovery, double seconds)
    {
        var beamSuspicious = IsSuspiciousRepair(beam, seconds);
        var shortSuspicious = IsSuspiciousRepair(shortRecovery, seconds);
        if (beamSuspicious != shortSuspicious)
        {
            return !shortSuspicious;
        }

        // Keep Beam Search unless the shorter pass is clearly more useful;
        // do not prefer a shorter result merely because it contains fewer markers.
        return RepairScore(shortRecovery) > RepairScore(beam) + 4 &&
            MeaningfulWords(shortRecovery) >= MeaningfulWords(beam) * 0.85;
    }


    internal static int CountWords(string text) => WordPattern.Matches(text).Count;

    internal static int CountRepeatedTokens(string text)
    {
        var matches = WordPattern.Matches(text);
        var words = new string[matches.Count];
        for (var index = 0; index < matches.Count; index++)
        {
            words[index] = matches[index].Value.ToLowerInvariant();
        }

        var repeated = 0;
        for (var i = 0; i < words.Length;)
        {
            var blockSize = 0;
            // Require at least three successive occurrences of 3–32 words.
            for (var size = 3; size <= 32 && i + 3 * size <= words.Length; size++)
            {
                var identical = true;
                for (var j = 0; j < size && identical; j++)
                {
                    identical = words[i + j] == words[i + size + j] &&
                        words[i + j] == words[i + 2 * size + j];
                }

                if (identical)
                {
                    blockSize = size;
                    break;
                }
            }

            if (blockSize == 0)
            {
                i++;
                continue;
            }

            var copies = 3;
            while (i + (copies + 1) * blockSize <= words.Length)
            {
                var identical = true;
                for (var j = 0; j < blockSize; j++)
                {
                    if (words[i + j] != words[i + copies * blockSize + j])
                    {
                        identical = false;
                        break;
                    }
                }

                if (!identical)
                {
                    break;
                }

                copies++;
            }

            repeated += (copies - 1) * blockSize;
            i += copies * blockSize;
        }

        return repeated;
    }


}
