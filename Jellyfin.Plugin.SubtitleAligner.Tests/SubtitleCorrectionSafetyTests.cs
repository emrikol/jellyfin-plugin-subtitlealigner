namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using Jellyfin.Plugin.SubtitleAligner.Configuration;
using Xunit;

public sealed class SubtitleCorrectionSafetyTests
{
    [Theory]
    [InlineData(0.10, 0.10, false)]
    [InlineData(-0.10, 0.10, false)]
    [InlineData(0.101, 0.10, true)]
    [InlineData(-0.101, 0.10, true)]
    [InlineData(0.075, 0, false)]
    public void CueDeadBand_SeparatesVerifiedFromAdjusted(
        double offsetSeconds,
        double deadBandSeconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            SubtitleValidationService.IsMeaningfulCueAdjustment(offsetSeconds, deadBandSeconds));
    }

    [Fact]
    public void CueDeadBand_UsesEvidenceSpecificDefaults()
    {
        PluginConfiguration configuration = new();

        Assert.Equal(0.10, SubtitleValidationService.ResolveCueDeadBandSeconds(configuration, crossLanguageVad: false));
        Assert.Equal(0.75, SubtitleValidationService.ResolveCueDeadBandSeconds(configuration, crossLanguageVad: true));
    }

    [Fact]
    public void CueDeadBand_UsesConservativeVadFallbackForLegacyConfiguration()
    {
        PluginConfiguration configuration = new() { CrossLanguageVadDeadBandSeconds = 0 };

        Assert.Equal(0.75, SubtitleValidationService.ResolveCueDeadBandSeconds(configuration, crossLanguageVad: true));
    }

    [Fact]
    public void FuzzyMatching_DefaultsToBothLanguagePaths()
    {
        PluginConfiguration configuration = new();

        Assert.True(SubtitleValidationService.ResolveFuzzyMatchingEnabled(configuration, crossLanguage: false));
        Assert.True(SubtitleValidationService.ResolveFuzzyMatchingEnabled(configuration, crossLanguage: true));

        configuration.EnableCrossLanguageFuzzyMatching = false;
        Assert.False(SubtitleValidationService.ResolveFuzzyMatchingEnabled(configuration, crossLanguage: true));
    }

    [Fact]
    public void CrossLanguageCueCoverage_UsesConservativeDefaultAndLegacyFallback()
    {
        PluginConfiguration configuration = new();

        Assert.Equal(0.35, SubtitleValidationService.ResolveCrossLanguageMinimumCueCoverage(configuration));

        configuration.CrossLanguageMinimumCueCoverage = 0;
        Assert.Equal(0.35, SubtitleValidationService.ResolveCrossLanguageMinimumCueCoverage(configuration));
    }

    [Theory]
    [InlineData(0.10, 0.25)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.8, 0.8)]
    [InlineData(1.25, 1.0)]
    public void CrossLanguageCueCoverage_ClampsConfiguredValue(double configured, double expected)
    {
        PluginConfiguration configuration = new() { CrossLanguageMinimumCueCoverage = configured };

        Assert.Equal(expected, SubtitleValidationService.ResolveCrossLanguageMinimumCueCoverage(configuration));
    }

    [Fact]
    public void MaximumIndividualCueOffset_UsesBoundedDefaultAndLegacyFallback()
    {
        PluginConfiguration configuration = new();
        Assert.Equal(2.0, SubtitleValidationService.ResolveMaximumIndividualCueOffsetSeconds(configuration));

        configuration.MaximumIndividualCueOffsetSeconds = 0;
        Assert.Equal(2.0, SubtitleValidationService.ResolveMaximumIndividualCueOffsetSeconds(configuration));

        configuration.MaximumIndividualCueOffsetSeconds = 20;
        Assert.Equal(10.0, SubtitleValidationService.ResolveMaximumIndividualCueOffsetSeconds(configuration));
    }

    [Theory]
    [InlineData(5, 60, 0.80, "High")]
    [InlineData(4, 35, 0.70, "Medium")]
    [InlineData(3, 100, 0.95, "Inconclusive")]
    public void DirectSemanticConfidence_DependsOnIndependentEvidenceNotTitleCoverage(
        int alignedCues,
        int matchedWords,
        double medianScore,
        string expected)
    {
        Assert.Equal(
            expected,
            SubtitleValidationService.ClassifyDirectSemanticConfidence(
                alignedCues,
                matchedWords,
                medianScore,
                new PluginConfiguration()));
    }

    [Fact]
    public void SharedCueBias_DetectsCoherentWholeTrackShift()
    {
        bool detected = SubtitleValidationService.TryDetectSharedCueBias(
            [1.92, 2.04, 1.88, 2.15, 1.97, 0.10],
            0.75,
            out double median,
            out int agreeing);

        Assert.True(detected);
        Assert.Equal(1.97, median, 3);
        Assert.Equal(5, agreeing);
    }

    [Fact]
    public void SharedCueBias_IgnoresInBandAndConflictingCandidates()
    {
        bool detected = SubtitleValidationService.TryDetectSharedCueBias(
            [-0.84, -0.89, -0.78, -1.61, -1.11, 1.44, -0.84, -0.98, -0.34, 0.34],
            0.75,
            out double median,
            out int agreeing);

        Assert.True(detected);
        Assert.Equal(-0.865, median, 3);
        Assert.Equal(6, agreeing);
    }

    [Fact]
    public void SharedCueBias_DoesNotMisclassifySparseOutliers()
    {
        bool detected = SubtitleValidationService.TryDetectSharedCueBias(
            [0.04, -0.03, 0.08, -0.06, 1.12],
            0.75,
            out _,
            out _);

        Assert.False(detected);
    }

    [Fact]
    public void SharedCueBias_RequiresIndependentAgreement()
    {
        bool detected = SubtitleValidationService.TryDetectSharedCueBias(
            [1.2, -1.1, 1.5, -1.4, 0.9, -0.8],
            0.10,
            out _,
            out _);

        Assert.False(detected);
    }

    [Fact]
    public void IsCorrectable_RejectsRunWithNoCueCorrections()
    {
        ValidationRecord result = new()
        {
            Status = "Locally aligned",
            Confidence = "High",
            CueAdjustmentStrategy = "Direct",
            CueTimingPoints =
            [
                new SubtitleCueTimingPoint { Adjusted = false, MatchKind = "Verified", OffsetSeconds = 0.04 },
                new SubtitleCueTimingPoint { Adjusted = false, MatchKind = "Verified", OffsetSeconds = -0.03 },
                new SubtitleCueTimingPoint { Adjusted = false, MatchKind = "Verified", OffsetSeconds = 0.01 }
            ]
        };

        Assert.False(SubtitleValidationService.IsCorrectable(result));
    }

    [Fact]
    public void IsCorrectable_AcceptsOneDirectCorrectionWithStrongRunEvidence()
    {
        ValidationRecord result = new()
        {
            Status = "Locally aligned",
            Confidence = "High",
            CueAdjustmentStrategy = "Direct",
            CueTimingPoints =
            [
                new SubtitleCueTimingPoint { Adjusted = false, MatchKind = "Verified", OffsetSeconds = 0.04 },
                new SubtitleCueTimingPoint { Adjusted = true, MatchKind = "Exact", OffsetSeconds = 0.45 }
            ]
        };

        Assert.True(SubtitleValidationService.IsCorrectable(result));
    }

    [Fact]
    public async Task DirectCueCorrection_ChangesStartButPreservesEnd()
    {
        string directory = Directory.CreateTempSubdirectory("subtitle-aligner-tests-").FullName;
        string sourcePath = Path.Combine(directory, "source.srt");
        string destinationPath = Path.Combine(directory, "corrected.srt");
        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                "1\n00:00:10,000 --> 00:00:12,000\nSynthetic dialogue.\n",
                TestContext.Current.CancellationToken);
            ValidationRecord result = new()
            {
                Status = "Locally aligned",
                CueAdjustmentStrategy = "Direct",
                CueTimingPoints =
                [
                    new SubtitleCueTimingPoint
                    {
                        Adjusted = true,
                        MatchKind = "Exact",
                        OffsetSeconds = 0.5
                    }
                ]
            };

            await SubtitleCorrector.WriteCorrectedCopyAsync(
                sourcePath,
                destinationPath,
                result,
                overwrite: false,
                TestContext.Current.CancellationToken);

            string corrected = await File.ReadAllTextAsync(
                destinationPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("00:00:10,500 --> 00:00:12,000", corrected, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DirectCueCorrection_UsesSourceIndexWhenNonSpeechAssRowsWereExcluded()
    {
        string directory = Directory.CreateTempSubdirectory("subtitle-aligner-tests-").FullName;
        string sourcePath = Path.Combine(directory, "source.ass");
        string destinationPath = Path.Combine(directory, "corrected.ass");
        try
        {
            const string Source = """
                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,(DOOR SLAMS)
                Dialogue: 0,0:00:10.00,0:00:12.00,Default,,0,0,0,,Synthetic spoken dialogue
                """;
            await File.WriteAllTextAsync(sourcePath, Source, TestContext.Current.CancellationToken);
            ValidationRecord result = new()
            {
                Status = "Locally aligned",
                CueAdjustmentStrategy = "Direct",
                CueTimingPoints =
                [
                    new SubtitleCueTimingPoint
                    {
                        SourceCueIndex = 1,
                        Adjusted = true,
                        MatchKind = "Exact",
                        OffsetSeconds = -0.5
                    }
                ]
            };

            await SubtitleCorrector.WriteCorrectedCopyAsync(
                sourcePath,
                destinationPath,
                result,
                overwrite: false,
                TestContext.Current.CancellationToken);

            string corrected = await File.ReadAllTextAsync(
                destinationPath,
                TestContext.Current.CancellationToken);
            Assert.Contains(
                "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,(DOOR SLAMS)",
                corrected,
                StringComparison.Ordinal);
            Assert.Contains(
                "Dialogue: 0,0:00:09.50,0:00:12.00,Default,,0,0,0,,Synthetic spoken dialogue",
                corrected,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DirectCueCorrection_IsByteDeterministic()
    {
        string directory = Directory.CreateTempSubdirectory("subtitle-aligner-tests-").FullName;
        string sourcePath = Path.Combine(directory, "source.srt");
        string firstPath = Path.Combine(directory, "first.srt");
        string secondPath = Path.Combine(directory, "second.srt");
        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                "1\n00:00:10,000 --> 00:00:12,000\nSynthetic dialogue.\n",
                TestContext.Current.CancellationToken);
            ValidationRecord result = new()
            {
                Status = "Locally aligned",
                CueAdjustmentStrategy = "Direct",
                CueTimingPoints =
                [
                    new SubtitleCueTimingPoint
                    {
                        Adjusted = true,
                        MatchKind = "Exact",
                        OffsetSeconds = -0.25
                    }
                ]
            };

            await SubtitleCorrector.WriteCorrectedCopyAsync(
                sourcePath,
                firstPath,
                result,
                overwrite: false,
                TestContext.Current.CancellationToken);
            await SubtitleCorrector.WriteCorrectedCopyAsync(
                sourcePath,
                secondPath,
                result,
                overwrite: false,
                TestContext.Current.CancellationToken);

            byte[] first = await File.ReadAllBytesAsync(firstPath, TestContext.Current.CancellationToken);
            byte[] second = await File.ReadAllBytesAsync(secondPath, TestContext.Current.CancellationToken);
            Assert.Equal(first, second);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteCorrectedCopy_LeavesSourceBytesUnchanged()
    {
        string directory = Directory.CreateTempSubdirectory("subtitle-aligner-tests-").FullName;
        string sourcePath = Path.Combine(directory, "source.srt");
        string destinationPath = Path.Combine(directory, "source.subalign.srt");
        byte[] source = "1\r\n00:00:10,000 --> 00:00:12,000\r\nSynthetic dialogue.\r\n"u8.ToArray();
        try
        {
            await File.WriteAllBytesAsync(sourcePath, source, TestContext.Current.CancellationToken);

            await SubtitleCorrector.WriteCorrectedCopyAsync(
                sourcePath,
                destinationPath,
                ConstantOffsetResult(0.5),
                overwrite: false,
                TestContext.Current.CancellationToken);

            Assert.Equal(source, await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken));
            byte[] corrected = await File.ReadAllBytesAsync(destinationPath, TestContext.Current.CancellationToken);
            Assert.NotEqual(source, corrected);
            Assert.Contains("\r\n", System.Text.Encoding.UTF8.GetString(corrected), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteCorrectedCopy_SupportsAssWithCrLfWithoutChangingLineEndings()
    {
        string directory = Directory.CreateTempSubdirectory("subtitle-aligner-tests-").FullName;
        string sourcePath = Path.Combine(directory, "source.ass");
        string destinationPath = Path.Combine(directory, "source.subalign.ass");
        const string source = "[Events]\r\nDialogue: 0,0:00:10.00,0:00:12.00,Default,,0,0,0,,Synthetic dialogue.\r\n";
        try
        {
            await File.WriteAllTextAsync(sourcePath, source, TestContext.Current.CancellationToken);

            await SubtitleCorrector.WriteCorrectedCopyAsync(
                sourcePath,
                destinationPath,
                ConstantOffsetResult(0.5),
                overwrite: false,
                TestContext.Current.CancellationToken);

            string corrected = await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken);
            Assert.Contains("Dialogue: 0,0:00:10.50,0:00:12.50", corrected, StringComparison.Ordinal);
            Assert.Contains("\r\n", corrected, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteCorrectedCopy_DoesNotReplaceExistingFileWithoutExplicitOwnershipDecision()
    {
        string directory = Directory.CreateTempSubdirectory("subtitle-aligner-tests-").FullName;
        string sourcePath = Path.Combine(directory, "source.srt");
        string destinationPath = Path.Combine(directory, "source.subalign.srt");
        const string existing = "user-owned subtitle data";
        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                "1\n00:00:10,000 --> 00:00:12,000\nSynthetic dialogue.\n",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(destinationPath, existing, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(() => SubtitleCorrector.WriteCorrectedCopyAsync(
                sourcePath,
                destinationPath,
                ConstantOffsetResult(0.5),
                overwrite: false,
                TestContext.Current.CancellationToken));

            Assert.Equal(existing, await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ValidationRecord ConstantOffsetResult(double offsetSeconds) => new()
    {
        Status = "Constant offset",
        OffsetSeconds = offsetSeconds
    };
}
