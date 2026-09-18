namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using Xunit;

public sealed class QuickAlignmentClassificationTests
{
    [Theory]
    [InlineData(-5.0)]
    [InlineData(-2.0)]
    [InlineData(-0.5)]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void Classify_RecognizesSyntheticConstantShift(double offsetSeconds)
    {
        ValidationRecord result = Classify(mediaSeconds => offsetSeconds);

        Assert.Equal("Constant offset", result.Status);
        Assert.Equal("High", result.Confidence);
        Assert.Equal(offsetSeconds, result.OffsetSeconds);
        Assert.True(SubtitleValidationService.IsCorrectable(result));
    }

    [Fact]
    public void Classify_RecognizesSyntheticProgressiveDrift()
    {
        ValidationRecord result = Classify(mediaSeconds => 2 * mediaSeconds / MediaDurationSeconds);

        Assert.Equal("Progressive drift", result.Status);
        Assert.Equal("High", result.Confidence);
        Assert.InRange(result.DriftSecondsPerHour ?? double.NaN, 1.99, 2.01);
        Assert.False(SubtitleValidationService.IsCorrectable(result));
    }

    [Fact]
    public void Classify_RecognizesSyntheticMidTitleTimingJump()
    {
        ValidationRecord result = Classify(mediaSeconds => mediaSeconds < 1800 ? 0.25 : 2.25);

        Assert.Equal("Timing break", result.Status);
        Assert.Equal("High", result.Confidence);
        Assert.InRange(result.BreakSeconds ?? double.NaN, 1574, 2026);
        Assert.InRange(result.AfterBreakOffsetSeconds - result.BeforeBreakOffsetSeconds ?? double.NaN, 1.99, 2.01);
        Assert.False(SubtitleValidationService.IsCorrectable(result));
    }

    [Theory]
    [InlineData("Timing break", "Quick", true, true)]
    [InlineData("Progressive drift", "Quick", true, true)]
    [InlineData("Inconsistent", "Quick", true, true)]
    [InlineData("Constant offset", "Quick", true, false)]
    [InlineData("Inconsistent", "Quick", false, false)]
    [InlineData("Inconsistent", "Full", true, false)]
    public void IndividualCueRecommendation_RequiresFailedWholeTrackModelAndBackendSupport(
        string status,
        string alignmentMode,
        bool backendSupportsIndividualCueAlignment,
        bool expectedRecommendation)
    {
        ValidationRecord result = new()
        {
            Status = status,
            AlignmentMode = alignmentMode
        };

        SubtitleValidationService.SetIndividualCueRecommendation(
            result,
            backendSupportsIndividualCueAlignment);

        Assert.Equal(backendSupportsIndividualCueAlignment, result.IndividualCueAlignmentAvailable);
        Assert.Equal(expectedRecommendation, result.IndividualCueAlignmentRecommended);
    }

    [Fact]
    public void SpeechActivityCorroboration_AllowsWholeTrackShiftOnlyAfterFiveAgreeingWindows()
    {
        ValidationRecord result = Classify(_ => 1.25);

        SubtitleValidationService.ApplySpeechActivityCorroboration(
            result,
            ActivitySamples(1.24, 1.25, 1.26, 1.24, 1.25),
            residualToleranceSeconds: 0.25);

        Assert.Equal("Constant offset", result.Status);
        Assert.Equal("High", result.Confidence);
        Assert.Equal("High", result.SemanticConfidence);
        Assert.Equal("High", result.TimingConfidence);
        Assert.Equal(5, result.SpeechActivitySamples);
        Assert.Equal(1.25, result.SpeechActivityOffsetSeconds);
        Assert.True(SubtitleValidationService.IsCorrectable(result));
    }

    [Fact]
    public void SpeechActivityCorroboration_RefusesShiftWhenIndependentTimingDisagrees()
    {
        ValidationRecord result = Classify(_ => 1.25);

        SubtitleValidationService.ApplySpeechActivityCorroboration(
            result,
            ActivitySamples(-0.75, -0.74, -0.76, -0.75, -0.74),
            residualToleranceSeconds: 0.25);

        Assert.Equal("Insufficient evidence", result.Status);
        Assert.Equal("Inconclusive", result.Confidence);
        Assert.Equal("Inconclusive", result.TimingConfidence);
        Assert.False(SubtitleValidationService.IsCorrectable(result));
    }

    [Fact]
    public void SpeechActivityCorroboration_RefusesShiftWithTooFewIndependentWindows()
    {
        ValidationRecord result = Classify(_ => 1.25);

        SubtitleValidationService.ApplySpeechActivityCorroboration(
            result,
            ActivitySamples(1.24, 1.25, 1.26, 1.25),
            residualToleranceSeconds: 0.25);

        Assert.Equal("Insufficient evidence", result.Status);
        Assert.Equal("Inconclusive", result.TimingConfidence);
        Assert.False(SubtitleValidationService.IsCorrectable(result));
    }

    private const double MediaDurationSeconds = 3600;

    private static SpeechActivitySample[] ActivitySamples(params double[] offsets) => offsets
        .Select((offset, index) => new SpeechActivitySample(
            index * 300,
            new SpeechActivityAlignment(
                offset,
                RateRatio: 1,
                Correlation: 0.85,
                Uniqueness: 0.15,
                SpeechActivitySeconds: 12,
                SubtitleActivitySeconds: 12,
                GridSeconds: 0.01),
            EvidenceType: "VAD"))
        .ToArray();

    private static ValidationRecord Classify(Func<double, double> offsetAt)
    {
        TimingAnchor[] anchors = Enumerable.Range(0, 9)
            .Select(index => index * (MediaDurationSeconds / 8))
            .Select(mediaSeconds =>
            {
                double offset = offsetAt(mediaSeconds);
                return new TimingAnchor(
                    mediaSeconds,
                    mediaSeconds - offset,
                    offset,
                    Similarity: 0.95,
                    Uniqueness: 0.30,
                    MatchedWords: 12,
                    Weight: 1);
            })
            .ToArray();

        SubtitleInput input = new(
            Guid.NewGuid(),
            "Synthetic library",
            Guid.NewGuid(),
            "Synthetic title",
            "/synthetic/video.mkv",
            "/synthetic/video.en.srt",
            "eng",
            "eng",
            MediaDurationSeconds,
            EmbeddedSubtitleOrdinal: null,
            SubtitleExtension: ".srt");
        return SubtitleValidationService.Classify(
            input,
            "synthetic-fingerprint",
            anchors,
            samples: anchors.Length,
            subtitleDuration: MediaDurationSeconds,
            new QuickAlignmentClassificationOptions());
    }
}
