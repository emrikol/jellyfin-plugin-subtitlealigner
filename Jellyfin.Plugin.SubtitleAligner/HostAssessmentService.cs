using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Jellyfin.Plugin.SubtitleAligner.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Advises model compatibility from host specifications and optionally benchmarks throughput.</summary>
public sealed class HostAssessmentService
{
    private const string SampleFileName = "jfk.wav";
    private const long SampleSize = 352_078;
    private const double SampleDurationSeconds = 11;
    private const int BenchmarkRuns = 3;
    private const string SampleSha256 = "59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e";
    private static readonly Uri SampleUri = new(
        "https://raw.githubusercontent.com/ggml-org/whisper.cpp/v1.9.4/samples/jfk.wav",
        UriKind.Absolute);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WhisperModelManager _modelManager;
    private readonly WhisperServer _whisperServer;
    private readonly InferenceActivityGate _inferenceGate;
    private readonly ILogger<HostAssessmentService> _logger;
    private readonly object _stateGate = new();
    private HostAssessmentStatus _status;

    /// <summary>Initializes a new instance of the <see cref="HostAssessmentService"/> class.</summary>
    public HostAssessmentService(
        IHttpClientFactory httpClientFactory,
        WhisperModelManager modelManager,
        WhisperServer whisperServer,
        InferenceActivityGate inferenceGate,
        ILogger<HostAssessmentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _modelManager = modelManager;
        _whisperServer = whisperServer;
        _inferenceGate = inferenceGate;
        _logger = logger;
        _status = CreateStatus(
            Plugin.Instance.Configuration,
            "Ready",
            "Choose a compatible model or run an optional throughput benchmark.",
            []);
    }

    /// <summary>Gets a snapshot of host advice and optional benchmark activity.</summary>
    public HostAssessmentStatus GetStatus()
    {
        lock (_stateGate)
        {
            RefreshInstalledFlags(_status.CompatibleModels);
            return CopyStatus(_status);
        }
    }

    /// <summary>Starts an optional background throughput benchmark for one compatible model.</summary>
    public bool TryStart(string modelName, out string message)
    {
        PluginConfiguration configuration = Plugin.Instance.Configuration;
        if (!string.IsNullOrWhiteSpace(configuration.WhisperModelPath))
        {
            message = "Clear the custom model path before benchmarking a managed model.";
            return false;
        }

        HostCapabilities host = InspectHost(configuration);
        IReadOnlyList<WhisperModelDefinition> compatible = GetCompatibleModels(host, configuration);
        WhisperModelDefinition? selected = compatible.FirstOrDefault(candidate =>
            string.Equals(candidate.FileName, modelName, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            message = "The selected model is not compatible with the detected host and memory constraints.";
            return false;
        }

        lock (_stateGate)
        {
            if (_status.Running)
            {
                message = "A model benchmark is already running.";
                return false;
            }

            _status.Running = true;
            _status.Stage = "Preparing benchmark";
            _status.Message = $"Preparing {selected.DisplayName} without changing the selected or installed model.";
            _status.Host = host;
            _status.CompatibleModels = BuildOptions(compatible);
        }

        _ = Task.Run(() => RunBenchmarkAsync(selected, CancellationToken.None));
        message = "Model benchmark started.";
        return true;
    }

    private async Task RunBenchmarkAsync(WhisperModelDefinition definition, CancellationToken cancellationToken)
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "subtitle-aligner-benchmark-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            SetProgress("Downloading test sample", "Downloading and verifying the temporary public-domain speech sample.");
            string samplePath = Path.Combine(temporaryDirectory, SampleFileName);
            await DownloadSampleAsync(samplePath, cancellationToken).ConfigureAwait(false);

            string installedPath = WhisperModelManager.GetManagedModelPath(definition);
            string modelPath;
            if (await WhisperModelManager.IsVerifiedAsync(definition, installedPath, cancellationToken).ConfigureAwait(false))
            {
                modelPath = installedPath;
            }
            else
            {
                SetProgress("Downloading benchmark model", $"Downloading and verifying {definition.DisplayName} temporarily.");
                modelPath = Path.Combine(temporaryDirectory, definition.FileName);
                await _modelManager.DownloadVerifiedAsync(definition, modelPath, cancellationToken).ConfigureAwait(false);
            }

            using IDisposable inferenceLease = await _inferenceGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
            ModelBenchmarkResult result = await BenchmarkAsync(
                Plugin.Instance.Configuration,
                definition,
                modelPath,
                samplePath,
                cancellationToken).ConfigureAwait(false);
            UpdateResult(result);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Subtitle Aligner benchmark failed for {ModelName}", definition.FileName);
            UpdateResult(new ModelBenchmarkResult
            {
                ModelName = definition.FileName,
                ModelDisplayName = definition.DisplayName,
                ModelSizeBytes = definition.SizeBytes,
                Successful = false,
                Message = exception.Message
            });
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not remove the Subtitle Aligner benchmark directory");
            }
        }
    }

    private async Task<ModelBenchmarkResult> BenchmarkAsync(
        PluginConfiguration configuration,
        WhisperModelDefinition definition,
        string modelPath,
        string samplePath,
        CancellationToken cancellationToken)
    {
        Stopwatch loadTimer = Stopwatch.StartNew();
        try
        {
            SetProgress("Loading model", $"Loading {definition.DisplayName} with the configured local engine.");
            await _whisperServer.StartAsync(configuration, modelPath, cancellationToken).ConfigureAwait(false);
            loadTimer.Stop();

            List<double> timings = [];
            for (int run = 1; run <= BenchmarkRuns; run++)
            {
                SetProgress(
                    "Benchmarking throughput",
                    $"Transcribing run {run} of {BenchmarkRuns} with {definition.DisplayName}.");
                Stopwatch timer = Stopwatch.StartNew();
                WhisperTranscript transcript = await _whisperServer
                    .TranscribeAsync(
                        samplePath,
                        "en",
                        translateToEnglish: false,
                        requireWordTimestamps: false,
                        requireVadIntervals: false,
                        bypassCache: true,
                        inferenceProgress: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                timer.Stop();
                IReadOnlyList<WhisperSegment> segments = transcript.Segments;
                if (segments.Count == 0 || segments.All(segment => string.IsNullOrWhiteSpace(segment.Text)))
                {
                    throw new InvalidDataException("The benchmark model returned no transcription.");
                }

                timings.Add(timer.Elapsed.TotalSeconds);
            }

            double median = Median(timings);
            return new ModelBenchmarkResult
            {
                ModelName = definition.FileName,
                ModelDisplayName = definition.DisplayName,
                ModelSizeBytes = definition.SizeBytes,
                LoadSeconds = loadTimer.Elapsed.TotalSeconds,
                InferenceSeconds = median,
                RealTimeFactor = median / SampleDurationSeconds,
                Runs = BenchmarkRuns,
                Successful = true,
                Message = $"Median of {BenchmarkRuns} runs; benchmark only, with no selection or installation changes."
            };
        }
        finally
        {
            await _whisperServer.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task DownloadSampleAsync(string destination, CancellationToken cancellationToken)
    {
        HttpClient client = _httpClientFactory.CreateClient(WhisperModelManager.HttpClientName);
        using HttpResponseMessage response = await client
            .GetAsync(SampleUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != SampleSize)
        {
            throw new InvalidDataException($"The test sample reported an unexpected size ({length} bytes).");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long bytes = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            bytes += read;
            if (bytes > SampleSize)
            {
                throw new InvalidDataException("The test sample is larger than the pinned file.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (bytes != SampleSize || !string.Equals(actualHash, SampleSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The test sample size or SHA-256 does not match the pinned public-domain file.");
        }
    }

    private void SetProgress(string stage, string message)
    {
        lock (_stateGate)
        {
            _status.Running = true;
            _status.Stage = stage;
            _status.Message = message;
        }
    }

    private void UpdateResult(ModelBenchmarkResult result)
    {
        lock (_stateGate)
        {
            List<ModelBenchmarkResult> results = _status.Results
                .Where(existing => !string.Equals(existing.ModelName, result.ModelName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            results.Add(result);
            _status.Results = results
                .OrderBy(existing => WhisperModelCatalog.Find(existing.ModelName).QualityRank)
                .ToArray();
            _status.Running = false;
            _status.Stage = result.Successful ? "Benchmark complete" : "Benchmark failed";
            _status.Message = result.Successful
                ? $"{result.ModelDisplayName}: {result.InferenceSeconds:F1}s median for 11 seconds of audio ({result.RealTimeFactor:F2}× real time). The recommendation and installed model were not changed."
                : $"Could not benchmark {result.ModelDisplayName}: {result.Message}";
            _status.CompletedUtc = DateTime.UtcNow;
        }
    }

    private static HostAssessmentStatus CreateStatus(
        PluginConfiguration configuration,
        string stage,
        string message,
        IReadOnlyList<ModelBenchmarkResult> results)
    {
        HostCapabilities host = InspectHost(configuration);
        IReadOnlyList<WhisperModelDefinition> compatible = GetCompatibleModels(host, configuration);
        WhisperModelDefinition? recommended = compatible.Count == 0
            ? null
            : RecommendModel(compatible, configuration);
        return new HostAssessmentStatus
        {
            Stage = stage,
            Message = message,
            Host = host,
            CompatibleModels = BuildOptions(compatible),
            Results = results,
            RecommendedModelName = recommended?.FileName ?? string.Empty,
            Recommendation = recommended is null
                ? "No managed model is compatible with the detected engine and available memory. Configure a compatible custom engine and model under Advanced paths."
                : BuildRecommendation(host, compatible, recommended, configuration)
        };
    }

    private static IReadOnlyList<WhisperModelDefinition> GetCompatibleModels(
        HostCapabilities host,
        PluginConfiguration configuration)
    {
        bool customEngine = !string.IsNullOrWhiteSpace(configuration.WhisperServerPath);
        bool engineCompatible = customEngine
            || (RuntimeInformation.ProcessArchitecture == Architecture.X64 && Sse42.IsSupported);
        if (!engineCompatible)
        {
            return [];
        }

        return WhisperModelCatalog.AssessmentCandidates
            .Where(candidate => host.AvailableMemoryBytes <= 0
                ? candidate == WhisperModelCatalog.TinyQ8
                : host.AvailableMemoryBytes >= candidate.MinimumAvailableMemoryBytes)
            .ToArray();
    }

    private static WhisperModelDefinition RecommendModel(
        IReadOnlyList<WhisperModelDefinition> compatible,
        PluginConfiguration configuration)
    {
        if (compatible.Count == 0)
        {
            throw new InvalidOperationException("No managed model is compatible with the detected engine and memory constraints.");
        }

        int threads = EffectiveThreadCount(configuration);
        int targetRank = threads <= 2 ? 1 : threads <= 7 ? 2 : 3;
        return compatible
            .Where(candidate => candidate.QualityRank <= targetRank)
            .OrderByDescending(candidate => candidate.QualityRank)
            .FirstOrDefault()
            ?? compatible.OrderBy(candidate => candidate.QualityRank).First();
    }

    private static string BuildRecommendation(
        HostCapabilities host,
        IReadOnlyList<WhisperModelDefinition> compatible,
        WhisperModelDefinition recommended,
        PluginConfiguration configuration)
    {
        string choices = string.Join(", ", compatible.Select(candidate => candidate.DisplayName.Replace("Whisper ", string.Empty, StringComparison.Ordinal)));
        string gpu = host.GpuDeviceDetected
            ? "A GPU device is present, but this bundled engine cannot use it."
            : "The bundled engine is CPU-only; no GPU backend is available.";
        return $"{recommended.DisplayName} is recommended for {EffectiveThreadCount(configuration)} inference threads and the detected memory. Compatible choices: {choices}. {gpu} Model capability tiers are predefined; an optional benchmark measures throughput only.";
    }

    private static IReadOnlyList<CompatibleModelOption> BuildOptions(IReadOnlyList<WhisperModelDefinition> compatible)
    {
        return compatible.Select(candidate =>
        {
            FileInfo installed = new(WhisperModelManager.GetManagedModelPath(candidate));
            return new CompatibleModelOption
            {
                ModelName = candidate.FileName,
                ModelDisplayName = candidate.DisplayName,
                QualityTier = candidate.QualityTier,
                Description = candidate.Description,
                DownloadSizeBytes = candidate.SizeBytes,
                EstimatedWorkingMemoryBytes = candidate.EstimatedWorkingMemoryBytes,
                Installed = installed.Exists && installed.Length == candidate.SizeBytes
            };
        }).ToArray();
    }

    private static void RefreshInstalledFlags(IReadOnlyList<CompatibleModelOption> options)
    {
        foreach (CompatibleModelOption option in options)
        {
            WhisperModelDefinition definition = WhisperModelCatalog.Find(option.ModelName);
            FileInfo installed = new(WhisperModelManager.GetManagedModelPath(definition));
            option.Installed = installed.Exists && installed.Length == definition.SizeBytes;
        }
    }

    private static HostCapabilities InspectHost(PluginConfiguration configuration)
    {
        (long total, long available) = ReadMemory();
        List<string> features = [];
        AddFeature(features, "SSE4.2", Sse42.IsSupported);
        AddFeature(features, "AVX", Avx.IsSupported);
        AddFeature(features, "AVX2", Avx2.IsSupported);
        AddFeature(features, "FMA", Fma.IsSupported);
        AddFeature(features, "ARM AdvSIMD", AdvSimd.IsSupported);
        bool customEngine = !string.IsNullOrWhiteSpace(configuration.WhisperServerPath);
        return new HostCapabilities
        {
            OperatingSystem = RuntimeInformation.OSDescription.Trim(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Processor = ReadProcessorName(),
            LogicalProcessors = Environment.ProcessorCount,
            TotalMemoryBytes = total,
            AvailableMemoryBytes = available,
            CpuFeatures = features,
            GpuDeviceDetected = DetectGpuDevice(),
            InferenceBackend = customEngine
                ? "Custom engine; acceleration capabilities are not introspected"
                : "Bundled portable x64 CPU engine (SSE4.2, no GPU backend)"
        };
    }

    private static int EffectiveThreadCount(PluginConfiguration configuration)
    {
        return configuration.WhisperThreads <= 0
            ? Math.Clamp(Environment.ProcessorCount, 1, 8)
            : Math.Clamp(configuration.WhisperThreads, 1, 32);
    }

    private static string ReadProcessorName()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
                {
                    int separator = line.IndexOf(':', StringComparison.Ordinal);
                    if (separator >= 0)
                    {
                        return line[(separator + 1)..].Trim();
                    }
                }
            }
        }

        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static bool DetectGpuDevice()
    {
        try
        {
            return OperatingSystem.IsLinux()
                && Directory.Exists("/dev/dri")
                && Directory.EnumerateFiles("/dev/dri", "renderD*").Any();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (long Total, long Available) ReadMemory()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            long total = 0;
            long available = 0;
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseMemInfoKilobytes(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseMemInfoKilobytes(line);
                }
            }

            return (total, available);
        }

        long fallback = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (fallback, fallback);
    }

    private static long ParseMemInfoKilobytes(string line)
    {
        string token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? "0";
        return long.TryParse(token, out long kilobytes) ? kilobytes * 1024 : 0;
    }

    private static void AddFeature(List<string> features, string name, bool supported)
    {
        if (supported)
        {
            features.Add(name);
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private static HostAssessmentStatus CopyStatus(HostAssessmentStatus status)
    {
        return new HostAssessmentStatus
        {
            Running = status.Running,
            Stage = status.Stage,
            Message = status.Message,
            Host = new HostCapabilities
            {
                OperatingSystem = status.Host.OperatingSystem,
                Architecture = status.Host.Architecture,
                Processor = status.Host.Processor,
                LogicalProcessors = status.Host.LogicalProcessors,
                TotalMemoryBytes = status.Host.TotalMemoryBytes,
                AvailableMemoryBytes = status.Host.AvailableMemoryBytes,
                CpuFeatures = status.Host.CpuFeatures.ToArray(),
                GpuDeviceDetected = status.Host.GpuDeviceDetected,
                InferenceBackend = status.Host.InferenceBackend
            },
            CompatibleModels = status.CompatibleModels.Select(option => new CompatibleModelOption
            {
                ModelName = option.ModelName,
                ModelDisplayName = option.ModelDisplayName,
                QualityTier = option.QualityTier,
                Description = option.Description,
                DownloadSizeBytes = option.DownloadSizeBytes,
                EstimatedWorkingMemoryBytes = option.EstimatedWorkingMemoryBytes,
                Installed = option.Installed
            }).ToArray(),
            Results = status.Results.Select(result => new ModelBenchmarkResult
            {
                ModelName = result.ModelName,
                ModelDisplayName = result.ModelDisplayName,
                ModelSizeBytes = result.ModelSizeBytes,
                LoadSeconds = result.LoadSeconds,
                InferenceSeconds = result.InferenceSeconds,
                RealTimeFactor = result.RealTimeFactor,
                Runs = result.Runs,
                Successful = result.Successful,
                Message = result.Message
            }).ToArray(),
            RecommendedModelName = status.RecommendedModelName,
            Recommendation = status.Recommendation,
            CompletedUtc = status.CompletedUtc
        };
    }
}
