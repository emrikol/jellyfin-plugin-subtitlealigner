using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Writes corrected subtitle copies while preserving all non-timing content.</summary>
internal static partial class SubtitleCorrector
{
    internal const double LocalAlignmentSupportSeconds = 90;

    [GeneratedRegex(
        @"(?m)^(?<prefix>\s*)(?<start>(?:\d{1,3}:)?\d{2}:\d{2}[,.]\d{1,3})(?<arrow>\s*-->\s*)(?<end>(?:\d{1,3}:)?\d{2}:\d{2}[,.]\d{1,3})(?<suffix>[^\r\n]*)(?=\r?$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TextCueRegex();

    [GeneratedRegex(
        @"(?im)^(?<prefix>\s*Dialogue:\s*[^,\r\n]*,)(?<start>\d{1,3}:\d{2}:\d{2}[,.]\d{1,3}),(?<end>\d{1,3}:\d{2}:\d{2}[,.]\d{1,3})(?<suffix>,[^\r\n]*)(?=\r?$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex AssCueRegex();

    [GeneratedRegex(
        @"^(?:(?<h>\d{1,3}):)?(?<m>\d{2}):(?<s>\d{2})(?<sep>[,.])(?<f>\d{1,3})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    public static async Task WriteCorrectedCopyAsync(
        string sourcePath,
        string destinationPath,
        ValidationRecord result,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string source = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        string extension = Path.GetExtension(sourcePath);
        Regex cueRegex = extension.Equals(".ass", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase)
            ? AssCueRegex()
            : TextCueRegex();
        IReadOnlyDictionary<int, SubtitleCueTimingPoint>? directPointsBySourceIndex =
            string.Equals(result.CueAdjustmentStrategy, "Direct", StringComparison.Ordinal)
            && result.CueTimingPoints.Any(point => point.SourceCueIndex.HasValue)
                ? result.CueTimingPoints
                    .Where(point => point.SourceCueIndex.HasValue)
                    .ToDictionary(point => point.SourceCueIndex!.Value)
                : null;
        int changed = 0;
        int cueIndex = 0;
        string corrected = cueRegex.Replace(source, match =>
        {
            if (!TryParseTimestamp(match.Groups["start"].Value, out double start)
                || !TryParseTimestamp(match.Groups["end"].Value, out double end))
            {
                return match.Value;
            }

            (double correctedStart, double correctedEnd) = CorrectCue(
                start,
                end,
                cueIndex,
                result,
                directPointsBySourceIndex);
            cueIndex++;
            changed++;
            string separator = match.Groups["arrow"].Success ? match.Groups["arrow"].Value : ",";
            return match.Groups["prefix"].Value
                + FormatTimestamp(correctedStart, match.Groups["start"].Value)
                + separator
                + FormatTimestamp(correctedEnd, match.Groups["end"].Value)
                + match.Groups["suffix"].Value;
        });

        if (changed == 0)
        {
            throw new InvalidDataException($"No supported timing lines were found in {Path.GetFileName(sourcePath)}.");
        }

        string directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The corrected subtitle path has no parent directory.");
        Directory.CreateDirectory(directory);
        FileMode mode = overwrite ? FileMode.Truncate : FileMode.CreateNew;
        await using FileStream output = new(
            destinationPath,
            mode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using StreamWriter writer = new(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(corrected.AsMemory(), CancellationToken.None).ConfigureAwait(false);
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static (double Start, double End) CorrectCue(
        double start,
        double end,
        int cueIndex,
        ValidationRecord result,
        IReadOnlyDictionary<int, SubtitleCueTimingPoint>? directPointsBySourceIndex)
    {
        if (result.Status == "Locally aligned")
        {
            double midpoint = (start + end) / 2;
            if (string.Equals(result.CueAdjustmentStrategy, "Direct", StringComparison.Ordinal))
            {
                SubtitleCueTimingPoint? directMatch;
                if (directPointsBySourceIndex is not null)
                {
                    if (!directPointsBySourceIndex.TryGetValue(cueIndex, out directMatch))
                    {
                        return (start, end);
                    }
                }
                else if (cueIndex < result.CueTimingPoints.Count)
                {
                    // Compatibility for results created before source cue indexes were recorded.
                    directMatch = result.CueTimingPoints[cueIndex];
                }
                else
                {
                    return (start, end);
                }

                if (!directMatch.Adjusted || directMatch.OffsetSeconds is not double directOffset)
                {
                    return (start, end);
                }

                double correctedStart = Math.Max(0, start + directOffset);
                return (Math.Min(correctedStart, Math.Max(0, end - 0.01)), end);
            }

            double supportSeconds = result.LocalAlignmentSupportSeconds > 0
                ? result.LocalAlignmentSupportSeconds
                : LocalAlignmentSupportSeconds;
            return TryGetLocalOffset(result.TimingPoints, midpoint, out double localOffset, supportSeconds)
                ? (Math.Max(0, start + localOffset), Math.Max(0, end + localOffset))
                : (start, end);
        }

        if (result.Status == "Progressive drift"
            && result.LinearSlope is double slope
            && result.LinearInterceptSeconds is double intercept
            && Math.Abs(1 - slope) > 0.000001)
        {
            return (Math.Max(0, (start + intercept) / (1 - slope)), Math.Max(0, (end + intercept) / (1 - slope)));
        }

        double offset;
        if (result.Status == "Timing break"
            && result.BreakSeconds is double split
            && result.BeforeBreakOffsetSeconds is double before
            && result.AfterBreakOffsetSeconds is double after)
        {
            double midpoint = (start + end) / 2;
            offset = midpoint + before < split ? before : after;
        }
        else
        {
            offset = result.OffsetSeconds ?? 0;
        }

        return (Math.Max(0, start + offset), Math.Max(0, end + offset));
    }

    internal static bool TryGetLocalOffset(
        IReadOnlyList<LocalTimingPoint> points,
        double subtitleSeconds,
        out double offsetSeconds,
        double supportSeconds = LocalAlignmentSupportSeconds)
    {
        offsetSeconds = 0;
        if (points.Count == 0)
        {
            return false;
        }

        supportSeconds = Math.Clamp(supportSeconds, 15, 300);
        LocalTimingPoint[] ordered = points.OrderBy(point => point.SubtitleSeconds).ToArray();
        int upperIndex = Array.FindIndex(ordered, point => point.SubtitleSeconds >= subtitleSeconds);
        if (upperIndex <= 0)
        {
            LocalTimingPoint nearest = upperIndex == 0 ? ordered[0] : ordered[^1];
            if (Math.Abs(nearest.SubtitleSeconds - subtitleSeconds) > supportSeconds)
            {
                return false;
            }

            offsetSeconds = nearest.OffsetSeconds;
            return true;
        }

        LocalTimingPoint upper = ordered[upperIndex];
        LocalTimingPoint lower = ordered[upperIndex - 1];
        double lowerDistance = subtitleSeconds - lower.SubtitleSeconds;
        double upperDistance = upper.SubtitleSeconds - subtitleSeconds;
        if (Math.Min(lowerDistance, upperDistance) > supportSeconds)
        {
            return false;
        }

        double span = upper.SubtitleSeconds - lower.SubtitleSeconds;
        double fraction = span <= double.Epsilon ? 0 : lowerDistance / span;
        offsetSeconds = lower.OffsetSeconds + ((upper.OffsetSeconds - lower.OffsetSeconds) * fraction);
        return true;
    }

    private static bool TryParseTimestamp(string value, out double seconds)
    {
        seconds = 0;
        Match match = TimestampRegex().Match(value);
        if (!match.Success
            || !int.TryParse(match.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || !int.TryParse(match.Groups["s"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int wholeSeconds)
            || !int.TryParse(match.Groups["f"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int fraction))
        {
            return false;
        }

        int hours = match.Groups["h"].Success
            && int.TryParse(match.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedHours)
            ? parsedHours
            : 0;
        seconds = (hours * 3600) + (minutes * 60) + wholeSeconds
            + (fraction / Math.Pow(10, match.Groups["f"].Value.Length));
        return true;
    }

    private static string FormatTimestamp(double seconds, string template)
    {
        Match match = TimestampRegex().Match(template);
        if (!match.Success)
        {
            return template;
        }

        int fractionDigits = match.Groups["f"].Value.Length;
        long scale = (long)Math.Pow(10, fractionDigits);
        long totalUnits = Math.Max(0, (long)Math.Round(seconds * scale, MidpointRounding.AwayFromZero));
        long unitsPerHour = 3600 * scale;
        long unitsPerMinute = 60 * scale;
        long hours = totalUnits / unitsPerHour;
        long remainder = totalUnits % unitsPerHour;
        long minutes = remainder / unitsPerMinute;
        remainder %= unitsPerMinute;
        long wholeSeconds = remainder / scale;
        long fraction = remainder % scale;
        bool includeHours = match.Groups["h"].Success || hours > 0;
        int hourDigits = Math.Max(match.Groups["h"].Value.Length, hours.ToString(CultureInfo.InvariantCulture).Length);
        string hourPrefix = includeHours
            ? hours.ToString(CultureInfo.InvariantCulture).PadLeft(hourDigits, '0') + ":"
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hourPrefix}{minutes:00}:{wholeSeconds:00}{match.Groups["sep"].Value}{fraction.ToString(CultureInfo.InvariantCulture).PadLeft(fractionDigits, '0')}");
    }
}
