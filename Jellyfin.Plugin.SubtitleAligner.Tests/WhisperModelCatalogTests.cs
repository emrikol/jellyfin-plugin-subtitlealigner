namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using System.Security.Cryptography;
using Xunit;

public sealed class WhisperModelCatalogTests
{
    [Fact]
    public void SileroVad_UsesPinnedImmutableArtifact()
    {
        WhisperVadModelDefinition definition = WhisperModelCatalog.SileroVad;

        Assert.Equal("ggml-silero-v6.2.0.bin", definition.FileName);
        Assert.Equal(885_098, definition.SizeBytes);
        Assert.Equal("2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987", definition.Sha256);
        Assert.Contains("/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b/", definition.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task IsVerifiedAsync_ValidatesAuxiliaryModelSizeAndHash()
    {
        byte[] content = "synthetic-vad-model"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        WhisperVadModelDefinition definition = new(
            "synthetic-vad.bin",
            content.Length,
            hash,
            new Uri("https://example.invalid/synthetic-vad.bin", UriKind.Absolute));
        string path = Path.Combine(Path.GetTempPath(), $"subtitle-aligner-vad-{Guid.NewGuid():N}.bin");

        try
        {
            await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
            Assert.True(await WhisperModelManager.IsVerifiedAsync(
                definition,
                path,
                TestContext.Current.CancellationToken));

            await File.AppendAllTextAsync(path, "changed", TestContext.Current.CancellationToken);
            Assert.False(await WhisperModelManager.IsVerifiedAsync(
                definition,
                path,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
