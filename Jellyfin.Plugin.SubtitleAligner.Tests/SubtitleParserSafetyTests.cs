namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using Xunit;

public sealed class SubtitleParserSafetyTests
{
    [Theory]
    [InlineData("Song - Romaji", "Lyrics")]
    [InlineData("OP", "Lyrics")]
    [InlineData("Ending Lyrics", "Lyrics")]
    [InlineData("Signs", "Sign")]
    [InlineData("On Screen Title", "Sign")]
    [InlineData("Default", "Dialogue")]
    public async Task ParseAsync_PreservesAssRoleSemantics(string style, string expectedRole)
    {
        string path = Path.Combine(Path.GetTempPath(), $"subtitle-aligner-{Guid.NewGuid():N}.ass");
        try
        {
            string contents = $"""
                [Script Info]
                ScriptType: v4.00+

                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                Dialogue: 0,0:00:01.00,0:00:03.00,{style},,0,0,0,,Synthetic words remain readable
                """;
            await File.WriteAllTextAsync(path, contents, TestContext.Current.CancellationToken);

            SubtitleCue cue = Assert.Single(await SubtitleParser.ParseAsync(path, TestContext.Current.CancellationToken));

            Assert.Equal(expectedRole, cue.Role.ToString());
            Assert.Equal("Synthetic words remain readable", cue.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_PreservesLyricRoleAfterCleaningMusicMarker()
    {
        string path = Path.Combine(Path.GetTempPath(), $"subtitle-aligner-{Guid.NewGuid():N}.srt");
        try
        {
            await File.WriteAllTextAsync(
                path,
                "1\n00:00:01,000 --> 00:00:03,000\n♪ Synthetic song words ♪\n",
                TestContext.Current.CancellationToken);

            SubtitleCue cue = Assert.Single(await SubtitleParser.ParseAsync(path, TestContext.Current.CancellationToken));

            Assert.Equal(SubtitleCueRole.Lyrics, cue.Role);
            Assert.Equal("Synthetic song words", cue.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MatchIndividualCues_LeavesAssSignUnmatched()
    {
        string path = Path.Combine(Path.GetTempPath(), $"subtitle-aligner-{Guid.NewGuid():N}.ass");
        try
        {
            const string Contents = """
                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                Dialogue: 0,0:00:10.00,0:00:12.00,Signs,,0,0,0,,Synthetic station sign
                """;
            await File.WriteAllTextAsync(path, Contents, TestContext.Current.CancellationToken);
            IReadOnlyList<SubtitleCue> cues = await SubtitleParser.ParseAsync(path, TestContext.Current.CancellationToken);
            TranscriptSection[] sections =
            [
                new(0, 30,
                [
                    new WhisperSegment(10.4, 10.7, "Synthetic", WhisperTimingGranularity.Word),
                    new WhisperSegment(10.8, 11.1, "station", WhisperTimingGranularity.Word),
                    new WhisperSegment(11.2, 11.5, "sign", WhisperTimingGranularity.Word)
                ])
            ];

            IReadOnlyList<CueTimingMatch> matches = TextMatcher.MatchIndividualCues(
                sections,
                cues,
                searchRadiusSeconds: 15,
                minimumSimilarity: 0.3,
                minimumUniqueness: 0.01,
                enableFuzzyMatching: true,
                new GuidedFuzzyMatchOptions(0.3, 0.01, 3, 0.6, true, true, 0.3));

            Assert.Empty(matches);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_PreservesSourceIndexesAcrossExcludedAssRows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"subtitle-aligner-{Guid.NewGuid():N}.ass");
        try
        {
            const string Contents = """
                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,(DOOR SLAMS)
                Dialogue: 0,0:00:03.00,0:00:05.00,Default,,0,0,0,,First spoken cue
                Dialogue: 0,0:00:06.00,0:00:08.00,Default,,0,0,0,,Second spoken cue
                """;
            await File.WriteAllTextAsync(path, Contents, TestContext.Current.CancellationToken);

            IReadOnlyList<SubtitleCue> cues = await SubtitleParser.ParseAsync(
                path,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, cues.Count);
            Assert.Equal(1, cues[0].SourceCueIndex);
            Assert.Equal(2, cues[1].SourceCueIndex);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
