namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using Xunit;

public sealed class TextMatcherRegressionTests
{
    [Theory]
    [InlineData(-5.0)]
    [InlineData(-2.0)]
    [InlineData(-1.0)]
    [InlineData(-0.5)]
    [InlineData(-0.25)]
    [InlineData(-0.10)]
    [InlineData(0.10)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void MatchIndividualCues_RecoversKnownWordTimedShift(double shiftSeconds)
    {
        SubtitleCue cue = new(100, 104, "distinctive synthetic dialogue phrase");
        TranscriptSection section = ExactWordSection(
            ["distinctive", "synthetic", "dialogue", "phrase"],
            100.5 + shiftSeconds);

        CueTimingMatch match = Assert.Single(Match([section], [cue]));

        Assert.Equal(shiftSeconds, match.OffsetSeconds, precision: 6);
    }

    [Fact]
    public void MatchIndividualCues_LeavesOneWordCueUnmatched()
    {
        SubtitleCue cue = new(100, 101, "Run!");
        TranscriptSection section = ExactWordSection(["run"], 100.5);

        Assert.Empty(Match([section], [cue]));
    }

    [Fact]
    public void MatchIndividualCues_LeavesShortNonSpeechCaptionUnchanged()
    {
        SubtitleCue cue = new(100, 102, "[door closes]");
        TranscriptSection section = ExactWordSection(["door", "closes"], 130.5);

        Assert.Empty(Match([section], [cue]));
    }

    [Fact]
    public void MatchIndividualCues_LeavesLyricMarkedCueUnchanged()
    {
        SubtitleCue cue = new(100, 106, "♪ chromatic fireflies circle midnight cedar branches ♪");
        TranscriptSection section = ExactWordSection(
            ["chromatic", "fireflies", "circle", "midnight", "cedar", "branches"],
            100.5);

        Assert.Empty(Match([section], [cue]));
    }

    [Fact]
    public void MatchIndividualCues_LeavesAuthoredOverlappingDialogueUnchanged()
    {
        SubtitleCue firstCue = new(
            100,
            106,
            "patient astronomers chart luminous winter constellations");
        SubtitleCue secondCue = new(
            104,
            110,
            "quiet otters gather polished river stones");
        TranscriptSection firstSpeech = ExactWordSection(
            ["patient", "astronomers", "chart", "luminous", "winter", "constellations"],
            100.5);
        TranscriptSection secondSpeech = ExactWordSection(
            ["quiet", "otters", "gather", "polished", "river", "stones"],
            104.5);
        TranscriptSection combined = new(
            0,
            180,
            firstSpeech.Segments
                .Concat(secondSpeech.Segments)
                .OrderBy(segment => segment.StartSeconds)
                .ToArray());

        Assert.Empty(Match([combined], [firstCue, secondCue]));
    }

    [Theory]
    [InlineData("Keep going")]
    [InlineData("Good job")]
    [InlineData("Thank you")]
    public void MatchIndividualCues_DoesNotMoveShortCommonPhraseToDistantOccurrence(string text)
    {
        string[] words = text.ToLowerInvariant().Split(' ');
        SubtitleCue cue = new(100, 102, text);
        TranscriptSection section = ExactWordSection(words, 130.5);

        Assert.Empty(Match([section], [cue]));
    }

    [Fact]
    public void MatchIndividualCues_RejectsDuplicatedExactPhrase()
    {
        string[] words = ["unique", "looking", "phrase", "appears"];
        SubtitleCue cue = new(100, 104, string.Join(' ', words));
        TranscriptSection first = ExactWordSection(words, 80.5);
        TranscriptSection second = ExactWordSection(words, 120.5);

        Assert.Empty(Match([first, second], [cue]));
    }

    [Fact]
    public void MatchIndividualCues_PrefersNearbyStrongParaphraseOverDistantExactRepetition()
    {
        SubtitleCue cue = new(100, 106, "the astronomer studies luminous galaxies tonight");
        TranscriptSection nearbyParaphrase = ExactWordSection(
            ["the", "astronomer", "studies", "luminous", "galaxy", "tonight"],
            100.5);
        TranscriptSection distantExact = ExactWordSection(
            ["the", "astronomer", "studies", "luminous", "galaxies", "tonight"],
            130.5);

        TranscriptSection combined = new(
            0,
            180,
            nearbyParaphrase.Segments.Concat(distantExact.Segments).ToArray());

        CueTimingMatch match = Assert.Single(Match([combined], [cue]));

        Assert.Equal(0, match.OffsetSeconds, precision: 6);
        Assert.True(match.Similarity < 1);
    }

    [Fact]
    public void MatchIndividualCues_MapsContractionToNearbyExpandedUtteranceNotLaterExactRepetition()
    {
        SubtitleCue cue = new(100, 104, "What's going on?");
        TranscriptSection nearbyExpanded = ExactWordSection(
            ["what", "is", "going", "on"],
            100.5);
        TranscriptSection laterExact = ExactWordSection(
            ["what", "s", "going", "on"],
            130.5);
        TranscriptSection combined = new(
            0,
            180,
            nearbyExpanded.Segments.Concat(laterExact.Segments).ToArray());

        CueTimingMatch match = Assert.Single(Match([combined], [cue]));

        Assert.Equal(0, match.OffsetSeconds, precision: 6);
        Assert.True(match.MediaSeconds < 105);
    }

    [Fact]
    public void MatchIndividualCues_RecoversDistinctiveLongPhraseAtLargeOffset()
    {
        string[] words = ["chromatic", "fireflies", "circle", "midnight", "cedar", "branches"];
        SubtitleCue cue = new(100, 106, string.Join(' ', words));
        TranscriptSection section = ExactWordSection(words, 130.5);

        CueTimingMatch match = Assert.Single(Match([section], [cue]));

        Assert.Equal(30, match.OffsetSeconds, precision: 6);
    }

    [Fact]
    public void MatchIndividualCues_RequiresMoreMatchedWordsForLargeCorrection()
    {
        string[] words = ["crimson", "satellites", "orbit", "quietly"];
        SubtitleCue cue = new(100, 104, string.Join(' ', words));
        TranscriptSection section = ExactWordSection(words, 130.5);

        Assert.Empty(Match([section], [cue]));
    }

    [Fact]
    public void MatchIndividualCues_RejectsLargeCorrectionForNondistinctivePhrase()
    {
        string[] words = ["what", "are", "you", "going", "to", "do"];
        SubtitleCue cue = new(100, 106, string.Join(' ', words));
        TranscriptSection section = ExactWordSection(words, 130.5);

        Assert.Empty(Match([section], [cue]));
    }

    [Fact]
    public void MatchIndividualCues_ProducesDeterministicDecisions()
    {
        SubtitleCue cue = new(100, 104, "distinctive synthetic dialogue phrase");
        TranscriptSection section = ExactWordSection(
            ["distinctive", "synthetic", "dialogue", "phrase"],
            101.0);

        CueTimingMatch[] first = Match([section], [cue]).ToArray();
        CueTimingMatch[] second = Match([section], [cue]).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void MatchIndividualCues_DoesNotReturnCrossedMatches()
    {
        SubtitleCue firstCue = new(100, 104, "first distinctive dialogue phrase");
        SubtitleCue secondCue = new(110, 114, "second unusual spoken sentence");
        TranscriptSection secondSpokenFirst = ExactWordSection(
            ["second", "unusual", "spoken", "sentence"],
            90.5);
        TranscriptSection firstSpokenSecond = ExactWordSection(
            ["first", "distinctive", "dialogue", "phrase"],
            120.5);

        CueTimingMatch[] matches = Match(
            [secondSpokenFirst, firstSpokenSecond],
            [firstCue, secondCue]).ToArray();

        Assert.True(matches.Length < 2);
        Assert.True(matches.Zip(matches.Skip(1)).All(pair => pair.First.MediaSeconds < pair.Second.MediaSeconds));
    }

    [Fact]
    public void MatchIndividualCues_GlobalMonotonicSelectionCanSkipOneMatchToKeepTwo()
    {
        SubtitleCue crossingCue = new(
            100,
            106,
            "chromatic fireflies circle midnight cedar branches");
        SubtitleCue firstOrderedCue = new(
            110,
            114,
            "patient otters gather polished stones");
        SubtitleCue secondOrderedCue = new(
            120,
            124,
            "winter lanterns illuminate quiet pathways");
        TranscriptSection crossingSpeech = ExactWordSection(
            ["chromatic", "fireflies", "circle", "midnight", "cedar", "branches"],
            120.5);
        TranscriptSection firstOrderedSpeech = ExactWordSection(
            ["patient", "otters", "gather", "polished", "stones"],
            100.4);
        TranscriptSection secondOrderedSpeech = ExactWordSection(
            ["winter", "lanterns", "illuminate", "quiet", "pathways"],
            110.4);
        TranscriptSection combined = new(
            0,
            180,
            crossingSpeech.Segments
                .Concat(firstOrderedSpeech.Segments)
                .Concat(secondOrderedSpeech.Segments)
                .OrderBy(segment => segment.StartSeconds)
                .ToArray());

        CueTimingMatch[] matches = Match(
            [combined],
            [crossingCue, firstOrderedCue, secondOrderedCue]).ToArray();

        Assert.Equal([1, 2], matches.Select(match => match.CueIndex));
        Assert.True(matches[0].MediaSeconds < matches[1].MediaSeconds);
    }

    [Fact]
    public void MatchIndividualCues_SequenceContextDisambiguatesRepeatedDialogue()
    {
        SubtitleCue openingCue = new(
            100,
            106,
            "patient astronomers chart luminous winter constellations");
        SubtitleCue repeatedCue = new(
            130,
            136,
            "chromatic fireflies circle midnight cedar branches");
        SubtitleCue closingCue = new(
            140,
            146,
            "quiet otters gather polished river stones");
        TranscriptSection openingSpeech = ExactWordSection(
            ["patient", "astronomers", "chart", "luminous", "winter", "constellations"],
            100.5);
        TranscriptSection firstRepeatedSpeech = ExactWordSection(
            ["chromatic", "fireflies", "circle", "midnight", "cedar", "branches"],
            110.5);
        TranscriptSection closingSpeech = ExactWordSection(
            ["quiet", "otters", "gather", "polished", "river", "stones"],
            120.5);
        TranscriptSection secondRepeatedSpeech = ExactWordSection(
            ["chromatic", "fireflies", "circle", "midnight", "cedar", "branches"],
            150.5);
        TranscriptSection combined = new(
            0,
            180,
            openingSpeech.Segments
                .Concat(firstRepeatedSpeech.Segments)
                .Concat(closingSpeech.Segments)
                .Concat(secondRepeatedSpeech.Segments)
                .OrderBy(segment => segment.StartSeconds)
                .ToArray());

        CueTimingMatch[] matches = Match(
            [combined],
            [openingCue, repeatedCue, closingCue]).ToArray();

        Assert.Equal([0, 1, 2], matches.Select(match => match.CueIndex));
        Assert.InRange(matches[1].MediaStartSeconds, 110, 111);
    }

    [Fact]
    public void MatchIndividualCues_AssignsOverlappingSpokenWordsToTheMoreCredibleCue()
    {
        SubtitleCue firstCue = new(100, 104, "patient otters gather polished");
        SubtitleCue secondCue = new(105, 109, "otters gather polished stones");
        TranscriptSection utterance = ExactWordSection(
            ["patient", "otters", "gather", "polished", "stones"],
            110.5);

        CueTimingMatch match = Assert.Single(Match([utterance], [firstCue, secondCue]));

        Assert.Equal(1, match.CueIndex);
    }

    [Fact]
    public void MatchIndividualCues_SkipsCueFromMissingScene()
    {
        SubtitleCue missingCue = new(100, 104, "missing distinctive dialogue phrase");
        SubtitleCue presentCue = new(110, 114, "present unusual spoken sentence");
        TranscriptSection section = ExactWordSection(
            ["present", "unusual", "spoken", "sentence"],
            110.5);

        CueTimingMatch match = Assert.Single(Match([section], [missingCue, presentCue]));

        Assert.Equal(1, match.CueIndex);
    }

    private static IReadOnlyList<CueTimingMatch> Match(
        IReadOnlyList<TranscriptSection> sections,
        IReadOnlyList<SubtitleCue> cues) =>
        TextMatcher.MatchIndividualCues(
            sections,
            cues,
            60,
            0.6,
            0.05,
            enableFuzzyMatching: false,
            new GuidedFuzzyMatchOptions(0.55, 0.08, 3, 0.82, true, true, 0.4));

    private static TranscriptSection ExactWordSection(IReadOnlyList<string> words, double firstWordSeconds)
    {
        WhisperSegment[] segments = words
            .Select((word, index) => new WhisperSegment(
                firstWordSeconds + index,
                firstWordSeconds + index + 0.2,
                word,
                WhisperTimingGranularity.Word))
            .ToArray();
        return new TranscriptSection(0, 180, segments);
    }
}
