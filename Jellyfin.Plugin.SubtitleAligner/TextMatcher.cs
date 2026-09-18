using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.SubtitleAligner;

internal static class TextMatcher
{
    private const int MaximumCandidatesPerCue = 4;
    private const double MinimumVadSegmentOverlapFraction = 0.5;
    private const double MaximumAdjacentVadRegionGapSeconds = 1.25;
    private const double MaximumVadRegionWindowSeconds = 12;
    private const double MaximumAuthoredPrefixLeadSeconds = 0.75;
    private const double MaximumSourceDecoderOnsetDisagreementSeconds = 0.75;

    private static readonly HashSet<string> CommonEnglishWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "but", "by", "can", "could", "did", "do", "does",
        "for", "from", "had", "has", "have", "he", "her", "him", "his", "i", "if", "in", "is", "it", "its",
        "me", "my", "no", "not", "of", "on", "or", "our", "she", "so", "that", "the", "their", "them", "then",
        "there", "they", "this", "to", "up", "us", "was", "we", "were", "what", "when", "where", "who", "will",
        "with", "would", "you", "your"
    };

    public static TimingAnchor? Match(
        IReadOnlyList<WhisperSegment> segments,
        IReadOnlyList<SubtitleCue> allCues,
        double clipStart,
        double minimumSimilarity,
        double minimumUniqueness,
        int minimumMatchedWords)
    {
        return MatchDetailed(
            segments,
            allCues,
            clipStart,
            minimumSimilarity,
            minimumUniqueness,
            minimumMatchedWords)?.Summary;
    }

    public static DetailedTimingMatch? MatchDetailed(
        IReadOnlyList<WhisperSegment> segments,
        IReadOnlyList<SubtitleCue> allCues,
        double clipStart,
        double minimumSimilarity,
        double minimumUniqueness,
        int minimumMatchedWords)
    {
        List<TimedWord> spoken = FlattenTranscript(segments, clipStart);
        List<TimedWord> subtitle = FlattenSubtitles(allCues);
        if (spoken.Count < minimumMatchedWords || subtitle.Count < minimumMatchedWords)
        {
            return null;
        }

        Alignment best = Align(spoken, subtitle, null);
        if (best.Matches < minimumMatchedWords)
        {
            return null;
        }

        Alignment runnerUp = Align(spoken, subtitle, (best.SubtitleStart, best.SubtitleEnd));
        double runnerUpSimilarity = runnerUp.Matches >= minimumMatchedWords ? runnerUp.Similarity : 0;
        double uniqueness = best.Similarity - runnerUpSimilarity;
        if (best.Similarity < minimumSimilarity || uniqueness < minimumUniqueness)
        {
            return null;
        }

        TimingAnchor summary = CreateAnchor(best.Pairs, spoken, subtitle, best.Similarity, uniqueness);
        List<TimingAnchor> localAnchors = [];
        int localMinimumWords = Math.Clamp(minimumMatchedWords / 2, 4, 8);
        foreach (IReadOnlyList<(int Query, int Subtitle)> run in SplitContinuousRuns(best.Pairs, spoken, subtitle))
        {
            const int wordsPerAnchor = 16;
            for (int start = 0; start < run.Count; start += wordsPerAnchor)
            {
                int count = Math.Min(wordsPerAnchor, run.Count - start);
                if (count < localMinimumWords)
                {
                    continue;
                }

                localAnchors.Add(CreateAnchor(
                    run.Skip(start).Take(count).ToArray(),
                    spoken,
                    subtitle,
                    best.Similarity,
                    uniqueness));
            }
        }

        if (localAnchors.Count == 0)
        {
            localAnchors.Add(summary);
        }

        return new DetailedTimingMatch(summary, localAnchors);
    }

    public static DetailedTimingMatch? MatchGuided(
        IReadOnlyList<WhisperSegment> segments,
        IReadOnlyList<SubtitleCue> candidateCues,
        double clipStart,
        GuidedFuzzyMatchOptions options)
    {
        List<TimedWord> spoken = FlattenTranscript(segments, clipStart);
        List<TimedWord> subtitle = FlattenSubtitles(candidateCues);
        if (spoken.Count < options.MinimumDistinctiveTokens || subtitle.Count < options.MinimumDistinctiveTokens)
        {
            return null;
        }

        FuzzyAlignment best = AlignFuzzy(spoken, subtitle, null, options);
        if (best.DistinctiveMatches < options.MinimumDistinctiveTokens
            || best.Similarity < options.MinimumPhraseScore)
        {
            return null;
        }

        FuzzyAlignment runnerUp = AlignFuzzy(spoken, subtitle, (best.SubtitleStart, best.SubtitleEnd), options);
        double runnerUpSimilarity = runnerUp.DistinctiveMatches >= options.MinimumDistinctiveTokens
            ? runnerUp.Similarity
            : 0;
        double uniqueness = best.Similarity - runnerUpSimilarity;
        if (uniqueness < options.MinimumUniqueness)
        {
            return null;
        }

        (int Query, int Subtitle)[] pairs = best.Pairs
            .Select(pair => (pair.Query, pair.Subtitle))
            .ToArray();
        TimingAnchor summary = CreateAnchor(pairs, spoken, subtitle, best.Similarity, uniqueness);
        List<TimingAnchor> localAnchors = [];
        int localMinimumWords = Math.Clamp(options.MinimumDistinctiveTokens, 4, 8);
        foreach (IReadOnlyList<(int Query, int Subtitle)> run in SplitContinuousRuns(pairs, spoken, subtitle))
        {
            const int wordsPerAnchor = 16;
            for (int start = 0; start < run.Count; start += wordsPerAnchor)
            {
                int count = Math.Min(wordsPerAnchor, run.Count - start);
                if (count < localMinimumWords)
                {
                    continue;
                }

                localAnchors.Add(CreateAnchor(
                    run.Skip(start).Take(count).ToArray(),
                    spoken,
                    subtitle,
                    best.Similarity,
                    uniqueness));
            }
        }

        if (localAnchors.Count == 0)
        {
            localAnchors.Add(summary);
        }

        return new DetailedTimingMatch(summary, localAnchors);
    }

    public static IReadOnlyList<CueTimingMatch> MatchIndividualCues(
        IReadOnlyList<TranscriptSection> sections,
        IReadOnlyList<SubtitleCue> cues,
        double searchRadiusSeconds,
        double minimumSimilarity,
        double minimumUniqueness,
        bool enableFuzzyMatching,
        GuidedFuzzyMatchOptions fuzzyOptions)
    {
        if (sections
            .SelectMany(section => section.Segments)
            .Any(segment => segment.TimingGranularity == WhisperTimingGranularity.Segment))
        {
            return [];
        }

        List<TimedWord> transcript = FlattenTranscriptSections(sections);
        if (transcript.Count == 0)
        {
            return [];
        }

        searchRadiusSeconds = Math.Clamp(searchRadiusSeconds, 15, 180);
        HashSet<int> unsupportedCueIndexes = FindUnsupportedIndividualCueIndexes(cues);
        List<CueMatchCandidate> lattice = [];
        for (int cueIndex = 0; cueIndex < cues.Count; cueIndex++)
        {
            if (unsupportedCueIndexes.Contains(cueIndex))
            {
                continue;
            }

            SubtitleCue cue = cues[cueIndex];
            List<TimedWord> cueWords = FlattenSubtitles([cue]);
            int requiredMatches = RequiredCueMatches(cueWords.Count);
            if (requiredMatches == int.MaxValue)
            {
                continue;
            }

            double searchStart = Math.Max(0, cue.StartSeconds - searchRadiusSeconds);
            double searchEnd = cue.EndSeconds + searchRadiusSeconds;
            List<TimedWord> candidates = transcript
                .Where(word => word.Seconds >= searchStart && word.Seconds <= searchEnd)
                .ToList();
            if (candidates.Count < requiredMatches)
            {
                continue;
            }

            IReadOnlyList<CueMatchCandidate> cueCandidates = MatchStrictCueCandidates(
                cueIndex,
                cue,
                cueWords,
                candidates,
                requiredMatches,
                minimumSimilarity,
                minimumUniqueness,
                searchRadiusSeconds);
            if (cueCandidates.Count == 0 && enableFuzzyMatching)
            {
                cueCandidates = MatchFuzzyCueCandidates(
                    cueIndex,
                    cue,
                    cueWords,
                    candidates,
                    requiredMatches,
                    fuzzyOptions,
                    searchRadiusSeconds);
            }

            lattice.AddRange(cueCandidates);
        }

        return SelectMonotonicMatches(lattice);
    }

    public static IReadOnlyList<CueTimingMatch> MatchIndividualCuesWithVad(
        IReadOnlyList<TranscriptSection> sections,
        IReadOnlyList<SubtitleCue> cues,
        double searchRadiusSeconds,
        double minimumSimilarity,
        double minimumUniqueness,
        bool enableFuzzyMatching,
        GuidedFuzzyMatchOptions fuzzyOptions,
        double minimumCueCoverage = 0.35,
        double maximumOffsetSeconds = 2.0)
    {
        List<VadTextRegion> regions = FlattenVadTextRegions(sections);
        List<VadTextRegion> decoderRegions = FlattenDecoderTextRegions(sections);
        if (regions.Count == 0 || decoderRegions.Count == 0)
        {
            return [];
        }

        searchRadiusSeconds = Math.Clamp(searchRadiusSeconds, 15, 180);
        minimumCueCoverage = Math.Clamp(minimumCueCoverage, 0.25, 1.0);
        maximumOffsetSeconds = Math.Clamp(maximumOffsetSeconds, 0.25, 10.0);
        HashSet<int> unsupportedCueIndexes = FindUnsupportedIndividualCueIndexes(cues);
        List<CueMatchCandidate> lattice = [];
        for (int cueIndex = 0; cueIndex < cues.Count; cueIndex++)
        {
            if (unsupportedCueIndexes.Contains(cueIndex))
            {
                continue;
            }

            SubtitleCue cue = cues[cueIndex];
            List<TimedWord> cueWords = FlattenSubtitles([cue]);
            int requiredMatches = RequiredCrossLanguageCueMatches(cueWords, minimumCueCoverage);
            if (requiredMatches == int.MaxValue)
            {
                continue;
            }

            double searchStart = Math.Max(0, cue.StartSeconds - searchRadiusSeconds);
            double searchEnd = cue.EndSeconds + searchRadiusSeconds;
            VadTextRegion[] nearbyRegions = regions
                .Where(region => region.EndSeconds >= searchStart
                    && region.StartSeconds <= searchEnd
                    && Math.Abs(region.StartSeconds - cue.StartSeconds) <= maximumOffsetSeconds)
                .ToArray();
            List<CueMatchCandidate> cueCandidates = [];
            int distinctiveCueWords = cueWords.Count(word => !CommonEnglishWords.Contains(word.Text));
            int requiredDistinctive = Math.Min(
                fuzzyOptions.MinimumDistinctiveTokens,
                Math.Max(2, (int)Math.Ceiling(distinctiveCueWords * 0.6)));
            foreach (VadTextRegion[] regionWindow in EnumerateVadRegionWindows(nearbyRegions))
            {
                List<TimedWord> candidateWords = [];
                List<int> regionByWord = [];
                List<int> wordOffsetInRegion = [];
                for (int regionIndex = 0; regionIndex < regionWindow.Length; regionIndex++)
                {
                    string[] words = Words(regionWindow[regionIndex].Text);
                    for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
                    {
                        candidateWords.Add(new TimedWord(words[wordIndex], regionWindow[regionIndex].StartSeconds));
                        regionByWord.Add(regionIndex);
                        wordOffsetInRegion.Add(wordIndex);
                    }
                }

                if (candidateWords.Count < requiredMatches)
                {
                    continue;
                }

                foreach (Alignment alignment in FindStrictAlignments(cueWords, candidateWords))
                {
                    if (alignment.Matches < requiredMatches
                        || alignment.Pairs.Count(pair => !CommonEnglishWords.Contains(cueWords[pair.Query].Text)) < 2
                        || alignment.Similarity < minimumSimilarity
                        || (double)alignment.Matches / cueWords.Count < minimumCueCoverage
                        || !StartsAtVadTextRegionBeginning(alignment.Pairs, wordOffsetInRegion)
                        || !CanLocateCueAtMatchedRegionStart(
                            cueIndex,
                            cues,
                            regionWindow,
                            regionByWord,
                            alignment.Pairs))
                    {
                        continue;
                    }

                    CueTimingMatch match = CreateVadCueMatch(
                        cueIndex,
                        cue,
                        cueWords,
                        regionWindow,
                        regionByWord,
                        alignment.Pairs,
                        alignment.Similarity,
                        fuzzy: false);
                    match = ApplyNearbyAuthoredPrefixContext(
                        match,
                        cueIndex,
                        cues,
                        cueWords,
                        nearbyRegions);
                    if (HasSufficientEvidenceForDistance(match, cueWords, requiredMatches)
                        && HasCorroboratingDecoderOnset(
                            match.MediaStartSeconds,
                            cueWords,
                            decoderRegions,
                            requiredMatches,
                            minimumSimilarity,
                            minimumCueCoverage,
                            enableFuzzyMatching,
                            fuzzyOptions,
                            requiredDistinctive))
                    {
                        cueCandidates.Add(new CueMatchCandidate(
                            match,
                            CrossLanguageCandidateScore(match, maximumOffsetSeconds),
                            minimumUniqueness));
                    }
                }

                if (!enableFuzzyMatching || requiredDistinctive < 2)
                {
                    continue;
                }

                foreach (FuzzyAlignment alignment in FindFuzzyAlignments(cueWords, candidateWords, fuzzyOptions))
                {
                    (int Query, int Subtitle)[] pairs = alignment.Pairs
                        .Select(pair => (pair.Query, pair.Subtitle))
                        .ToArray();
                    if (alignment.Matches < requiredMatches
                        || alignment.DistinctiveMatches < requiredDistinctive
                        || alignment.Similarity < fuzzyOptions.MinimumPhraseScore
                        || (double)alignment.Matches / cueWords.Count < minimumCueCoverage
                        || !StartsAtVadTextRegionBeginning(pairs, wordOffsetInRegion)
                        || !CanLocateCueAtMatchedRegionStart(
                            cueIndex,
                            cues,
                            regionWindow,
                            regionByWord,
                            pairs))
                    {
                        continue;
                    }

                    CueTimingMatch match = CreateVadCueMatch(
                            cueIndex,
                            cue,
                            cueWords,
                            regionWindow,
                            regionByWord,
                            pairs,
                            alignment.Similarity,
                            fuzzy: true);
                    match = ApplyNearbyAuthoredPrefixContext(
                        match,
                        cueIndex,
                        cues,
                        cueWords,
                        nearbyRegions);
                    if (HasSufficientEvidenceForDistance(match, cueWords, requiredMatches)
                        && HasCorroboratingDecoderOnset(
                            match.MediaStartSeconds,
                            cueWords,
                            decoderRegions,
                            requiredMatches,
                            minimumSimilarity,
                            minimumCueCoverage,
                            enableFuzzyMatching,
                            fuzzyOptions,
                            requiredDistinctive))
                    {
                        cueCandidates.Add(new CueMatchCandidate(
                            match,
                            CrossLanguageCandidateScore(match, maximumOffsetSeconds),
                            fuzzyOptions.MinimumUniqueness));
                    }
                }
            }

            lattice.AddRange(OrderCueCandidates(cueCandidates));
        }

        return SelectMonotonicMatches(lattice);
    }

    private static IEnumerable<VadTextRegion[]> EnumerateVadRegionWindows(
        IReadOnlyList<VadTextRegion> regions)
    {
        for (int index = 0; index < regions.Count; index++)
        {
            yield return [regions[index]];
            if (index + 1 >= regions.Count)
            {
                continue;
            }

            VadTextRegion current = regions[index];
            VadTextRegion next = regions[index + 1];
            double gap = next.StartSeconds - current.EndSeconds;
            if (gap <= MaximumAdjacentVadRegionGapSeconds
                && next.EndSeconds - current.StartSeconds <= MaximumVadRegionWindowSeconds)
            {
                yield return [current, next];
            }
        }
    }

    private static bool StartsAtVadTextRegionBeginning(
        IReadOnlyList<(int Query, int Subtitle)> pairs,
        IReadOnlyList<int> wordOffsetInRegion)
    {
        if (pairs.Count == 0)
        {
            return false;
        }

        (int Query, int Subtitle) first = pairs
            .OrderBy(pair => pair.Query)
            .ThenBy(pair => pair.Subtitle)
            .First();
        return first.Subtitle >= 0
            && first.Subtitle < wordOffsetInRegion.Count
            && wordOffsetInRegion[first.Subtitle] == 0;
    }

    private static bool CanLocateCueAtMatchedRegionStart(
        int cueIndex,
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlyList<VadTextRegion> regions,
        IReadOnlyList<int> regionByWord,
        IReadOnlyList<(int Query, int Subtitle)> pairs)
    {
        int firstMatchedRegion = pairs.Min(pair => regionByWord[pair.Subtitle]);
        double regionStart = regions[firstMatchedRegion].StartSeconds;
        return !cues
            .Select((cue, index) => (Cue: cue, Index: index))
            .Any(item => item.Index != cueIndex
                && item.Cue.Role == SubtitleCueRole.Dialogue
                && item.Cue.StartSeconds < cues[cueIndex].StartSeconds
                && item.Cue.EndSeconds > regionStart + 0.001);
    }

    private static CueTimingMatch CreateVadCueMatch(
        int cueIndex,
        SubtitleCue cue,
        IReadOnlyList<TimedWord> cueWords,
        IReadOnlyList<VadTextRegion> regions,
        IReadOnlyList<int> regionByWord,
        IReadOnlyList<(int Query, int Subtitle)> pairs,
        double similarity,
        bool fuzzy)
    {
        int[] matchedRegionIndexes = pairs
            .Select(pair => regionByWord[pair.Subtitle])
            .Distinct()
            .Order()
            .ToArray();
        int firstRegionIndex = matchedRegionIndexes[0];
        double mediaStart = regions[firstRegionIndex].StartSeconds;
        double mediaEnd = matchedRegionIndexes.Max(index => regions[index].EndSeconds);
        return new CueTimingMatch(
            cueIndex,
            cue.StartSeconds,
            mediaStart,
            mediaStart,
            mediaEnd,
            mediaStart - cue.StartSeconds,
            similarity,
            0,
            pairs.Count,
            cueWords.Count,
            fuzzy,
            OnsetAnchored: pairs.Min(pair => pair.Query) == 0);
    }

    private static bool IsCuePrefixRegion(
        IReadOnlyList<TimedWord> cueWords,
        string regionText)
    {
        string[] regionWords = Words(regionText);
        if (regionWords.Length == 0 || regionWords.Length >= cueWords.Count)
        {
            return false;
        }

        bool exactPrefix = regionWords
            .Select((word, index) => string.Equals(word, cueWords[index].Text, StringComparison.Ordinal))
            .All(matches => matches);
        if (exactPrefix)
        {
            return true;
        }

        int cueCursor = 0;
        int matches = 0;
        int distinctiveMatches = 0;
        int cuePrefixLimit = Math.Min(cueWords.Count, Math.Max(4, regionWords.Length + 2));
        foreach (string regionWord in regionWords)
        {
            int cueIndex = Enumerable.Range(cueCursor, cuePrefixLimit - cueCursor)
                .FirstOrDefault(
                    index => string.Equals(regionWord, cueWords[index].Text, StringComparison.Ordinal),
                    -1);
            if (cueIndex < 0)
            {
                continue;
            }

            matches++;
            if (!CommonEnglishWords.Contains(regionWord))
            {
                distinctiveMatches++;
            }

            cueCursor = cueIndex + 1;
            if (cueCursor >= cuePrefixLimit)
            {
                break;
            }
        }

        return matches >= 2 && distinctiveMatches >= 1;
    }

    private static CueTimingMatch ApplyNearbyAuthoredPrefixContext(
        CueTimingMatch match,
        int cueIndex,
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlyList<TimedWord> cueWords,
        IReadOnlyList<VadTextRegion> nearbyRegions)
    {
        SubtitleCue cue = cues[cueIndex];
        VadTextRegion? previous = nearbyRegions
            .Where(region => region.EndSeconds <= match.MediaStartSeconds + 0.001)
            .OrderByDescending(region => region.EndSeconds)
            .FirstOrDefault();
        if (previous is null
            || match.MediaStartSeconds - previous.EndSeconds > MaximumAdjacentVadRegionGapSeconds
            || match.MediaEndSeconds - previous.StartSeconds > MaximumVadRegionWindowSeconds
            || Math.Abs(previous.StartSeconds - cue.StartSeconds) > MaximumAuthoredPrefixLeadSeconds
            || !IsCuePrefixRegion(cueWords, previous.Text))
        {
            return match;
        }

        bool overlapsEarlierCue = cues.Select((item, index) => (Cue: item, Index: index)).Any(item =>
            item.Index != cueIndex
            && item.Cue.Role == SubtitleCueRole.Dialogue
            && item.Cue.StartSeconds < cue.StartSeconds
            && item.Cue.EndSeconds > previous.StartSeconds + 0.001);
        if (overlapsEarlierCue)
        {
            // The decoder found a plausible opening fragment before the selected
            // segment, but that fragment overlaps an earlier authored cue. We cannot
            // safely choose either onset, so retain the semantic match as verification
            // evidence while forbidding an individual timing correction.
            return match with { OnsetAnchored = false };
        }

        return match with
        {
            MediaSeconds = previous.StartSeconds,
            MediaStartSeconds = previous.StartSeconds,
            OffsetSeconds = previous.StartSeconds - cue.StartSeconds,
            OnsetAnchored = true
        };
    }

    private static bool HasCorroboratingDecoderOnset(
        double sourceLinkedOnsetSeconds,
        IReadOnlyList<TimedWord> cueWords,
        IReadOnlyList<VadTextRegion> decoderRegions,
        int requiredMatches,
        double minimumSimilarity,
        double minimumCueCoverage,
        bool enableFuzzyMatching,
        GuidedFuzzyMatchOptions fuzzyOptions,
        int requiredDistinctive)
    {
        VadTextRegion[] nearby = decoderRegions
            .Where(region => region.EndSeconds
                    >= sourceLinkedOnsetSeconds - MaximumSourceDecoderOnsetDisagreementSeconds
                && region.StartSeconds
                    <= sourceLinkedOnsetSeconds + MaximumVadRegionWindowSeconds)
            .ToArray();
        foreach (VadTextRegion[] regionWindow in EnumerateVadRegionWindows(nearby))
        {
            List<TimedWord> candidateWords = [];
            List<int> regionByWord = [];
            List<int> wordOffsetInRegion = [];
            for (int regionIndex = 0; regionIndex < regionWindow.Length; regionIndex++)
            {
                string[] words = Words(regionWindow[regionIndex].Text);
                for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
                {
                    candidateWords.Add(new TimedWord(words[wordIndex], regionWindow[regionIndex].StartSeconds));
                    regionByWord.Add(regionIndex);
                    wordOffsetInRegion.Add(wordIndex);
                }
            }

            if (candidateWords.Count < requiredMatches)
            {
                continue;
            }

            bool strictMatch = FindStrictAlignments(cueWords, candidateWords).Any(alignment =>
                alignment.Matches >= requiredMatches
                && alignment.Pairs.Count(pair => !CommonEnglishWords.Contains(cueWords[pair.Query].Text)) >= 2
                && alignment.Similarity >= minimumSimilarity
                && (double)alignment.Matches / cueWords.Count >= minimumCueCoverage
                && StartsAtVadTextRegionBeginning(alignment.Pairs, wordOffsetInRegion)
                && DecoderWindowSupportsOnset(
                    sourceLinkedOnsetSeconds,
                    cueWords,
                    regionWindow,
                    regionByWord,
                    alignment.Pairs));
            if (strictMatch)
            {
                return true;
            }

            if (!enableFuzzyMatching || requiredDistinctive < 2)
            {
                continue;
            }

            bool fuzzyMatch = FindFuzzyAlignments(cueWords, candidateWords, fuzzyOptions).Any(alignment =>
            {
                (int Query, int Subtitle)[] pairs = alignment.Pairs
                    .Select(pair => (pair.Query, pair.Subtitle))
                    .ToArray();
                return alignment.Matches >= requiredMatches
                    && alignment.DistinctiveMatches >= requiredDistinctive
                    && alignment.Similarity >= fuzzyOptions.MinimumPhraseScore
                    && (double)alignment.Matches / cueWords.Count >= minimumCueCoverage
                    && StartsAtVadTextRegionBeginning(pairs, wordOffsetInRegion)
                    && DecoderWindowSupportsOnset(
                        sourceLinkedOnsetSeconds,
                        cueWords,
                        regionWindow,
                        regionByWord,
                        pairs);
            });
            if (fuzzyMatch)
            {
                return true;
            }
        }

        return false;
    }

    private static bool DecoderWindowSupportsOnset(
        double sourceLinkedOnsetSeconds,
        IReadOnlyList<TimedWord> cueWords,
        IReadOnlyList<VadTextRegion> regionWindow,
        IReadOnlyList<int> regionByWord,
        IReadOnlyList<(int Query, int Subtitle)> pairs)
    {
        if (Math.Abs(regionWindow[0].StartSeconds - sourceLinkedOnsetSeconds)
            > MaximumSourceDecoderOnsetDisagreementSeconds)
        {
            return false;
        }

        int firstMatchedRegion = pairs.Min(pair => regionByWord[pair.Subtitle]);
        return firstMatchedRegion == 0 || IsCuePrefixRegion(cueWords, regionWindow[0].Text);
    }

    private static List<VadTextRegion> FlattenVadTextRegions(IReadOnlyList<TranscriptSection> sections)
    {
        TranscriptSection[] ordered = sections.OrderBy(section => section.StartSeconds).ToArray();
        List<VadTextRegion> regions = [];
        for (int sectionIndex = 0; sectionIndex < ordered.Length; sectionIndex++)
        {
            TranscriptSection section = ordered[sectionIndex];
            if (section.Metadata?.SpeechIntervals is not { Count: > 0 } speechIntervals)
            {
                continue;
            }

            double leftBoundary = sectionIndex == 0
                ? double.NegativeInfinity
                : (ordered[sectionIndex - 1].StartSeconds
                    + ordered[sectionIndex - 1].DurationSeconds
                    + section.StartSeconds) / 2;
            double rightBoundary = sectionIndex == ordered.Length - 1
                ? double.PositiveInfinity
                : (section.StartSeconds
                    + section.DurationSeconds
                    + ordered[sectionIndex + 1].StartSeconds) / 2;
            foreach (WhisperSegment segment in section.Segments)
            {
                if (string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                double segmentStart = section.StartSeconds + segment.StartSeconds;
                double segmentEnd = section.StartSeconds + segment.EndSeconds;
                double midpoint = (segmentStart + segmentEnd) / 2;
                if (midpoint < leftBoundary || midpoint >= rightBoundary)
                {
                    continue;
                }

                (WhisperSpeechInterval Interval, double Overlap, double OnsetDifference)[] supportCandidates = speechIntervals
                    .Select(interval =>
                    {
                        WhisperSpeechInterval absolute = new(
                            section.StartSeconds + interval.StartSeconds,
                            section.StartSeconds + interval.EndSeconds,
                            interval.Confidence);
                        double overlap = Math.Max(
                            0,
                            Math.Min(segmentEnd, absolute.EndSeconds) - Math.Max(segmentStart, absolute.StartSeconds));
                        return (Interval: absolute, Overlap: overlap, OnsetDifference: Math.Abs(absolute.StartSeconds - segmentStart));
                    })
                    .Where(candidate => candidate.Overlap / Math.Max(0.01, segmentEnd - segmentStart)
                        >= MinimumVadSegmentOverlapFraction)
                    .OrderByDescending(candidate => candidate.Overlap)
                    .ThenBy(candidate => candidate.OnsetDifference)
                    .ToArray();
                if (supportCandidates.Length == 0)
                {
                    continue;
                }

                WhisperSpeechInterval interval = supportCandidates[0].Interval;
                regions.Add(new VadTextRegion(
                    segmentStart,
                    segmentEnd,
                    segment.Text,
                    interval.Confidence));
            }
        }

        return regions
            .OrderBy(region => region.StartSeconds)
            .ThenBy(region => region.EndSeconds)
            .ToList();
    }

    private static List<VadTextRegion> FlattenDecoderTextRegions(IReadOnlyList<TranscriptSection> sections)
    {
        TranscriptSection[] ordered = sections.OrderBy(section => section.StartSeconds).ToArray();
        List<VadTextRegion> regions = [];
        for (int sectionIndex = 0; sectionIndex < ordered.Length; sectionIndex++)
        {
            TranscriptSection section = ordered[sectionIndex];
            if (section.Metadata?.Timing?.Channels?.TryGetValue(
                    "segment",
                    out WhisperTimingChannelProvenance? segmentChannel) != true
                || segmentChannel?.Reliable != true
                || !string.Equals(segmentChannel.Basis, "decoder_segment", StringComparison.Ordinal))
            {
                continue;
            }

            double leftBoundary = sectionIndex == 0
                ? double.NegativeInfinity
                : (ordered[sectionIndex - 1].StartSeconds
                    + ordered[sectionIndex - 1].DurationSeconds
                    + section.StartSeconds) / 2;
            double rightBoundary = sectionIndex == ordered.Length - 1
                ? double.PositiveInfinity
                : (section.StartSeconds
                    + section.DurationSeconds
                    + ordered[sectionIndex + 1].StartSeconds) / 2;
            foreach (WhisperSegment segment in section.DecoderSegments)
            {
                if (string.IsNullOrWhiteSpace(segment.Text)
                    || segment.TimingGranularity != WhisperTimingGranularity.Segment)
                {
                    continue;
                }

                double segmentStart = section.StartSeconds + segment.StartSeconds;
                double segmentEnd = section.StartSeconds + segment.EndSeconds;
                double midpoint = (segmentStart + segmentEnd) / 2;
                if (midpoint < leftBoundary || midpoint >= rightBoundary)
                {
                    continue;
                }

                regions.Add(new VadTextRegion(segmentStart, segmentEnd, segment.Text, null));
            }
        }

        return regions
            .OrderBy(region => region.StartSeconds)
            .ThenBy(region => region.EndSeconds)
            .ToList();
    }

    private static HashSet<int> FindUnsupportedIndividualCueIndexes(IReadOnlyList<SubtitleCue> cues)
    {
        HashSet<int> unsupported = [];
        for (int index = 0; index < cues.Count; index++)
        {
            if (cues[index].Role != SubtitleCueRole.Dialogue || ContainsLyricMarker(cues[index].Text))
            {
                unsupported.Add(index);
            }

        }

        List<int> active = [];
        foreach (int index in Enumerable.Range(0, cues.Count)
                     .OrderBy(index => cues[index].StartSeconds)
                     .ThenBy(index => cues[index].EndSeconds))
        {
            active.RemoveAll(activeIndex =>
                cues[activeIndex].EndSeconds <= cues[index].StartSeconds + 0.001);
            if (active.Count > 0)
            {
                unsupported.Add(index);
                foreach (int activeIndex in active)
                {
                    unsupported.Add(activeIndex);
                }
            }

            active.Add(index);
        }

        return unsupported;
    }

    private static bool ContainsLyricMarker(string text) =>
        text.IndexOfAny(['\u2669', '\u266a', '\u266b', '\u266c']) >= 0;

    private static IReadOnlyList<CueMatchCandidate> MatchStrictCueCandidates(
        int cueIndex,
        SubtitleCue cue,
        IReadOnlyList<TimedWord> cueWords,
        IReadOnlyList<TimedWord> candidates,
        int requiredMatches,
        double minimumSimilarity,
        double minimumUniqueness,
        double searchRadiusSeconds)
    {
        List<CueMatchCandidate> result = [];
        foreach (Alignment alignment in FindStrictAlignments(cueWords, candidates))
        {
            if (alignment.Matches < requiredMatches || alignment.Similarity < minimumSimilarity)
            {
                continue;
            }

            CueTimingMatch match = CreateCueMatch(
                cueIndex,
                cue,
                cueWords,
                candidates,
                alignment.Pairs,
                alignment.Similarity,
                uniqueness: 0,
                fuzzy: false);
            if (HasSufficientEvidenceForDistance(match, cueWords, requiredMatches))
            {
                result.Add(new CueMatchCandidate(
                    match,
                    ContextualCandidateScore(match, cue, searchRadiusSeconds),
                    minimumUniqueness));
            }
        }

        return OrderCueCandidates(result);
    }

    private static double ContextualCandidateScore(
        CueTimingMatch match,
        SubtitleCue cue,
        double searchRadiusSeconds)
    {
        double noPenaltySeconds = Math.Max(2, cue.EndSeconds - cue.StartSeconds);
        double excessDistance = Math.Max(0, Math.Abs(match.OffsetSeconds) - noPenaltySeconds);
        // Nearby timing breaks otherwise plausible text ties. It only ranks independently
        // measured candidates and never supplies or modifies the chosen correction value.
        return match.Similarity / (1 + (excessDistance / Math.Max(1, searchRadiusSeconds)));
    }

    private static double CrossLanguageCandidateScore(
        CueTimingMatch match,
        double maximumOffsetSeconds)
    {
        double distanceFraction = Math.Clamp(
            Math.Abs(match.OffsetSeconds) / Math.Max(0.25, maximumOffsetSeconds),
            0,
            1);
        // Cross-language translations are often paraphrased. Authored timing therefore
        // provides a bounded tie-breaker inside the explicit per-cue correction limit;
        // it never creates a match or changes the measured onset.
        return match.Similarity * (1 - (0.25 * distanceFraction));
    }

    private static IReadOnlyList<CueMatchCandidate> MatchFuzzyCueCandidates(
        int cueIndex,
        SubtitleCue cue,
        IReadOnlyList<TimedWord> cueWords,
        IReadOnlyList<TimedWord> candidates,
        int requiredMatches,
        GuidedFuzzyMatchOptions options,
        double searchRadiusSeconds)
    {
        int distinctiveCueWords = cueWords.Count(word => !CommonEnglishWords.Contains(word.Text));
        int requiredDistinctive = Math.Min(
            options.MinimumDistinctiveTokens,
            Math.Max(2, (int)Math.Ceiling(distinctiveCueWords * 0.6)));
        if (requiredDistinctive < 2)
        {
            return [];
        }

        List<CueMatchCandidate> result = [];
        foreach (FuzzyAlignment alignment in FindFuzzyAlignments(cueWords, candidates, options))
        {
            if (alignment.Matches < requiredMatches
                || alignment.DistinctiveMatches < requiredDistinctive
                || alignment.Similarity < options.MinimumPhraseScore)
            {
                continue;
            }

            (int Query, int Subtitle)[] pairs = alignment.Pairs
                .Select(pair => (pair.Query, pair.Subtitle))
                .ToArray();
            CueTimingMatch match = CreateCueMatch(
                cueIndex,
                cue,
                cueWords,
                candidates,
                pairs,
                alignment.Similarity,
                uniqueness: 0,
                fuzzy: true);
            if (HasSufficientEvidenceForDistance(match, cueWords, requiredMatches))
            {
                result.Add(new CueMatchCandidate(
                    match,
                    ContextualCandidateScore(match, cue, searchRadiusSeconds),
                    options.MinimumUniqueness));
            }
        }

        return OrderCueCandidates(result);
    }

    private static IReadOnlyList<CueMatchCandidate> OrderCueCandidates(
        IEnumerable<CueMatchCandidate> candidates) => candidates
            .GroupBy(candidate => (
                candidate.Match.CueIndex,
                StartMilliseconds: Math.Round(candidate.Match.MediaStartSeconds * 1000),
                EndMilliseconds: Math.Round(candidate.Match.MediaEndSeconds * 1000)))
            .Select(group => group
                .OrderByDescending(candidate => candidate.SelectionScore)
                .ThenBy(candidate => candidate.Match.Fuzzy)
                .ThenByDescending(candidate => candidate.Match.MatchedWords)
                .First())
            .OrderByDescending(candidate => candidate.SelectionScore)
            .ThenBy(candidate => Math.Abs(candidate.Match.OffsetSeconds))
            .ThenBy(candidate => candidate.Match.MediaStartSeconds)
            .Take(MaximumCandidatesPerCue)
            .ToArray();

    private static IReadOnlyList<Alignment> FindStrictAlignments(
        IReadOnlyList<TimedWord> cueWords,
        IReadOnlyList<TimedWord> candidates)
    {
        List<(int Start, int End)> excluded = [];
        List<Alignment> alignments = [];
        for (int index = 0; index < MaximumCandidatesPerCue; index++)
        {
            Alignment alignment = AlignExcludingRanges(cueWords, candidates, excluded);
            if (alignment.Matches == 0)
            {
                break;
            }

            alignments.Add(alignment);
            excluded.Add((alignment.SubtitleStart, alignment.SubtitleEnd));
        }

        return alignments;
    }

    private static IReadOnlyList<FuzzyAlignment> FindFuzzyAlignments(
        IReadOnlyList<TimedWord> cueWords,
        IReadOnlyList<TimedWord> candidates,
        GuidedFuzzyMatchOptions options)
    {
        List<(int Start, int End)> excluded = [];
        List<FuzzyAlignment> alignments = [];
        for (int index = 0; index < MaximumCandidatesPerCue; index++)
        {
            FuzzyAlignment alignment = AlignFuzzyExcludingRanges(cueWords, candidates, excluded, options);
            if (alignment.Matches == 0)
            {
                break;
            }

            alignments.Add(alignment);
            excluded.Add((alignment.SubtitleStart, alignment.SubtitleEnd));
        }

        return alignments;
    }

    private static bool HasSufficientEvidenceForDistance(
        CueTimingMatch match,
        IReadOnlyList<TimedWord> cueWords,
        int localRequiredMatches)
    {
        double distance = Math.Abs(match.OffsetSeconds);
        int additionalMatches = distance switch
        {
            <= 5 => 0,
            <= 15 => 1,
            _ => 2
        };
        if (match.MatchedWords < localRequiredMatches + additionalMatches)
        {
            return false;
        }

        if (distance <= 10)
        {
            return true;
        }

        int distinctiveWords = cueWords.Count(word => !CommonEnglishWords.Contains(word.Text));
        return distinctiveWords >= 3;
    }

    private static CueTimingMatch CreateCueMatch(
        int cueIndex,
        SubtitleCue cue,
        IReadOnlyList<TimedWord> cueWords,
        IReadOnlyList<TimedWord> candidates,
        IReadOnlyList<(int Query, int Subtitle)> pairs,
        double similarity,
        double uniqueness,
        bool fuzzy)
    {
        double subtitleSeconds = (cue.StartSeconds + cue.EndSeconds) / 2;
        double offsetSeconds = Median(pairs.Select(pair =>
            candidates[pair.Subtitle].Seconds - cueWords[pair.Query].Seconds));
        double mediaStartSeconds = pairs.Min(pair => candidates[pair.Subtitle].Seconds);
        double mediaEndSeconds = pairs.Max(pair => candidates[pair.Subtitle].Seconds);
        return new CueTimingMatch(
            cueIndex,
            subtitleSeconds,
            subtitleSeconds + offsetSeconds,
            mediaStartSeconds,
            mediaEndSeconds,
            offsetSeconds,
            similarity,
            uniqueness,
            pairs.Count,
            cueWords.Count,
            fuzzy);
    }

    private static IReadOnlyList<CueTimingMatch> SelectMonotonicMatches(
        IReadOnlyList<CueMatchCandidate> lattice)
    {
        CueMatchCandidate[] ordered = lattice
            .OrderBy(candidate => candidate.Match.CueIndex)
            .ThenBy(candidate => candidate.Match.MediaStartSeconds)
            .ThenByDescending(candidate => candidate.SelectionScore)
            .ToArray();
        if (ordered.Length < 2)
        {
            return ordered.Length == 0
                ? []
                : [ordered[0].Match with { Uniqueness = ordered[0].SelectionScore }];
        }

        double[] scores = new double[ordered.Length];
        int[] lengths = new int[ordered.Length];
        int[] previous = Enumerable.Repeat(-1, ordered.Length).ToArray();
        for (int index = 0; index < ordered.Length; index++)
        {
            scores[index] = LatticeCandidateQuality(ordered[index]);
            lengths[index] = 1;
            for (int predecessor = 0; predecessor < index; predecessor++)
            {
                if (ordered[predecessor].Match.CueIndex >= ordered[index].Match.CueIndex
                    || ordered[predecessor].Match.MediaEndSeconds >= ordered[index].Match.MediaStartSeconds)
                {
                    continue;
                }

                double candidateScore = scores[predecessor] + LatticeCandidateQuality(ordered[index]);
                int candidateLength = lengths[predecessor] + 1;
                if (candidateScore > scores[index] + 1e-9
                    || (Math.Abs(candidateScore - scores[index]) <= 1e-9
                        && candidateLength > lengths[index]))
                {
                    scores[index] = candidateScore;
                    lengths[index] = candidateLength;
                    previous[index] = predecessor;
                }
            }
        }

        int best = 0;
        for (int index = 1; index < ordered.Length; index++)
        {
            if (scores[index] > scores[best] + 1e-9
                || (Math.Abs(scores[index] - scores[best]) <= 1e-9
                    && lengths[index] > lengths[best]))
            {
                best = index;
            }
        }

        List<CueMatchCandidate> selected = [];
        for (int index = best; index >= 0; index = previous[index])
        {
            selected.Add(ordered[index]);
            if (previous[index] < 0)
            {
                break;
            }
        }

        selected.Reverse();
        RemoveAmbiguousCandidates(selected, ordered);
        return selected
            .Select((candidate, index) => candidate.Match with
            {
                Uniqueness = CandidateSequenceMargin(candidate, index, selected, ordered)
            })
            .ToArray();
    }

    private static double LatticeCandidateQuality(CueMatchCandidate candidate) =>
        candidate.SelectionScore
        + (0.05 * candidate.Match.MatchedWords / Math.Max(1, candidate.Match.TotalWords))
        + (Math.Min(candidate.Match.MatchedWords / 8.0, 1) * 0.05)
        + (candidate.Match.Fuzzy ? 0 : 0.01);

    private static void RemoveAmbiguousCandidates(
        List<CueMatchCandidate> selected,
        IReadOnlyList<CueMatchCandidate> lattice)
    {
        bool changed;
        do
        {
            changed = false;
            for (int index = 0; index < selected.Count; index++)
            {
                CueMatchCandidate candidate = selected[index];
                double margin = CandidateSequenceMargin(candidate, index, selected, lattice);
                if (margin + 1e-9 >= candidate.MinimumUniqueness)
                {
                    continue;
                }

                selected.RemoveAt(index);
                changed = true;
                break;
            }
        }
        while (changed);
    }

    private static double CandidateSequenceMargin(
        CueMatchCandidate selected,
        int selectedIndex,
        IReadOnlyList<CueMatchCandidate> sequence,
        IReadOnlyList<CueMatchCandidate> lattice)
    {
        double previousEnd = selectedIndex == 0
            ? double.NegativeInfinity
            : sequence[selectedIndex - 1].Match.MediaEndSeconds;
        double nextStart = selectedIndex == sequence.Count - 1
            ? double.PositiveInfinity
            : sequence[selectedIndex + 1].Match.MediaStartSeconds;
        double alternativeScore = lattice
            .Where(candidate => !ReferenceEquals(candidate, selected)
                && candidate.Match.CueIndex == selected.Match.CueIndex
                && candidate.Match.MediaStartSeconds > previousEnd
                && candidate.Match.MediaEndSeconds < nextStart)
            .Select(candidate => candidate.SelectionScore)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(0, selected.SelectionScore - alternativeScore);
    }

    private static int RequiredCueMatches(int wordCount) => wordCount switch
    {
        < 3 => int.MaxValue,
        3 => 3,
        _ => Math.Max(3, (int)Math.Ceiling(wordCount * 0.6))
    };

    private static int RequiredCrossLanguageCueMatches(
        IReadOnlyList<TimedWord> cueWords,
        double minimumCueCoverage)
    {
        if (cueWords.Count < 3
            || cueWords.Count(word => !CommonEnglishWords.Contains(word.Text)) < 2)
        {
            return int.MaxValue;
        }

        return Math.Max(2, (int)Math.Ceiling(cueWords.Count * minimumCueCoverage));
    }

    private static TimingAnchor CreateAnchor(
        IReadOnlyList<(int Query, int Subtitle)> pairs,
        IReadOnlyList<TimedWord> spoken,
        IReadOnlyList<TimedWord> subtitle,
        double similarity,
        double uniqueness)
    {
        double mediaSeconds = Median(pairs.Select(pair => spoken[pair.Query].Seconds));
        double subtitleSeconds = Median(pairs.Select(pair => subtitle[pair.Subtitle].Seconds));
        double weight = similarity * similarity
            * Math.Min(pairs.Count / 12.0, 1)
            * Math.Min(uniqueness / 0.15, 1);
        return new TimingAnchor(
            mediaSeconds,
            subtitleSeconds,
            mediaSeconds - subtitleSeconds,
            similarity,
            uniqueness,
            pairs.Count,
            weight);
    }

    private static IReadOnlyList<IReadOnlyList<(int Query, int Subtitle)>> SplitContinuousRuns(
        IReadOnlyList<(int Query, int Subtitle)> pairs,
        IReadOnlyList<TimedWord> spoken,
        IReadOnlyList<TimedWord> subtitle)
    {
        List<IReadOnlyList<(int Query, int Subtitle)>> runs = [];
        List<(int Query, int Subtitle)> current = [];
        foreach ((int query, int subtitleIndex) in pairs)
        {
            if (current.Count > 0)
            {
                (int previousQuery, int previousSubtitle) = current[^1];
                if (spoken[query].Seconds - spoken[previousQuery].Seconds > 12
                    || subtitle[subtitleIndex].Seconds - subtitle[previousSubtitle].Seconds > 12)
                {
                    runs.Add(current);
                    current = [];
                }
            }

            current.Add((query, subtitleIndex));
        }

        if (current.Count > 0)
        {
            runs.Add(current);
        }

        return runs;
    }

    private static List<TimedWord> FlattenTranscript(IReadOnlyList<WhisperSegment> segments, double clipStart)
    {
        List<TimedWord> words = [];
        foreach (WhisperSegment segment in segments)
        {
            string[] normalized = Words(segment.Text);
            double relativeSeconds = segment.TimingGranularity == WhisperTimingGranularity.Segment
                ? segment.StartSeconds + (Math.Max(0, segment.EndSeconds - segment.StartSeconds) / 2)
                : segment.StartSeconds;
            foreach (string word in normalized)
            {
                words.Add(new TimedWord(word, clipStart + relativeSeconds));
            }
        }

        return words;
    }

    private static List<TimedWord> FlattenTranscriptSections(IReadOnlyList<TranscriptSection> sections)
    {
        TranscriptSection[] ordered = sections.OrderBy(section => section.StartSeconds).ToArray();
        List<TimedWord> words = [];
        for (int sectionIndex = 0; sectionIndex < ordered.Length; sectionIndex++)
        {
            TranscriptSection section = ordered[sectionIndex];
            double leftBoundary = sectionIndex == 0
                ? double.NegativeInfinity
                : (ordered[sectionIndex - 1].StartSeconds
                    + ordered[sectionIndex - 1].DurationSeconds
                    + section.StartSeconds) / 2;
            double rightBoundary = sectionIndex == ordered.Length - 1
                ? double.PositiveInfinity
                : (section.StartSeconds
                    + section.DurationSeconds
                    + ordered[sectionIndex + 1].StartSeconds) / 2;
            foreach (TimedWord word in FlattenTranscript(section.Segments, section.StartSeconds))
            {
                if (word.Seconds >= leftBoundary && word.Seconds < rightBoundary)
                {
                    words.Add(word);
                }
            }
        }

        return words.OrderBy(word => word.Seconds).ToList();
    }

    private static List<TimedWord> FlattenSubtitles(IReadOnlyList<SubtitleCue> cues)
    {
        List<TimedWord> words = [];
        foreach (SubtitleCue cue in cues)
        {
            string[] normalized = Words(cue.Text);
            for (int index = 0; index < normalized.Length; index++)
            {
                double fraction = (index + 0.5) / normalized.Length;
                double seconds = cue.StartSeconds + (fraction * Math.Max(0, cue.EndSeconds - cue.StartSeconds));
                words.Add(new TimedWord(normalized[index], seconds));
            }
        }

        return words;
    }

    private static Alignment Align(
        IReadOnlyList<TimedWord> query,
        IReadOnlyList<TimedWord> subtitle,
        (int Start, int End)? excludedSubtitleRange)
    {
        return AlignExcludingRanges(
            query,
            subtitle,
            excludedSubtitleRange is { } excluded ? [excluded] : []);
    }

    private static Alignment AlignExcludingRanges(
        IReadOnlyList<TimedWord> query,
        IReadOnlyList<TimedWord> subtitle,
        IReadOnlyList<(int Start, int End)> excludedSubtitleRanges)
    {
        int[,] scores = new int[query.Count + 1, subtitle.Count + 1];
        int bestScore = 0;
        int bestQuery = 0;
        int bestSubtitle = 0;

        for (int queryIndex = 1; queryIndex <= query.Count; queryIndex++)
        {
            for (int subtitleIndex = 1; subtitleIndex <= subtitle.Count; subtitleIndex++)
            {
                int subtitleWordIndex = subtitleIndex - 1;
                if (excludedSubtitleRanges.Any(excluded =>
                        subtitleWordIndex >= excluded.Start
                        && subtitleWordIndex <= excluded.End))
                {
                    scores[queryIndex, subtitleIndex] = 0;
                    continue;
                }

                bool same = string.Equals(
                    query[queryIndex - 1].Text,
                    subtitle[subtitleWordIndex].Text,
                    StringComparison.Ordinal);
                int diagonal = scores[queryIndex - 1, subtitleIndex - 1] + (same ? 3 : -2);
                int delete = scores[queryIndex - 1, subtitleIndex] - 2;
                int insert = scores[queryIndex, subtitleIndex - 1] - 2;
                int score = Math.Max(0, Math.Max(diagonal, Math.Max(delete, insert)));
                scores[queryIndex, subtitleIndex] = score;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestQuery = queryIndex;
                    bestSubtitle = subtitleIndex;
                }
            }
        }

        if (bestScore == 0)
        {
            return Alignment.Empty;
        }

        int queryCursor = bestQuery;
        int subtitleCursor = bestSubtitle;
        List<(int Query, int Subtitle)> pairs = [];
        while (queryCursor > 0 && subtitleCursor > 0 && scores[queryCursor, subtitleCursor] > 0)
        {
            bool same = string.Equals(
                query[queryCursor - 1].Text,
                subtitle[subtitleCursor - 1].Text,
                StringComparison.Ordinal);
            int current = scores[queryCursor, subtitleCursor];
            int diagonal = scores[queryCursor - 1, subtitleCursor - 1] + (same ? 3 : -2);
            if (current == diagonal)
            {
                if (same)
                {
                    pairs.Add((queryCursor - 1, subtitleCursor - 1));
                }

                queryCursor--;
                subtitleCursor--;
            }
            else if (current == scores[queryCursor - 1, subtitleCursor] - 2)
            {
                queryCursor--;
            }
            else
            {
                subtitleCursor--;
            }
        }

        pairs.Reverse();
        int queryStart = queryCursor;
        int queryEnd = bestQuery - 1;
        int subtitleStart = subtitleCursor;
        int subtitleEnd = bestSubtitle - 1;
        int spanWords = (queryEnd - queryStart + 1) + (subtitleEnd - subtitleStart + 1);
        double similarity = spanWords <= 0 ? 0 : (2.0 * pairs.Count) / spanWords;
        return new Alignment(queryStart, queryEnd, subtitleStart, subtitleEnd, pairs.Count, similarity, pairs);
    }

    private static FuzzyAlignment AlignFuzzy(
        IReadOnlyList<TimedWord> query,
        IReadOnlyList<TimedWord> subtitle,
        (int Start, int End)? excludedSubtitleRange,
        GuidedFuzzyMatchOptions options)
    {
        return AlignFuzzyExcludingRanges(
            query,
            subtitle,
            excludedSubtitleRange is { } excluded ? [excluded] : [],
            options);
    }

    private static FuzzyAlignment AlignFuzzyExcludingRanges(
        IReadOnlyList<TimedWord> query,
        IReadOnlyList<TimedWord> subtitle,
        IReadOnlyList<(int Start, int End)> excludedSubtitleRanges,
        GuidedFuzzyMatchOptions options)
    {
        FuzzyTimedWord[] fuzzyQuery = query.Select(ToFuzzyWord).ToArray();
        FuzzyTimedWord[] fuzzySubtitle = subtitle.Select(ToFuzzyWord).ToArray();
        double[,] scores = new double[fuzzyQuery.Length + 1, fuzzySubtitle.Length + 1];
        byte[,] directions = new byte[fuzzyQuery.Length + 1, fuzzySubtitle.Length + 1];
        double bestScore = 0;
        int bestQuery = 0;
        int bestSubtitle = 0;

        for (int queryIndex = 1; queryIndex <= fuzzyQuery.Length; queryIndex++)
        {
            for (int subtitleIndex = 1; subtitleIndex <= fuzzySubtitle.Length; subtitleIndex++)
            {
                int subtitleWordIndex = subtitleIndex - 1;
                if (excludedSubtitleRanges.Any(excluded =>
                        subtitleWordIndex >= excluded.Start
                        && subtitleWordIndex <= excluded.End))
                {
                    continue;
                }

                FuzzyTimedWord queryWord = fuzzyQuery[queryIndex - 1];
                FuzzyTimedWord subtitleWord = fuzzySubtitle[subtitleWordIndex];
                double tokenSimilarity = TokenSimilarity(queryWord, subtitleWord, options);
                double wordWeight = Math.Min(queryWord.Weight, subtitleWord.Weight);
                double diagonal = scores[queryIndex - 1, subtitleIndex - 1]
                    + (tokenSimilarity > 0 ? 3 * tokenSimilarity * wordWeight : -1.5);
                double delete = scores[queryIndex - 1, subtitleIndex] - (0.9 * queryWord.Weight);
                double insert = scores[queryIndex, subtitleIndex - 1] - (0.9 * subtitleWord.Weight);
                double score = Math.Max(0, Math.Max(diagonal, Math.Max(delete, insert)));
                scores[queryIndex, subtitleIndex] = score;
                directions[queryIndex, subtitleIndex] = score <= 0
                    ? (byte)0
                    : score == diagonal
                        ? (byte)1
                        : score == delete ? (byte)2 : (byte)3;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestQuery = queryIndex;
                    bestSubtitle = subtitleIndex;
                }
            }
        }

        if (bestScore <= 0)
        {
            return FuzzyAlignment.Empty;
        }

        int queryCursor = bestQuery;
        int subtitleCursor = bestSubtitle;
        List<FuzzyPair> pairs = [];
        while (queryCursor > 0 && subtitleCursor > 0 && scores[queryCursor, subtitleCursor] > 0)
        {
            byte direction = directions[queryCursor, subtitleCursor];
            if (direction == 1)
            {
                FuzzyTimedWord queryWord = fuzzyQuery[queryCursor - 1];
                FuzzyTimedWord subtitleWord = fuzzySubtitle[subtitleCursor - 1];
                double similarity = TokenSimilarity(queryWord, subtitleWord, options);
                if (similarity > 0)
                {
                    pairs.Add(new FuzzyPair(
                        queryCursor - 1,
                        subtitleCursor - 1,
                        similarity,
                        Math.Min(queryWord.Weight, subtitleWord.Weight)));
                }

                queryCursor--;
                subtitleCursor--;
            }
            else if (direction == 2)
            {
                queryCursor--;
            }
            else if (direction == 3)
            {
                subtitleCursor--;
            }
            else
            {
                break;
            }
        }

        pairs.Reverse();
        int queryStart = queryCursor;
        int queryEnd = bestQuery - 1;
        int subtitleStart = subtitleCursor;
        int subtitleEnd = bestSubtitle - 1;
        double queryWeight = fuzzyQuery
            .Skip(queryStart)
            .Take(Math.Max(0, queryEnd - queryStart + 1))
            .Sum(word => word.Weight);
        double subtitleWeight = fuzzySubtitle
            .Skip(subtitleStart)
            .Take(Math.Max(0, subtitleEnd - subtitleStart + 1))
            .Sum(word => word.Weight);
        double matchedWeight = pairs.Sum(pair => pair.Similarity * pair.Weight);
        double similarityScore = queryWeight + subtitleWeight <= double.Epsilon
            ? 0
            : (2 * matchedWeight) / (queryWeight + subtitleWeight);
        int distinctiveMatches = pairs.Count(pair => pair.Weight >= 0.99);
        return new FuzzyAlignment(
            queryStart,
            queryEnd,
            subtitleStart,
            subtitleEnd,
            pairs.Count,
            distinctiveMatches,
            Math.Clamp(similarityScore, 0, 1),
            pairs);
    }

    private static FuzzyTimedWord ToFuzzyWord(TimedWord word)
    {
        double weight = CommonEnglishWords.Contains(word.Text) ? 0.25 : 1.0;
        return new FuzzyTimedWord(word.Text, StemEnglish(word.Text), Soundex(word.Text), word.Seconds, weight);
    }

    private static double TokenSimilarity(
        FuzzyTimedWord first,
        FuzzyTimedWord second,
        GuidedFuzzyMatchOptions options)
    {
        if (string.Equals(first.Text, second.Text, StringComparison.Ordinal))
        {
            return 1;
        }

        double similarity = 0;
        if (options.UseStemming
            && first.Stem.Length >= 3
            && string.Equals(first.Stem, second.Stem, StringComparison.Ordinal))
        {
            similarity = 0.9;
        }

        int maximumLength = Math.Max(first.Text.Length, second.Text.Length);
        int lengthDifference = Math.Abs(first.Text.Length - second.Text.Length);
        if (maximumLength >= 4
            && 1.0 - ((double)lengthDifference / maximumLength) >= options.MinimumTokenSimilarity)
        {
            int distance = LevenshteinDistance(first.Text, second.Text);
            double spellingSimilarity = 1.0 - ((double)distance / maximumLength);
            if (spellingSimilarity >= options.MinimumTokenSimilarity)
            {
                similarity = Math.Max(similarity, spellingSimilarity);
            }
        }

        if (options.UsePhoneticMatching
            && first.Text.Length >= 4
            && second.Text.Length >= 4
            && first.Phonetic.Length > 0
            && string.Equals(first.Phonetic, second.Phonetic, StringComparison.Ordinal))
        {
            similarity = Math.Max(similarity, options.PhoneticWeight);
        }

        return similarity;
    }

    private static string StemEnglish(string word)
    {
        if (word.Length < 4)
        {
            return word;
        }

        string stem = word;
        if (stem.EndsWith("ies", StringComparison.Ordinal) && stem.Length > 4)
        {
            return stem[..^3] + "y";
        }

        if (stem.EndsWith("ing", StringComparison.Ordinal) && stem.Length > 5)
        {
            stem = stem[..^3];
        }
        else if (stem.EndsWith("ed", StringComparison.Ordinal) && stem.Length > 4)
        {
            stem = stem[..^2];
        }
        else if (stem.EndsWith("es", StringComparison.Ordinal) && stem.Length > 4)
        {
            stem = stem[..^2];
        }
        else if (stem.EndsWith('s') && stem.Length > 4)
        {
            stem = stem[..^1];
        }

        if (stem.Length >= 3 && stem[^1] == stem[^2] && !"aeiou".Contains(stem[^1]))
        {
            stem = stem[..^1];
        }

        return stem;
    }

    private static string Soundex(string word)
    {
        if (word.Length < 2)
        {
            return string.Empty;
        }

        StringBuilder result = new(4);
        result.Append(word[0]);
        char previous = SoundexCode(word[0]);
        for (int index = 1; index < word.Length && result.Length < 4; index++)
        {
            char code = SoundexCode(word[index]);
            if (code != '0' && code != previous)
            {
                result.Append(code);
            }

            previous = code;
        }

        while (result.Length < 4)
        {
            result.Append('0');
        }

        return result.ToString();
    }

    private static char SoundexCode(char value) => value switch
    {
        'b' or 'f' or 'p' or 'v' => '1',
        'c' or 'g' or 'j' or 'k' or 'q' or 's' or 'x' or 'z' => '2',
        'd' or 't' => '3',
        'l' => '4',
        'm' or 'n' => '5',
        'r' => '6',
        _ => '0'
    };

    private static int LevenshteinDistance(string first, string second)
    {
        int[] previous = Enumerable.Range(0, second.Length + 1).ToArray();
        int[] current = new int[second.Length + 1];
        for (int firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            current[0] = firstIndex;
            for (int secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                int substitution = previous[secondIndex - 1]
                    + (first[firstIndex - 1] == second[secondIndex - 1] ? 0 : 1);
                current[secondIndex] = Math.Min(
                    substitution,
                    Math.Min(previous[secondIndex] + 1, current[secondIndex - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[second.Length];
    }

    private static string[] Words(string value)
    {
        StringBuilder normalized = new(value.Length);
        foreach (char character in value.Normalize(NormalizationForm.FormKD))
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            normalized.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private sealed record TimedWord(string Text, double Seconds);

    private sealed record VadTextRegion(
        double StartSeconds,
        double EndSeconds,
        string Text,
        double? Confidence);

    private sealed record CueMatchCandidate(
        CueTimingMatch Match,
        double SelectionScore,
        double MinimumUniqueness);

    private sealed record FuzzyTimedWord(
        string Text,
        string Stem,
        string Phonetic,
        double Seconds,
        double Weight);

    private sealed record FuzzyPair(int Query, int Subtitle, double Similarity, double Weight);

    private sealed record FuzzyAlignment(
        int QueryStart,
        int QueryEnd,
        int SubtitleStart,
        int SubtitleEnd,
        int Matches,
        int DistinctiveMatches,
        double Similarity,
        IReadOnlyList<FuzzyPair> Pairs)
    {
        public static FuzzyAlignment Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, []);
    }

    private sealed record Alignment(
        int QueryStart,
        int QueryEnd,
        int SubtitleStart,
        int SubtitleEnd,
        int Matches,
        double Similarity,
        IReadOnlyList<(int Query, int Subtitle)> Pairs)
    {
        public static Alignment Empty { get; } = new(0, 0, 0, 0, 0, 0, []);
    }
}
