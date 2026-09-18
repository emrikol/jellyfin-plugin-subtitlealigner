using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SubtitleAligner.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Persists validated speech-service responses for deterministic reuse.</summary>
public sealed class TranscriptionCache : IDisposable
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<TranscriptionCache> _logger;
    private readonly string _cacheDirectory;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sessionHits;
    private long _sessionMisses;

    /// <summary>Initializes a new instance of the <see cref="TranscriptionCache"/> class.</summary>
    public TranscriptionCache(ILogger<TranscriptionCache> logger)
        : this(logger, Path.Combine(WhisperModelCatalog.DataDirectory, "transcription-cache"), () => DateTime.UtcNow)
    {
    }

    internal TranscriptionCache(
        ILogger<TranscriptionCache> logger,
        string cacheDirectory,
        Func<DateTime>? utcNow = null)
    {
        _logger = logger;
        _cacheDirectory = cacheDirectory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    internal async Task<TranscriptionCacheKey> CreateKeyAsync(
        string wavPath,
        string language,
        bool translateToEnglish,
        bool requireWordTimestamps,
        bool requireVadIntervals,
        WhisperRouteCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        string audioHash;
        await using (FileStream stream = new(
                         wavPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            audioHash = Convert.ToHexString(hash);
        }

        string normalizedLanguage = string.IsNullOrWhiteSpace(language)
            ? "auto"
            : language.Trim().ToLowerInvariant();
        string routeIdentity = string.Join(
            '\n',
            capabilities.ExtensionSchemaVersion,
            capabilities.Task,
            capabilities.RequestedModel,
            capabilities.SelectedModel,
            capabilities.Backend,
            capabilities.BuildIdentity,
            capabilities.EffectiveLanguage,
            string.Join(',', capabilities.TimingGranularities.Order(StringComparer.Ordinal)));
        string requestIdentity = string.Join(
            '\n',
            $"schema={SchemaVersion}",
            $"audio={audioHash}",
            $"language={normalizedLanguage}",
            $"translate={translateToEnglish}",
            $"word={requireWordTimestamps}",
            $"vad={requireVadIntervals}",
            $"route={routeIdentity}");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestIdentity))).ToLowerInvariant();
        return new TranscriptionCacheKey(key, audioHash, normalizedLanguage, routeIdentity);
    }

    internal async Task<WhisperTranscript?> TryGetAsync(
        TranscriptionCacheKey key,
        PluginConfiguration configuration,
        bool requireWordTimestamps,
        bool requireVadIntervals,
        CancellationToken cancellationToken)
    {
        if (!configuration.EnableTranscriptionCache)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetEntryPath(key.Key);
            FileInfo file = new(path);
            if (!IsRegularCacheFile(file))
            {
                Interlocked.Increment(ref _sessionMisses);
                return null;
            }

            DateTime now = _utcNow();
            int retentionDays = NormalizeRetentionDays(configuration.TranscriptionCacheRetentionDays);
            if (file.LastWriteTimeUtc < now.AddDays(-retentionDays))
            {
                DeleteFile(path);
                Interlocked.Increment(ref _sessionMisses);
                return null;
            }

            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                CachedTranscription? cached = await JsonSerializer.DeserializeAsync<CachedTranscription>(
                        stream,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cached is null
                    || cached.SchemaVersion != SchemaVersion
                    || !string.Equals(cached.Key, key.Key, StringComparison.Ordinal)
                    || !string.Equals(cached.AudioSha256, key.AudioSha256, StringComparison.Ordinal)
                    || !string.Equals(cached.Language, key.Language, StringComparison.Ordinal)
                    || !string.Equals(cached.RouteIdentity, key.RouteIdentity, StringComparison.Ordinal)
                    || !IsReusable(cached.Transcript, requireWordTimestamps, requireVadIntervals))
                {
                    DeleteFile(path);
                    Interlocked.Increment(ref _sessionMisses);
                    return null;
                }

                File.SetLastWriteTimeUtc(path, now);
                Interlocked.Increment(ref _sessionHits);
                return cached.Transcript with
                {
                    Metadata = cached.Transcript.Metadata with
                    {
                        CacheHit = true,
                        CacheType = "plugin_disk"
                    }
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "Discarding an unreadable Subtitle Aligner transcription-cache entry.");
                DeleteFile(path);
                Interlocked.Increment(ref _sessionMisses);
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task StoreAsync(
        TranscriptionCacheKey key,
        WhisperTranscript transcript,
        PluginConfiguration configuration,
        bool requireWordTimestamps,
        bool requireVadIntervals,
        CancellationToken cancellationToken)
    {
        if (!configuration.EnableTranscriptionCache
            || !IsReusable(transcript, requireWordTimestamps, requireVadIntervals))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            string path = GetEntryPath(key.Key);
            string temporaryPath = Path.Combine(_cacheDirectory, $".{key.Key}.{Guid.NewGuid():N}.tmp");
            CachedTranscription cached = new(
                SchemaVersion,
                key.Key,
                key.AudioSha256,
                key.Language,
                key.RouteIdentity,
                _utcNow(),
                transcript);
            try
            {
                await using (FileStream stream = new(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, cached, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, overwrite: true);
                File.SetLastWriteTimeUtc(path, _utcNow());
                TryRestrictPermissions(path);
                EnforceSizeLimitUnlocked(NormalizeMaximumBytes(configuration.TranscriptionCacheMaximumMegabytes));
            }
            finally
            {
                DeleteFile(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Subtitle Aligner could not persist a transcription-cache entry.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns current cache usage and configured limits.</summary>
    public async Task<TranscriptionCacheStatus> GetStatusAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<FileInfo> files = EnumerateEntries();
            return CreateStatus(configuration, files);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes expired entries and enforces the configured least-recently-used size limit.</summary>
    public async Task<TranscriptionCacheCleanupResult> CleanupAsync(
        PluginConfiguration configuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<FileInfo> initial = EnumerateEntries();
            long removedBytes = 0;
            int removedEntries = 0;
            DateTime expiry = _utcNow().AddDays(-NormalizeRetentionDays(configuration.TranscriptionCacheRetentionDays));
            long maximumBytes = NormalizeMaximumBytes(configuration.TranscriptionCacheMaximumMegabytes);
            List<FileInfo> retained = [];

            for (int index = 0; index < initial.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = initial[index];
                if (file.LastWriteTimeUtc < expiry)
                {
                    removedBytes += file.Length;
                    removedEntries++;
                    DeleteFile(file.FullName);
                }
                else
                {
                    retained.Add(file);
                }

                progress?.Report(initial.Count == 0 ? 80 : (index + 1) * 80.0 / initial.Count);
            }

            long retainedBytes = retained.Sum(file => file.Length);
            foreach (FileInfo file in retained.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (retainedBytes <= maximumBytes)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                retainedBytes -= file.Length;
                removedBytes += file.Length;
                removedEntries++;
                DeleteFile(file.FullName);
            }

            DeleteStaleTemporaryFiles();
            IReadOnlyList<FileInfo> remaining = EnumerateEntries();
            progress?.Report(100);
            return new TranscriptionCacheCleanupResult(
                removedEntries,
                removedBytes,
                remaining.Count,
                remaining.Sum(file => file.Length));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes every plugin-owned cached speech response.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (FileInfo file in EnumerateEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteFile(file.FullName);
            }

            DeleteStaleTemporaryFiles(removeAll: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsReusable(
        WhisperTranscript transcript,
        bool requireWordTimestamps,
        bool requireVadIntervals)
    {
        WhisperResponseMetadata metadata = transcript.Metadata;
        if (!metadata.HasExtensionMetadata
            || string.IsNullOrWhiteSpace(metadata.ExtensionSchemaVersion)
            || string.IsNullOrWhiteSpace(metadata.BuildIdentity)
            || string.IsNullOrWhiteSpace(metadata.SelectedModel)
            || !metadata.RouteCapabilitiesKnown)
        {
            return false;
        }

        if (requireWordTimestamps
            && metadata.RouteTimingGranularities?.Contains("word", StringComparer.Ordinal) != true)
        {
            return false;
        }

        if (requireVadIntervals && metadata.RouteVadIntervals != true)
        {
            return false;
        }

        return transcript.Segments.All(IsValidSegment)
            && (transcript.DecoderSegments?.All(IsValidSegment) ?? true)
            && metadata.SpeechIntervals.All(interval =>
                double.IsFinite(interval.StartSeconds)
                && double.IsFinite(interval.EndSeconds)
                && interval.StartSeconds >= 0
                && interval.EndSeconds >= interval.StartSeconds);
    }

    private static bool IsValidSegment(WhisperSegment segment)
    {
        return double.IsFinite(segment.StartSeconds)
            && double.IsFinite(segment.EndSeconds)
            && segment.StartSeconds >= 0
            && segment.EndSeconds >= segment.StartSeconds;
    }

    private TranscriptionCacheStatus CreateStatus(
        PluginConfiguration configuration,
        IReadOnlyList<FileInfo> files)
    {
        return new TranscriptionCacheStatus(
            configuration.EnableTranscriptionCache,
            files.Count,
            files.Sum(file => file.Length),
            NormalizeRetentionDays(configuration.TranscriptionCacheRetentionDays),
            NormalizeMaximumBytes(configuration.TranscriptionCacheMaximumMegabytes),
            files.Count == 0 ? null : files.Min(file => file.LastWriteTimeUtc),
            files.Count == 0 ? null : files.Max(file => file.LastWriteTimeUtc),
            Interlocked.Read(ref _sessionHits),
            Interlocked.Read(ref _sessionMisses));
    }

    private IReadOnlyList<FileInfo> EnumerateEntries()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_cacheDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(IsRegularCacheFile)
            .ToArray();
    }

    private static bool IsRegularCacheFile(FileInfo file)
    {
        return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private string GetEntryPath(string key)
    {
        return Path.Combine(_cacheDirectory, key + ".json");
    }

    private void DeleteStaleTemporaryFiles(bool removeAll = false)
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return;
        }

        DateTime cutoff = _utcNow().AddDays(-1);
        foreach (string path in Directory.EnumerateFiles(_cacheDirectory, ".*.tmp", SearchOption.TopDirectoryOnly))
        {
            FileInfo file = new(path);
            if (IsRegularCacheFile(file) && (removeAll || file.LastWriteTimeUtc < cutoff))
            {
                DeleteFile(path);
            }
        }
    }

    private void EnforceSizeLimitUnlocked(long maximumBytes)
    {
        IReadOnlyList<FileInfo> entries = EnumerateEntries();
        long totalBytes = entries.Sum(file => file.Length);
        foreach (FileInfo file in entries.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (totalBytes <= maximumBytes)
            {
                break;
            }

            totalBytes -= file.Length;
            DeleteFile(file.FullName);
        }
    }

    private void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Subtitle Aligner could not remove transcription-cache file {FileName}.", Path.GetFileName(path));
        }
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            // The Jellyfin data directory permissions remain the fallback on non-Unix filesystems.
        }
    }

    private static int NormalizeRetentionDays(int value) => Math.Clamp(value <= 0 ? 7 : value, 1, 365);

    private static long NormalizeMaximumBytes(int megabytes)
    {
        int normalized = Math.Clamp(megabytes <= 0 ? 2048 : megabytes, 128, 16_384);
        return normalized * 1024L * 1024L;
    }

    private sealed record CachedTranscription(
        int SchemaVersion,
        string Key,
        string AudioSha256,
        string Language,
        string RouteIdentity,
        DateTime CreatedUtc,
        WhisperTranscript Transcript);
}

internal sealed record TranscriptionCacheKey(
    string Key,
    string AudioSha256,
    string Language,
    string RouteIdentity);
