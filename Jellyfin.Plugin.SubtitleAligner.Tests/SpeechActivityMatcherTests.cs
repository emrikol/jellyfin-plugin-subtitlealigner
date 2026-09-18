namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using Xunit;

public sealed class SpeechActivityMatcherTests
{
    [Theory]
    [InlineData(-2.0)]
    [InlineData(-1.25)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void Match_RecoversSyntheticGlobalSpeechShift(double shiftSeconds)
    {
        SubtitleCue[] cues =
        [
            new(8.0, 9.1, "Synthetic cue one"),
            new(14.0, 16.4, "Synthetic cue two"),
            new(23.0, 24.3, "Synthetic cue three"),
            new(31.0, 34.2, "Synthetic cue four"),
            new(48.0, 49.7, "Synthetic cue five")
        ];
        WhisperSpeechInterval[] speech = cues
            .Select(cue => new WhisperSpeechInterval(
                cue.StartSeconds + shiftSeconds,
                cue.EndSeconds + shiftSeconds,
                Confidence: 0.99))
            .ToArray();

        SpeechActivityAlignment result = Assert.IsType<SpeechActivityAlignment>(
            SpeechActivityMatcher.Match(
                speech,
                cues,
                windowStartSeconds: 0,
                windowEndSeconds: 60,
                maximumOffsetSeconds: 5));

        Assert.InRange(result.OffsetSeconds, shiftSeconds - 0.011, shiftSeconds + 0.011);
        Assert.Equal(1, result.RateRatio);
        Assert.InRange(result.Correlation, 0.99, 1.0);
        Assert.True(result.Uniqueness > 0);
    }

    [Fact]
    public void Match_SelectsKnownFramerateRatioAndOffset()
    {
        const double ratio = 1.001;
        const double offset = 0.4;
        SubtitleCue[] cues = Enumerable.Range(1, 11)
            .Select(index => new SubtitleCue(
                index * 300,
                (index * 300) + 2 + ((index % 3) * 0.4),
                $"Synthetic cue {index}"))
            .ToArray();
        WhisperSpeechInterval[] speech = cues
            .Select(cue => new WhisperSpeechInterval(
                (cue.StartSeconds * ratio) + offset,
                (cue.EndSeconds * ratio) + offset,
                Confidence: 0.99))
            .ToArray();

        SpeechActivityAlignment result = Assert.IsType<SpeechActivityAlignment>(
            SpeechActivityMatcher.Match(
                speech,
                cues,
                windowStartSeconds: 0,
                windowEndSeconds: 3600,
                maximumOffsetSeconds: 1,
                rateRatios: [1.0, ratio]));

        Assert.Equal(ratio, result.RateRatio, precision: 6);
        Assert.InRange(result.OffsetSeconds, offset - 0.011, offset + 0.011);
        Assert.InRange(result.Correlation, 0.99, 1.0);
    }

    [Fact]
    public void Match_RejectsSignalWithoutSpeechActivity()
    {
        SubtitleCue[] cues = [new(10, 12, "Synthetic cue")];

        Assert.Null(SpeechActivityMatcher.Match(
            [],
            cues,
            windowStartSeconds: 0,
            windowEndSeconds: 30,
            maximumOffsetSeconds: 5));
    }

    [Fact]
    public void Match_DoesNotTreatLyricsOrSignsAsSpokenDialogue()
    {
        WhisperSpeechInterval[] speech = [new(10, 12, Confidence: 0.99)];
        SubtitleCue[] nonDialogueCues =
        [
            new(10, 12, "Synthetic lyrics", SubtitleCueRole.Lyrics),
            new(16, 18, "Synthetic sign", SubtitleCueRole.Sign)
        ];

        Assert.Null(SpeechActivityMatcher.Match(
            speech,
            nonDialogueCues,
            windowStartSeconds: 0,
            windowEndSeconds: 30,
            maximumOffsetSeconds: 5));
    }

    [Fact]
    public void Match_RejectsNonFiniteSearchBounds()
    {
        WhisperSpeechInterval[] speech = [new(10, 12, Confidence: 0.99)];
        SubtitleCue[] cues = [new(10, 12, "Synthetic cue")];

        Assert.Null(SpeechActivityMatcher.Match(
            speech,
            cues,
            windowStartSeconds: 0,
            windowEndSeconds: double.PositiveInfinity,
            maximumOffsetSeconds: 5));
        Assert.Null(SpeechActivityMatcher.Match(
            speech,
            cues,
            windowStartSeconds: 0,
            windowEndSeconds: 30,
            maximumOffsetSeconds: double.NaN));
    }
}
