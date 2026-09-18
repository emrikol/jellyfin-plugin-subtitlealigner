using System.Numerics;

namespace Jellyfin.Plugin.SubtitleAligner;

internal static class SpeechActivityMatcher
{
    private const double DefaultGridSeconds = 0.01;
    private const double CompetingPeakSeparationSeconds = 0.25;
    private const int MinimumActiveTicks = 10;
    private const int MaximumGridTicks = 4_000_000;

    internal static SpeechActivityAlignment? Match(
        IReadOnlyList<WhisperSpeechInterval> speechIntervals,
        IReadOnlyList<SubtitleCue> subtitleCues,
        double windowStartSeconds,
        double windowEndSeconds,
        double maximumOffsetSeconds,
        IReadOnlyList<double>? rateRatios = null,
        double gridSeconds = DefaultGridSeconds)
    {
        if (!double.IsFinite(windowStartSeconds)
            || !double.IsFinite(windowEndSeconds)
            || !double.IsFinite(maximumOffsetSeconds)
            || !double.IsFinite(gridSeconds)
            || windowEndSeconds <= windowStartSeconds
            || maximumOffsetSeconds < 0
            || gridSeconds is < 0.005 or > 0.10)
        {
            return null;
        }

        double requestedTicks = Math.Ceiling((windowEndSeconds - windowStartSeconds) / gridSeconds);
        if (!double.IsFinite(requestedTicks)
            || requestedTicks < MinimumActiveTicks * 2
            || requestedTicks > MaximumGridTicks)
        {
            return null;
        }

        int tickCount = (int)requestedTicks;

        ulong[] speech = BuildSignal(
            speechIntervals.Select(interval => (interval.StartSeconds, interval.EndSeconds)),
            windowStartSeconds,
            gridSeconds,
            tickCount);
        int speechTicks = CountBits(speech, tickCount);
        if (speechTicks < MinimumActiveTicks || tickCount - speechTicks < MinimumActiveTicks)
        {
            return null;
        }

        double[] ratios = (rateRatios ?? [1.0])
            .Where(ratio => double.IsFinite(ratio) && ratio is >= 0.95 and <= 1.05)
            .Distinct()
            .OrderBy(ratio => Math.Abs(ratio - 1))
            .ThenBy(ratio => ratio)
            .ToArray();
        if (ratios.Length == 0)
        {
            return null;
        }

        int maximumOffsetTicks = (int)Math.Min(
            tickCount - 1,
            Math.Round(maximumOffsetSeconds / gridSeconds, MidpointRounding.AwayFromZero));
        List<Candidate> candidates = [];
        foreach (double ratio in ratios)
        {
            ulong[] subtitle = BuildSignal(
                subtitleCues
                    .Where(cue => cue.Role == SubtitleCueRole.Dialogue)
                    .Select(cue => (cue.StartSeconds * ratio, cue.EndSeconds * ratio)),
                windowStartSeconds,
                gridSeconds,
                tickCount);
            if (CountBits(subtitle, tickCount) < MinimumActiveTicks)
            {
                continue;
            }

            for (int lagTicks = -maximumOffsetTicks; lagTicks <= maximumOffsetTicks; lagTicks++)
            {
                (double correlation, int subtitleTicks) = CorrelateBinarySignals(
                    speech,
                    speechTicks,
                    subtitle,
                    lagTicks,
                    tickCount);
                if (double.IsFinite(correlation))
                {
                    candidates.Add(new Candidate(
                        lagTicks * gridSeconds,
                        ratio,
                        correlation,
                        subtitleTicks));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        Candidate best = candidates
            .OrderByDescending(candidate => candidate.Correlation)
            .ThenBy(candidate => Math.Abs(candidate.RateRatio - 1))
            .ThenBy(candidate => Math.Abs(candidate.OffsetSeconds))
            .ThenBy(candidate => candidate.OffsetSeconds)
            .First();
        double runnerUp = candidates
            .Where(candidate => candidate.RateRatio != best.RateRatio
                || Math.Abs(candidate.OffsetSeconds - best.OffsetSeconds) >= CompetingPeakSeparationSeconds)
            .Select(candidate => candidate.Correlation)
            .DefaultIfEmpty(-1)
            .Max();
        return new SpeechActivityAlignment(
            best.OffsetSeconds,
            best.RateRatio,
            best.Correlation,
            Math.Max(0, best.Correlation - runnerUp),
            speechTicks * gridSeconds,
            best.SubtitleTicks * gridSeconds,
            gridSeconds);
    }

    private static ulong[] BuildSignal(
        IEnumerable<(double Start, double End)> intervals,
        double windowStartSeconds,
        double gridSeconds,
        int tickCount)
    {
        ulong[] words = new ulong[(tickCount + 63) / 64];
        foreach ((double start, double end) in intervals)
        {
            if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
            {
                continue;
            }

            int first = BoundedTick(
                Math.Floor((start - windowStartSeconds) / gridSeconds),
                tickCount);
            int afterLast = BoundedTick(
                Math.Ceiling((end - windowStartSeconds) / gridSeconds),
                tickCount);
            for (int tick = first; tick < afterLast; tick++)
            {
                words[tick / 64] |= 1UL << (tick % 64);
            }
        }

        return words;
    }

    private static int BoundedTick(double value, int tickCount) => value switch
    {
        <= 0 => 0,
        _ when value >= tickCount => tickCount,
        _ => (int)value
    };

    private static (double Correlation, int SubtitleTicks) CorrelateBinarySignals(
        IReadOnlyList<ulong> speech,
        int speechTicks,
        IReadOnlyList<ulong> subtitle,
        int lagTicks,
        int tickCount)
    {
        int subtitleTicks = 0;
        int sharedTicks = 0;
        int wordCount = speech.Count;
        for (int wordIndex = 0; wordIndex < wordCount; wordIndex++)
        {
            ulong mask = wordIndex == wordCount - 1 && tickCount % 64 != 0
                ? (1UL << (tickCount % 64)) - 1
                : ulong.MaxValue;
            ulong shiftedSubtitle = ReadShiftedWord(subtitle, wordIndex, lagTicks) & mask;
            subtitleTicks += BitOperations.PopCount(shiftedSubtitle);
            sharedTicks += BitOperations.PopCount(speech[wordIndex] & shiftedSubtitle);
        }

        if (subtitleTicks < MinimumActiveTicks || tickCount - subtitleTicks < MinimumActiveTicks)
        {
            return (double.NaN, subtitleTicks);
        }

        double numerator = ((double)tickCount * sharedTicks) - ((double)speechTicks * subtitleTicks);
        double denominator = Math.Sqrt(
            (double)speechTicks
            * (tickCount - speechTicks)
            * subtitleTicks
            * (tickCount - subtitleTicks));
        return denominator <= double.Epsilon
            ? (double.NaN, subtitleTicks)
            : (numerator / denominator, subtitleTicks);
    }

    private static ulong ReadShiftedWord(
        IReadOnlyList<ulong> source,
        int destinationWordIndex,
        int lagTicks)
    {
        int sourceStart = (destinationWordIndex * 64) - lagTicks;
        int sourceWordIndex = sourceStart >= 0
            ? sourceStart / 64
            : -((-sourceStart + 63) / 64);
        int bitOffset = sourceStart - (sourceWordIndex * 64);
        ulong lower = sourceWordIndex >= 0 && sourceWordIndex < source.Count
            ? source[sourceWordIndex] >> bitOffset
            : 0;
        if (bitOffset == 0)
        {
            return lower;
        }

        ulong upper = sourceWordIndex + 1 >= 0 && sourceWordIndex + 1 < source.Count
            ? source[sourceWordIndex + 1] << (64 - bitOffset)
            : 0;
        return lower | upper;
    }

    private static int CountBits(IReadOnlyList<ulong> words, int tickCount)
    {
        int count = 0;
        for (int index = 0; index < words.Count; index++)
        {
            ulong word = words[index];
            if (index == words.Count - 1 && tickCount % 64 != 0)
            {
                word &= (1UL << (tickCount % 64)) - 1;
            }

            count += BitOperations.PopCount(word);
        }

        return count;
    }

    private sealed record Candidate(
        double OffsetSeconds,
        double RateRatio,
        double Correlation,
        int SubtitleTicks);
}

internal sealed record SpeechActivityAlignment(
    double OffsetSeconds,
    double RateRatio,
    double Correlation,
    double Uniqueness,
    double SpeechActivitySeconds,
    double SubtitleActivitySeconds,
    double GridSeconds);
