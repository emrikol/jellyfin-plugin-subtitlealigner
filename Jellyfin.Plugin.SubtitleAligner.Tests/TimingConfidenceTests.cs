namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using Xunit;

public sealed class TimingConfidenceTests
{
    [Fact]
    public void ClassifyTimingConfidence_SeparatesReliableWordTimingFromSemanticEvidence()
    {
        WhisperSegment[] segments =
        [
            new(1.0, 1.2, "Synthetic", WhisperTimingGranularity.Word),
            new(1.3, 1.5, "dialogue", WhisperTimingGranularity.Word)
        ];
        WhisperResponseMetadata[] metadata = [Metadata(reliable: true)];

        Assert.Equal("High", SubtitleValidationService.ClassifyTimingConfidence(segments, metadata));
    }

    [Fact]
    public void ClassifyTimingConfidence_DowngradesLegacyTokenTiming()
    {
        WhisperSegment[] segments =
        [
            new(1.0, 1.2, "Synthetic", WhisperTimingGranularity.Token),
            new(1.3, 1.5, "dialogue", WhisperTimingGranularity.Token)
        ];

        Assert.Equal("Medium", SubtitleValidationService.ClassifyTimingConfidence(segments, []));
    }

    [Fact]
    public void ClassifyTimingConfidence_RejectsExplicitlyUnreliableTiming()
    {
        WhisperSegment[] segments = [new(1.0, 1.2, "Synthetic", WhisperTimingGranularity.Word)];
        WhisperResponseMetadata[] metadata = [Metadata(reliable: false)];

        Assert.Equal("Inconclusive", SubtitleValidationService.ClassifyTimingConfidence(segments, metadata));
    }

    private static WhisperResponseMetadata Metadata(bool reliable) => new(
        true,
        "1.0",
        "synthetic-request",
        "transcribe",
        "auto",
        "synthetic-model",
        "synthetic-backend",
        "1.0",
        "en",
        0.99,
        "synthetic-classifier",
        "en",
        2,
        0.1,
        0.2,
        0.1,
        true,
        false,
        1,
        new WhisperTimingProvenance(
            ["word"],
            ["word"],
            "decoder_word",
            reliable,
            reliable ? "decoder-provided" : "approximate",
            new Dictionary<string, WhisperTimingChannelProvenance>(StringComparer.Ordinal)
            {
                ["word"] = new("decoder_word", reliable, reliable ? "decoder-provided" : "approximate")
            }),
        [],
        []);
}
