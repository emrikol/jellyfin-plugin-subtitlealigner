namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>A pinned, plugin-managed Whisper model.</summary>
public sealed record WhisperModelDefinition(
    string FileName,
    string DisplayName,
    int QualityRank,
    string QualityTier,
    string Description,
    long SizeBytes,
    long EstimatedWorkingMemoryBytes,
    string Sha256,
    Uri DownloadUri,
    long MinimumAvailableMemoryBytes);

/// <summary>A pinned auxiliary VAD model used for speech-boundary evidence.</summary>
public sealed record WhisperVadModelDefinition(
    string FileName,
    long SizeBytes,
    string Sha256,
    Uri DownloadUri);

/// <summary>Pinned models supported by the plugin.</summary>
public static class WhisperModelCatalog
{
    private const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";
    private const string VadRevision = "9ffd54a1e1ee413ddf265af9913beaf518d1639b";

    /// <summary>Gets the lightweight model used to obtain real speech intervals.</summary>
    public static WhisperVadModelDefinition SileroVad { get; } = new(
        "ggml-silero-v6.2.0.bin",
        885_098,
        "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987",
        new Uri(
            $"https://huggingface.co/ggml-org/whisper-vad/resolve/{VadRevision}/ggml-silero-v6.2.0.bin",
            UriKind.Absolute));

    /// <summary>Gets the lightest supported model.</summary>
    public static WhisperModelDefinition TinyQ8 { get; } = Create(
        "ggml-tiny-q8_0.bin",
        "Whisper tiny q8_0",
        1,
        "Lightweight",
        "Fastest; best for low-power CPUs, with the lowest speech-recognition capacity.",
        43_537_433,
        273L * 1024 * 1024,
        "c2085835d3f50733e2ff6e4b41ae8a2b8d8110461e18821b09a15c40c42d1cca",
        512L * 1024 * 1024);

    /// <summary>Gets the fast default model.</summary>
    public static WhisperModelDefinition BaseQ8 { get; } = Create(
        "ggml-base-q8_0.bin",
        "Whisper base q8_0",
        2,
        "Balanced",
        "Balanced speech-recognition capacity and CPU cost; the normal choice for a small NAS.",
        81_768_585,
        388L * 1024 * 1024,
        "c577b9a86e7e048a0b7eada054f4dd79a56bbfa911fbdacf900ac5b567cbb7d9",
        768L * 1024 * 1024);

    /// <summary>Gets the larger quality candidate.</summary>
    public static WhisperModelDefinition SmallQ8 { get; } = Create(
        "ggml-small-q8_0.bin",
        "Whisper small q8_0",
        3,
        "Higher capacity",
        "More speech-recognition capacity, with substantially higher CPU cost.",
        264_464_607,
        852L * 1024 * 1024,
        "49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f",
        2L * 1024 * 1024 * 1024);

    /// <summary>Gets the fallback model used before host compatibility advice is available.</summary>
    public static WhisperModelDefinition Default => BaseQ8;

    /// <summary>Gets selectable managed models, ordered from lightest to heaviest.</summary>
    public static IReadOnlyList<WhisperModelDefinition> AssessmentCandidates { get; } = [TinyQ8, BaseQ8, SmallQ8];

    /// <summary>Gets the stable plugin data directory, independent of the installed plugin version.</summary>
    public static string DataDirectory
    {
        get
        {
            string configurationDirectory = Path.GetDirectoryName(Plugin.Instance.ConfigurationFilePath)
                ?? throw new InvalidOperationException("The plugin configuration directory is unavailable.");
            return Path.Combine(configurationDirectory, "SubtitleAligner");
        }
    }

    /// <summary>Gets the stable plugin-managed models directory.</summary>
    public static string ModelsDirectory => Path.Combine(DataDirectory, "models");

    /// <summary>Gets the stable plugin-managed engine directory.</summary>
    public static string EngineDirectory => Path.Combine(DataDirectory, "engine");

    /// <summary>Finds a pinned model by file name, returning the default for unknown or empty values.</summary>
    public static WhisperModelDefinition Find(string? fileName)
    {
        return AssessmentCandidates.FirstOrDefault(candidate =>
                   string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase))
               ?? Default;
    }

    private static WhisperModelDefinition Create(
        string fileName,
        string displayName,
        int qualityRank,
        string qualityTier,
        string description,
        long sizeBytes,
        long estimatedWorkingMemoryBytes,
        string sha256,
        long minimumAvailableMemoryBytes)
    {
        return new WhisperModelDefinition(
            fileName,
            displayName,
            qualityRank,
            qualityTier,
            description,
            sizeBytes,
            estimatedWorkingMemoryBytes,
            sha256,
            new Uri($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Revision}/{fileName}", UriKind.Absolute),
            minimumAvailableMemoryBytes);
    }
}
