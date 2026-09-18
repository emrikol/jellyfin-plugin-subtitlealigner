namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using System.Security.Cryptography;
using Xunit;

public sealed class SidecarRestoreTests
{
    [Theory]
    [InlineData("old-plugin-hash", "original-hash", "original-hash", true)]
    [InlineData("ORIGINAL-HASH", "original-hash", "original-hash", false)]
    [InlineData("old-plugin-hash", "user-edited-hash", "original-hash", false)]
    [InlineData("old-plugin-hash", "original-hash", null, false)]
    public void OriginalTimingRecovery_RequiresAStalePluginHashAndExactOriginalContent(
        string recordedPluginHash,
        string currentSidecarHash,
        string? originalSubtitleHash,
        bool expected)
    {
        Assert.Equal(
            expected,
            SubtitleValidationService.IsRecoverableOriginalTimingSidecar(
                recordedPluginHash,
                currentSidecarHash,
                originalSubtitleHash));
    }

    [Fact]
    public async Task OriginalTimingRecovery_AllowsOnlyNarrowAssMuxerRounding()
    {
        string originalPath = Path.Combine(Path.GetTempPath(), $"subtitle-aligner-original-{Guid.NewGuid():N}.ass");
        string candidatePath = Path.Combine(Path.GetTempPath(), $"subtitle-aligner-candidate-{Guid.NewGuid():N}.ass");
        const string header = "[Script Info]\nTitle: Synthetic\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";
        const string originalDialogue = "Dialogue: 0,0:00:00.55,0:00:03.99,Default,Speaker,0000,0000,0000,,Original text\n";
        try
        {
            await File.WriteAllTextAsync(originalPath, header + originalDialogue, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                candidatePath,
                header.Replace("\n", "\r\n", StringComparison.Ordinal)
                    + "Dialogue: 0,0:00:00.54,0:00:03.98,Default,Speaker,0000,0000,0000,,Original text\r\n",
                TestContext.Current.CancellationToken);

            Assert.True(await SubtitleValidationService.AreEquivalentOriginalSubtitleFilesAsync(
                candidatePath,
                originalPath,
                TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(
                candidatePath,
                header + "Dialogue: 0,0:00:00.53,0:00:03.98,Default,Speaker,0000,0000,0000,,Original text\n",
                TestContext.Current.CancellationToken);
            Assert.False(await SubtitleValidationService.AreEquivalentOriginalSubtitleFilesAsync(
                candidatePath,
                originalPath,
                TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(
                candidatePath,
                header + "Dialogue: 0,0:00:00.54,0:00:03.98,Default,Speaker,0000,0000,0000,,Edited text\n",
                TestContext.Current.CancellationToken);
            Assert.False(await SubtitleValidationService.AreEquivalentOriginalSubtitleFilesAsync(
                candidatePath,
                originalPath,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(originalPath);
            File.Delete(candidatePath);
        }
    }

    [Fact]
    public async Task OverwriteFileAsync_ClosesDestinationBeforeReturning()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), $"subtitle-aligner-source-{Guid.NewGuid():N}.ass");
        string destinationPath = Path.Combine(Path.GetTempPath(), $"subtitle-aligner-destination-{Guid.NewGuid():N}.ass");
        try
        {
            await File.WriteAllTextAsync(sourcePath, "synthetic restored subtitle", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(destinationPath, "obsolete corrected subtitle", TestContext.Current.CancellationToken);

            await SubtitleValidationService.OverwriteFileAsync(
                sourcePath,
                destinationPath,
                TestContext.Current.CancellationToken);

            await using (FileStream exclusiveReader = new(
                destinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                byte[] hash = await SHA256.HashDataAsync(exclusiveReader, TestContext.Current.CancellationToken);
                Assert.NotEmpty(hash);
            }

            Assert.Equal("synthetic restored subtitle", await File.ReadAllTextAsync(
                destinationPath,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }
}
