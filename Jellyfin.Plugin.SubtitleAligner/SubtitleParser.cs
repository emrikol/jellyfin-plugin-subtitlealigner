using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleAligner;

internal static class SubtitleParser
{
    private static readonly Regex TimestampLine = new(
        @"^(?<start>(?:\d{1,2}:)?\d{2}:\d{2}[,.]\d{2,3})\s*-->\s*(?<end>(?:\d{1,2}:)?\d{2}:\d{2}[,.]\d{2,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex HtmlTag = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex AssTag = new(
        @"\{[^}]*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex HearingImpairedAnnotation = new(
        @"\[[^\]]*\]|\([^)]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex SpeakerPrefix = new(
        @"^\s*[-–—]?\s*[A-Z][A-Z0-9 .'-]{1,30}:\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex AssStyleWord = new(
        @"[\p{L}\p{Nd}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static async Task<IReadOnlyList<SubtitleCue>> ParseAsync(string path, CancellationToken cancellationToken)
    {
        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        string extension = Path.GetExtension(path);
        return extension.Equals(".ass", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase)
            ? ParseAss(text)
            : ParseSrtOrVtt(text);
    }

    private static IReadOnlyList<SubtitleCue> ParseSrtOrVtt(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        List<SubtitleCue> cues = [];
        int sourceCueIndex = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            Match match = TimestampLine.Match(lines[index].Trim());
            if (!match.Success
                || !TryParseTime(match.Groups["start"].Value, out double start)
                || !TryParseTime(match.Groups["end"].Value, out double end))
            {
                continue;
            }

            int currentSourceCueIndex = sourceCueIndex++;

            StringBuilder cueText = new();
            while (++index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                if (cueText.Length > 0)
                {
                    cueText.Append(' ');
                }

                cueText.Append(lines[index].Trim());
            }

            string authoredText = cueText.ToString();
            string cleaned = CleanText(authoredText);
            if (cleaned.Length > 0 && end > start)
            {
                SubtitleCueRole role = ContainsLyricMarker(authoredText)
                    ? SubtitleCueRole.Lyrics
                    : SubtitleCueRole.Dialogue;
                cues.Add(new SubtitleCue(start, end, cleaned, role, currentSourceCueIndex));
            }
        }

        return cues;
    }

    private static IReadOnlyList<SubtitleCue> ParseAss(string text)
    {
        List<SubtitleCue> cues = [];
        int sourceCueIndex = 0;
        foreach (string rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] fields = line[(line.IndexOf(':') + 1)..].Split(',', 10);
            if (fields.Length < 3
                || !TryParseTime(fields[1], out double start)
                || !TryParseTime(fields[2], out double end))
            {
                continue;
            }

            int currentSourceCueIndex = sourceCueIndex++;
            if (fields.Length < 10)
            {
                continue;
            }

            string authoredText = fields[9].Replace("\\N", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("\\h", " ", StringComparison.OrdinalIgnoreCase);
            string cleaned = CleanText(authoredText);
            if (cleaned.Length > 0 && end > start)
            {
                cues.Add(new SubtitleCue(
                    start,
                    end,
                    cleaned,
                    ClassifyAssRole(fields[3], authoredText),
                    currentSourceCueIndex));
            }
        }

        return cues;
    }

    private static bool TryParseTime(string value, out double seconds)
    {
        seconds = 0;
        string normalized = value.Trim().Replace(',', '.');
        string[] parts = normalized.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double last)
            || !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
        {
            return false;
        }

        int hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
        {
            return false;
        }

        seconds = (hours * 3600) + (minutes * 60) + last;
        return seconds >= 0;
    }

    private static string CleanText(string text)
    {
        string withoutTags = AssTag.Replace(HtmlTag.Replace(text, " "), " ");
        string decoded = WebUtility.HtmlDecode(withoutTags)
            .Replace('♪', ' ')
            .Replace('♫', ' ')
            .Replace('♬', ' ');
        string dialogue = SpeakerPrefix.Replace(HearingImpairedAnnotation.Replace(decoded, " "), string.Empty);
        return string.Join(' ', dialogue.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static SubtitleCueRole ClassifyAssRole(string style, string authoredText)
    {
        string[] words = AssStyleWord.Matches(style)
            .Select(match => match.Value.ToLowerInvariant())
            .ToArray();
        if (ContainsLyricMarker(authoredText)
            || authoredText.Contains("\\k", StringComparison.OrdinalIgnoreCase)
            || words.Any(word => word is "song" or "songs" or "lyric" or "lyrics" or "karaoke" or "romaji" or "kanji" or "op" or "ed"))
        {
            return SubtitleCueRole.Lyrics;
        }

        if (words.Any(word => word is "sign" or "signs" or "screen" or "title" or "titles" or "typeset" or "typesetting"))
        {
            return SubtitleCueRole.Sign;
        }

        return SubtitleCueRole.Dialogue;
    }

    private static bool ContainsLyricMarker(string text) =>
        text.IndexOfAny(['\u2669', '\u266a', '\u266b', '\u266c']) >= 0;
}
