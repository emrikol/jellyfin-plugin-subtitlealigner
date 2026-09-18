namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using Jellyfin.Plugin.SubtitleAligner.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TranscriptionCacheTests
{
    [Fact]
    public async Task StoreAndRead_RoundTripsValidatedResponseAndRefreshesAccessTime()
    {
        string directory = CreateTemporaryDirectory();
        DateTime now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            using TranscriptionCache cache = new(
                NullLogger<TranscriptionCache>.Instance,
                directory,
                () => now);
            PluginConfiguration configuration = CreateConfiguration();
            string wavPath = await CreateAudioAsync(directory);
            WhisperRouteCapabilities capabilities = CreateCapabilities("build-a", "whisper-large-v3");
            TranscriptionCacheKey key = await cache.CreateKeyAsync(
                wavPath,
                "ja",
                translateToEnglish: true,
                requireWordTimestamps: false,
                requireVadIntervals: true,
                capabilities,
                TestContext.Current.CancellationToken);

            await cache.StoreAsync(
                key,
                CreateTranscript(),
                configuration,
                requireWordTimestamps: false,
                requireVadIntervals: true,
                TestContext.Current.CancellationToken);
            now = now.AddDays(6);
            WhisperTranscript? restored = await cache.TryGetAsync(
                key,
                configuration,
                requireWordTimestamps: false,
                requireVadIntervals: true,
                TestContext.Current.CancellationToken);

            Assert.NotNull(restored);
            Assert.True(restored.Metadata.CacheHit);
            Assert.Equal("plugin_disk", restored.Metadata.CacheType);
            Assert.Equal("hello world", restored.Segments[0].Text);
            TranscriptionCacheStatus status = await cache.GetStatusAsync(
                configuration,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, status.EntryCount);
            Assert.Equal(1, status.SessionHits);
            Assert.Equal(now, status.NewestAccessUtc);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateKey_ChangesForRouteAndTimingRequirements()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            using TranscriptionCache cache = new(NullLogger<TranscriptionCache>.Instance, directory);
            string wavPath = await CreateAudioAsync(directory);
            TranscriptionCacheKey original = await cache.CreateKeyAsync(
                wavPath,
                "en",
                translateToEnglish: false,
                requireWordTimestamps: false,
                requireVadIntervals: false,
                CreateCapabilities("build-a", "model-a"),
                TestContext.Current.CancellationToken);
            TranscriptionCacheKey wordTiming = await cache.CreateKeyAsync(
                wavPath,
                "en",
                translateToEnglish: false,
                requireWordTimestamps: true,
                requireVadIntervals: false,
                CreateCapabilities("build-a", "model-a"),
                TestContext.Current.CancellationToken);
            TranscriptionCacheKey differentRoute = await cache.CreateKeyAsync(
                wavPath,
                "en",
                translateToEnglish: false,
                requireWordTimestamps: false,
                requireVadIntervals: false,
                CreateCapabilities("build-b", "model-b"),
                TestContext.Current.CancellationToken);

            Assert.NotEqual(original.Key, wordTiming.Key);
            Assert.NotEqual(original.Key, differentRoute.Key);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cleanup_RemovesEntriesUnusedBeyondRetention()
    {
        string directory = CreateTemporaryDirectory();
        DateTime now = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            using TranscriptionCache cache = new(
                NullLogger<TranscriptionCache>.Instance,
                directory,
                () => now);
            PluginConfiguration configuration = CreateConfiguration();
            string wavPath = await CreateAudioAsync(directory);
            TranscriptionCacheKey key = await cache.CreateKeyAsync(
                wavPath,
                "ja",
                translateToEnglish: true,
                requireWordTimestamps: false,
                requireVadIntervals: true,
                CreateCapabilities("build-a", "model-a"),
                TestContext.Current.CancellationToken);
            await cache.StoreAsync(
                key,
                CreateTranscript(),
                configuration,
                requireWordTimestamps: false,
                requireVadIntervals: true,
                TestContext.Current.CancellationToken);

            now = now.AddDays(8);
            TranscriptionCacheCleanupResult result = await cache.CleanupAsync(
                configuration,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.RemovedEntries);
            Assert.Equal(0, result.RemainingEntries);
            Assert.Null(await cache.TryGetAsync(
                key,
                configuration,
                requireWordTimestamps: false,
                requireVadIntervals: true,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryGet_DiscardsCorruptEntry()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            using TranscriptionCache cache = new(NullLogger<TranscriptionCache>.Instance, directory);
            PluginConfiguration configuration = CreateConfiguration();
            string wavPath = await CreateAudioAsync(directory);
            TranscriptionCacheKey key = await cache.CreateKeyAsync(
                wavPath,
                "en",
                translateToEnglish: false,
                requireWordTimestamps: false,
                requireVadIntervals: false,
                CreateCapabilities("build-a", "model-a"),
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(directory, key.Key + ".json"),
                "{not-json",
                TestContext.Current.CancellationToken);

            Assert.Null(await cache.TryGetAsync(
                key,
                configuration,
                requireWordTimestamps: false,
                requireVadIntervals: false,
                TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PluginConfiguration CreateConfiguration()
    {
        return new PluginConfiguration
        {
            EnableTranscriptionCache = true,
            TranscriptionCacheRetentionDays = 7,
            TranscriptionCacheMaximumMegabytes = 128
        };
    }

    private static WhisperTranscript CreateTranscript()
    {
        WhisperResponseMetadata metadata = new(
            HasExtensionMetadata: true,
            ExtensionSchemaVersion: "1.0",
            RequestId: "request-a",
            Task: "translate",
            RequestedModel: "auto",
            SelectedModel: "whisper-large-v3",
            Backend: "whisper.cpp",
            ServerVersion: "1.0",
            DetectedLanguage: "ja",
            DetectedLanguageProbability: 0.99,
            DetectedLanguageProbabilityBasis: "decoder",
            EffectiveLanguage: "ja",
            AudioDurationSeconds: 2,
            PreparationSeconds: 0.1,
            InferenceSeconds: 0.2,
            RealtimeFactor: 0.1,
            CacheHit: false,
            InferenceIncludesPreparation: false,
            DecodeCount: 2,
            Timing: new WhisperTimingProvenance(
                ["segment", "vad"],
                ["segment", "vad"],
                "decoder_segment",
                true,
                "high"),
            Warnings: [],
            SpeechIntervals: [new WhisperSpeechInterval(0, 1, 0.9)],
            RouteCapabilitiesKnown: true,
            RouteTimingGranularities: ["segment", "vad"],
            RouteVadIntervals: true,
            BuildIdentity: "build-a",
            RequestedLanguage: "ja",
            CacheType: string.Empty);
        return new WhisperTranscript(
            [new WhisperSegment(0, 1, "hello world", WhisperTimingGranularity.Segment)],
            metadata,
            [new WhisperSegment(0, 1, "source speech", WhisperTimingGranularity.Segment)]);
    }

    private static WhisperRouteCapabilities CreateCapabilities(string buildIdentity, string model)
    {
        return new WhisperRouteCapabilities(
            HasExtensionContract: true,
            ObjectType: "audio.route_capabilities",
            ExtensionSchemaVersion: "1.0",
            Task: "translate",
            RequestedModel: "auto",
            SelectedModel: model,
            Backend: "whisper.cpp",
            ModelFilesCached: true,
            SupportedTasks: ["transcribe", "translate"],
            TimingGranularities: ["segment", "word", "vad"],
            VadIntervals: true,
            SupportedLanguages: ["auto", "en", "ja"],
            AutomaticLanguageDetection: true,
            EffectiveLanguage: "ja",
            MaximumUploadBytes: null,
            MaximumAudioDurationSeconds: null,
            Limitations: [],
            ConfigurationCanChange: false,
            BuildIdentity: buildIdentity,
            TimingBases: ["decoder_segment", "decoder_word", "vad"],
            ResponseFormats: ["verbose_json"],
            DualOutput: true,
            DualOutputDecodeCount: 2,
            Progress: true,
            Cancellation: true,
            WordProbability: true,
            DecoderTokenProbabilities: true,
            SegmentAverageLogProbability: true,
            SegmentNoSpeechProbability: true,
            DetectedLanguageProbability: true,
            ErrorCodes: []);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "subtitle-aligner-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> CreateAudioAsync(string directory)
    {
        string path = Path.Combine(directory, "audio.wav");
        await File.WriteAllBytesAsync(
            path,
            [0x52, 0x49, 0x46, 0x46, 0x01, 0x02, 0x03, 0x04],
            TestContext.Current.CancellationToken);
        return path;
    }
}
