namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using Xunit;

public sealed class CrossLanguageVadMatcherTests
{
    [Fact]
    public void MatchIndividualCuesWithVad_UsesPairedSourceSegmentOnsetConfirmedByVad()
    {
        SubtitleCue cue = new(9.0, 11.0, "Distinctive synthetic dialogue phrase");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(
                10.4,
                12.5,
                "Distinctive synthetic dialogue phrase",
                WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(10.0, 12.6, 0.97)]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions()));

        Assert.Equal(10.4, match.MediaStartSeconds);
        Assert.Equal(1.4, match.OffsetSeconds, 6);
        Assert.False(match.Fuzzy);
    }

    [Fact]
    public void MatchIndividualCuesWithVad_UsesSourceOnsetWhenIndependentDecoderAgrees()
    {
        SubtitleCue cue = new(9.0, 11.0, "Distinctive synthetic dialogue phrase");
        WhisperSegment sourceLinked = new(
            10.0,
            12.0,
            "Distinctive synthetic dialogue phrase",
            WhisperTimingGranularity.Segment);
        WhisperSegment decoder = new(
            10.4,
            12.2,
            "Distinctive synthetic dialogue phrase",
            WhisperTimingGranularity.Segment);
        TranscriptSection section = new(
            0,
            30,
            [sourceLinked],
            Metadata([new WhisperSpeechInterval(9.9, 12.3, 0.97)]),
            [decoder]);

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions()));

        Assert.Equal(10.0, match.MediaStartSeconds);
        Assert.Equal(1.0, match.OffsetSeconds);
    }

    [Fact]
    public void MatchIndividualCuesWithVad_RejectsSourceOnsetWhenIndependentDecoderDisagrees()
    {
        SubtitleCue cue = new(10.0, 12.0, "Thank you Tsutsumi");
        WhisperSegment sourceLinked = new(
            8.0,
            10.0,
            "Thank you Tsutsumi",
            WhisperTimingGranularity.Segment);
        WhisperSegment decoder = new(
            10.1,
            11.8,
            "Thank you Tsutsumi",
            WhisperTimingGranularity.Segment);
        TranscriptSection section = new(
            0,
            30,
            [sourceLinked],
            Metadata([new WhisperSpeechInterval(7.9, 12.0, 0.97)]),
            [decoder]);

        Assert.Empty(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions(),
            maximumOffsetSeconds: 2.0));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_RejectsSourceOnsetWithoutIndependentDecoderEvidence()
    {
        SubtitleCue cue = new(10.0, 12.0, "Distinctive synthetic dialogue phrase");
        WhisperSegment sourceLinked = new(
            10.1,
            12.0,
            "Distinctive synthetic dialogue phrase",
            WhisperTimingGranularity.Segment);
        TranscriptSection section = new(
            0,
            30,
            [sourceLinked],
            Metadata([new WhisperSpeechInterval(10.0, 12.1, 0.97)]),
            []);

        Assert.Empty(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions()));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_DoesNotUseTranslatedWordsWithoutVad()
    {
        SubtitleCue cue = new(9.0, 11.0, "Distinctive synthetic dialogue phrase");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(
                10.4,
                12.5,
                "Distinctive synthetic dialogue phrase",
                WhisperTimingGranularity.Word)],
            Metadata([]));

        Assert.Empty(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions()));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_UsesDistinctPairedSegmentInsideLongVadInterval()
    {
        SubtitleCue cue = new(11.0, 12.0, "Second distinctive dialogue phrase");
        TranscriptSection section = new(
            0,
            30,
            [
                new WhisperSegment(9.0, 10.5, "First unrelated dialogue phrase", WhisperTimingGranularity.Segment),
                new WhisperSegment(11.0, 12.0, "Second distinctive dialogue phrase", WhisperTimingGranularity.Segment)
            ],
            Metadata([new WhisperSpeechInterval(8.9, 12.1, 0.97)]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions()));

        Assert.Equal(11.0, match.MediaStartSeconds);
        Assert.Equal(0.0, match.OffsetSeconds);
    }

    [Fact]
    public void MatchIndividualCuesWithVad_RejectsPairedSegmentWithoutVadSupport()
    {
        SubtitleCue cue = new(11.0, 12.0, "Second distinctive dialogue phrase");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(11.0, 12.0, "Second distinctive dialogue phrase", WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(4.0, 6.0, 0.97)]));

        Assert.Empty(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions()));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_RejectsStrictPartialPhraseBelowCueCoverage()
    {
        SubtitleCue cue = new(9.0, 11.0, "alpha bravo charlie delta echo");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(10.0, 11.0, "alpha bravo charlie", WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(10.0, 11.0, 0.97)]));

        Assert.Empty(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.75));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_AcceptsStrictPhraseAtCueCoverageBoundary()
    {
        SubtitleCue cue = new(9.0, 11.0, "alpha bravo charlie delta");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(10.0, 11.0, "alpha bravo charlie", WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(10.0, 11.0, 0.97)]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.75));

        Assert.Equal(3, match.MatchedWords);
        Assert.Equal(4, match.TotalWords);
    }

    [Fact]
    public void MatchIndividualCuesWithVad_DoesNotAnchorOnsetWhenOpeningSubtitleWordsAreUnmatched()
    {
        SubtitleCue cue = new(10.0, 14.0, "Or maybe I already am a cat");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(11.4, 13.5, "Maybe I am already a cat", WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(11.4, 13.5, 0.97)]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: true,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.35));

        Assert.False(match.OnsetAnchored);
        Assert.False(SubtitleValidationService.IsEligibleCueAdjustment(match, cue, 0.75));
    }

    [Fact]
    public void IndividualCueAdjustment_RejectsLaterStartThatLeavesTooLittleDisplayTime()
    {
        SubtitleCue cue = new(10.0, 11.25, "Let's go");
        CueTimingMatch match = new(
            0,
            10.0,
            10.85,
            10.85,
            11.25,
            0.85,
            1.0,
            0.9,
            3,
            3,
            false,
            OnsetAnchored: true);

        Assert.False(SubtitleValidationService.IsEligibleCueAdjustment(match, cue, 0.75));
    }

    [Fact]
    public void IndividualCueAdjustment_AcceptsAnchoredLaterStartWithUsableDisplayTime()
    {
        SubtitleCue cue = new(10.0, 13.0, "Distinctive dialogue phrase");
        CueTimingMatch match = new(
            0,
            10.0,
            10.85,
            10.85,
            12.0,
            0.85,
            1.0,
            0.9,
            3,
            3,
            false,
            OnsetAnchored: true);

        Assert.True(SubtitleValidationService.IsEligibleCueAdjustment(match, cue, 0.75));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_RejectsSuffixOfCombinedTranslatedRegion()
    {
        SubtitleCue cue = new(14.0, 16.0, "who are you");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(
                10.0,
                16.0,
                "I cannot stay here any longer who are you",
                WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(10.0, 16.0, 0.97)]));

        Assert.Empty(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.75));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_AcceptsDistinctivePhraseAtTranslatedRegionBeginning()
    {
        SubtitleCue cue = new(9.0, 11.0, "urgent rescue plan");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(
                10.0,
                16.0,
                "urgent rescue plan cannot wait any longer",
                WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(10.0, 16.0, 0.97)]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.75));

        Assert.Equal(10.0, match.MediaStartSeconds);
    }

    [Fact]
    public void MatchIndividualCuesWithVad_RejectsCandidateBeyondIndividualOffsetLimit()
    {
        SubtitleCue cue = new(10.0, 12.0, "distinctive emergency dialogue");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(12.25, 14.0, "distinctive emergency dialogue", WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(12.25, 14.0, 0.97)]));

        Assert.Empty(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.35,
            maximumOffsetSeconds: 2.0));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_FuzzyMatchesTokenVariantsInsideOneSpeechRegion()
    {
        SubtitleCue cue = new(10.0, 12.0, "running toward the station tonight");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(
                10.08,
                12.0,
                "runing toward the station tonite",
                WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(10.08, 12.0, 0.97)]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: true,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.75));

        Assert.True(match.Fuzzy);
        Assert.Equal(10.08, match.MediaStartSeconds);
        Assert.Equal(4, match.MatchedWords);
        Assert.Equal(5, match.TotalWords);
    }

    [Fact]
    public void MatchIndividualCuesWithVad_DoesNotStitchDistantSpeechRegionsIntoOneCue()
    {
        SubtitleCue cue = new(10.0, 15.0, "we need to leave immediately");
        TranscriptSection section = new(
            0,
            30,
            [
                new WhisperSegment(10.0, 11.0, "we need to", WhisperTimingGranularity.Segment),
                new WhisperSegment(14.0, 15.0, "leave immediately", WhisperTimingGranularity.Segment)
            ],
            Metadata(
            [
                new WhisperSpeechInterval(10.0, 11.0, 0.97),
                new WhisperSpeechInterval(14.0, 15.0, 0.97)
            ]));

        Assert.Empty(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: true,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.75));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_CanMatchAcrossAdjacentSpeechRegions()
    {
        SubtitleCue cue = new(10.0, 12.0, "we need to leave immediately");
        TranscriptSection section = new(
            0,
            30,
            [
                new WhisperSegment(10.0, 10.8, "we need to", WhisperTimingGranularity.Segment),
                new WhisperSegment(11.0, 12.0, "leave immediately", WhisperTimingGranularity.Segment)
            ],
            Metadata(
            [
                new WhisperSpeechInterval(10.0, 10.8, 0.97),
                new WhisperSpeechInterval(11.0, 12.0, 0.97)
            ]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: true,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.75));

        Assert.Equal(10.0, match.MediaStartSeconds);
        Assert.Equal(5, match.MatchedWords);
    }

    [Fact]
    public void MatchIndividualCuesWithVad_PreservesNearbyAuthoredOnsetForRepeatedPrefix()
    {
        SubtitleCue cue = new(10.0, 13.0, "I just love cats thats all");
        TranscriptSection section = new(
            0,
            30,
            [
                new WhisperSegment(10.2, 10.9, "I just", WhisperTimingGranularity.Segment),
                new WhisperSegment(11.5, 13.0, "I just love cats", WhisperTimingGranularity.Segment)
            ],
            Metadata([new WhisperSpeechInterval(10.2, 13.0, 0.97)]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: true,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.35,
            maximumOffsetSeconds: 2.0));

        Assert.Equal(10.2, match.MediaStartSeconds);
        Assert.Equal(0.2, match.OffsetSeconds, 6);
    }

    [Fact]
    public void MatchIndividualCuesWithVad_RejectsRegionOnsetInsidePreviousCue()
    {
        SubtitleCue previous = new(10.0, 12.0, "keep your distance");
        SubtitleCue current = new(12.0, 14.0, "but do not move away");
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(
                11.0,
                14.0,
                "do not get too close but do not move away",
                WhisperTimingGranularity.Segment)],
            Metadata([new WhisperSpeechInterval(11.0, 14.0, 0.97)]));

        Assert.Empty(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [previous, current],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: true,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.35,
            maximumOffsetSeconds: 2.0));
    }

    [Fact]
    public void MatchIndividualCuesWithVad_UsesNearbyParaphrasedPrefixAsOnsetContext()
    {
        SubtitleCue cue = new(10.0, 14.0, "Could you guys wait why are you leaving");
        TranscriptSection section = new(
            0,
            30,
            [
                new WhisperSegment(9.8, 11.0, "You guys stop it", WhisperTimingGranularity.Segment),
                new WhisperSegment(11.1, 13.5, "Why are you leaving", WhisperTimingGranularity.Segment)
            ],
            Metadata([new WhisperSpeechInterval(9.8, 13.5, 0.97)]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [cue],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: true,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.35,
            maximumOffsetSeconds: 2.0));

        Assert.Equal(9.8, match.MediaStartSeconds);
        Assert.Equal(-0.2, match.OffsetSeconds, 6);
    }

    [Fact]
    public void MatchIndividualCuesWithVad_DoesNotBorrowPrefixInsidePreviousCue()
    {
        SubtitleCue previous = new(8.5, 10.0, "previous spoken dialogue");
        SubtitleCue current = new(10.0, 13.0, "Could you guys wait why are you leaving");
        TranscriptSection section = new(
            0,
            30,
            [
                new WhisperSegment(9.8, 10.1, "You guys stop it", WhisperTimingGranularity.Segment),
                new WhisperSegment(10.5, 12.5, "Why are you leaving", WhisperTimingGranularity.Segment)
            ],
            Metadata([new WhisperSpeechInterval(9.8, 12.5, 0.97)]));

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCuesWithVad(
            [section],
            [previous, current],
            searchRadiusSeconds: 15,
            minimumSimilarity: 0.6,
            minimumUniqueness: 0.01,
            enableFuzzyMatching: true,
            DefaultFuzzyOptions(),
            minimumCueCoverage: 0.35,
            maximumOffsetSeconds: 2.0));

        Assert.Equal(1, match.CueIndex);
        Assert.Equal(10.5, match.MediaStartSeconds);
        Assert.Equal(0.5, match.OffsetSeconds, 6);
        Assert.False(match.OnsetAnchored);
    }

    [Fact]
    public void ClassifyVadTimingConfidence_IsCappedAtMedium()
    {
        Assert.Equal(
            "Medium",
            SubtitleValidationService.ClassifyVadTimingConfidence(
                [Metadata([new WhisperSpeechInterval(10.0, 12.6, 0.97)])]));
        Assert.Equal(
            "Inconclusive",
            SubtitleValidationService.ClassifyVadTimingConfidence([Metadata([])]));
    }

    [Theory]
    [InlineData("decoder_segment", true)]
    [InlineData("vad", false)]
    public void ClassifyVadTimingConfidence_RejectsIneligibleVadProvenance(
        string basis,
        bool reliable)
    {
        Assert.Equal(
            "Inconclusive",
            SubtitleValidationService.ClassifyVadTimingConfidence(
                [Metadata([new WhisperSpeechInterval(10.0, 12.6, 0.97)], basis, reliable)]));
    }

    [Theory]
    [InlineData("source_segment", true)]
    [InlineData("decoder_segment", false)]
    public void ClassifyVadTimingConfidence_RejectsIneligibleDecoderSegmentProvenance(
        string basis,
        bool reliable)
    {
        Assert.Equal(
            "Inconclusive",
            SubtitleValidationService.ClassifyVadTimingConfidence(
                [Metadata(
                    [new WhisperSpeechInterval(10.0, 12.6, 0.97)],
                    segmentBasis: basis,
                    segmentReliable: reliable)]));
    }

    private static GuidedFuzzyMatchOptions DefaultFuzzyOptions() => new(
        0.55,
        0.04,
        3,
        0.76,
        true,
        true,
        0.3);

    private static WhisperResponseMetadata Metadata(
        IReadOnlyList<WhisperSpeechInterval> speechIntervals,
        string vadBasis = "vad",
        bool vadReliable = true,
        string segmentBasis = "decoder_segment",
        bool segmentReliable = true) => new(
        true,
        "1.0",
        "synthetic-request",
        "translate",
        "auto",
        "synthetic-model",
        "synthetic-backend",
        "1.0",
        "ja",
        0.99,
        "synthetic-classifier",
        "ja",
        30,
        0.1,
        0.2,
        0.01,
        true,
        false,
        1,
        new WhisperTimingProvenance(
            ["segment", "vad"],
            ["segment", "vad"],
            "decoder_segment",
            true,
            "decoder-provided",
            new Dictionary<string, WhisperTimingChannelProvenance>(StringComparer.Ordinal)
            {
                ["segment"] = new(segmentBasis, segmentReliable, "decoder-provided"),
                ["vad"] = new(vadBasis, vadReliable, "synthetic-vad")
            }),
        [],
        speechIntervals);
}
