using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleAligner.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Uses a trusted external whisper-server or owns one loopback-only process for a run.</summary>
public sealed class WhisperServer : IDisposable
{
    private const int SupportedExtensionSchemaMajor = 1;
    private const int MaximumExternalAttempts = 3;
    private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CompletedResponseGracePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] ExternalRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    ];
    private static readonly string[] RequiredErrorCodes =
    [
        "unsupported_timing",
        "word_timestamps_unavailable",
        "vad_intervals_unavailable",
        "model_task_mismatch",
        "invalid_language",
        "capability_downgrade",
        "overloaded",
        "request_cancelled",
        "request_not_found",
        "backend_failure",
        "unsupported_extension_schema_version"
    ];
    /// <summary>The named HTTP client used for loopback inference.</summary>
    public const string HttpClientName = "SubtitleAlignerWhisper";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WhisperServer> _logger;
    private readonly TranscriptionCache _transcriptionCache;
    private Process? _process;
    private Uri? _baseUri;
    private bool _usingExternal;
    private WhisperApiStyle _apiStyle = WhisperApiStyle.WhisperCpp;
    private bool _externalSupportsTranslation = true;
    private PluginConfiguration? _localFallbackConfiguration;
    private PluginConfiguration? _activeConfiguration;

    /// <summary>Gets a user-facing description of the active inference backend.</summary>
    public string BackendDescription => _usingExternal ? "external Whisper server" : "NAS Whisper server";

    /// <summary>Initializes a new instance of the <see cref="WhisperServer"/> class.</summary>
    public WhisperServer(
        IHttpClientFactory httpClientFactory,
        ILogger<WhisperServer> logger,
        TranscriptionCache transcriptionCache)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _transcriptionCache = transcriptionCache;
    }

    /// <summary>Connects to the configured external server or starts the private local server.</summary>
    public async Task StartAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        _activeConfiguration = configuration;
        if (_baseUri is not null && (_usingExternal || _process is { HasExited: false }))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(configuration.ExternalWhisperUrl))
        {
            Uri externalUri = NormalizeExternalUri(configuration.ExternalWhisperUrl);
            ExternalServerInfo? external = await DetectExternalServerAsync(
                externalUri,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            if (external is not null)
            {
                _baseUri = externalUri;
                _usingExternal = true;
                _apiStyle = external.ApiStyle;
                _externalSupportsTranslation = external.SupportsTranslation;
                _localFallbackConfiguration = configuration.FallbackToLocalWhisper ? configuration : null;
                _logger.LogInformation("Subtitle Aligner connected to the configured external whisper-server.");
                return;
            }

            if (!configuration.FallbackToLocalWhisper)
            {
                throw new InvalidOperationException(
                    "The external Whisper server did not respond. Check its address or enable NAS fallback.");
            }

            _logger.LogWarning("The configured external whisper-server is unavailable; using the NAS engine for this run.");
        }

        WhisperModelDefinition selected = WhisperModelManager.GetSelectedModel(configuration);
        string model = ResolveModel(configuration.WhisperModelPath, selected.FileName);
        await StartAsync(configuration, model, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a private server with an explicit model path for a model benchmark.</summary>
    internal async Task StartAsync(
        PluginConfiguration configuration,
        string modelPath,
        CancellationToken cancellationToken)
    {
        _activeConfiguration = configuration;
        if (_process is { HasExited: false })
        {
            return;
        }

        _usingExternal = false;
        _apiStyle = WhisperApiStyle.WhisperCpp;
        _externalSupportsTranslation = true;
        _localFallbackConfiguration = null;

        string executable = ResolveExecutable(configuration.WhisperServerPath);
        int port = GetFreeLoopbackPort();

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(IPAddress.Loopback.ToString());
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelPath);
        string vadModelPath = WhisperModelManager.GetManagedVadModelPath();
        if (File.Exists(vadModelPath))
        {
            startInfo.ArgumentList.Add("--vad-model");
            startInfo.ArgumentList.Add(vadModelPath);
        }

        startInfo.ArgumentList.Add("--threads");
        int threads = configuration.WhisperThreads <= 0
            ? Math.Clamp(Environment.ProcessorCount, 1, 8)
            : Math.Clamp(configuration.WhisperThreads, 1, 32);
        startInfo.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--processors");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--no-gpu");
        startInfo.ArgumentList.Add("--no-fallback");

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger.LogDebug("whisper-server: {Message}", args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger.LogDebug("whisper-server: {Message}", args.Data);
            }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("whisper-server did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _baseUri = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);

        try
        {
            await WaitUntilReadyAsync(process, cancellationToken).ConfigureAwait(false);
            ExternalServerInfo? local = await DetectExternalServerAsync(
                _baseUri,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            if (local?.ApiStyle != WhisperApiStyle.OpenAi)
            {
                throw new InvalidOperationException(
                    "The bundled whisper-server does not expose the required speech-service v1 API.");
            }

            _apiStyle = local.ApiStyle;
            _externalSupportsTranslation = local.SupportsTranslation;
            _logger.LogInformation("Subtitle Aligner started the NAS Whisper speech-service v1 backend.");
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Transcribes one 16 kHz mono WAV sample.</summary>
    internal async Task<WhisperTranscript> TranscribeAsync(
        string wavPath,
        string language,
        bool translateToEnglish,
        bool requireWordTimestamps,
        bool requireVadIntervals,
        bool bypassCache,
        Action<WhisperInferenceProgress>? inferenceProgress,
        CancellationToken cancellationToken)
    {
        PluginConfiguration? fallbackConfiguration = _localFallbackConfiguration;
        if (fallbackConfiguration is null)
        {
            return await TranscribeOnceAsync(
                wavPath,
                language,
                translateToEnglish,
                requireWordTimestamps,
                requireVadIntervals,
                bypassCache,
                inferenceProgress,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ExecuteWithRetriesAsync(
                attempt => TranscribeOnceAsync(
                    wavPath,
                    language,
                    translateToEnglish,
                    requireWordTimestamps,
                    requireVadIntervals,
                    bypassCache,
                    progress => inferenceProgress?.Invoke(progress with
                    {
                        Attempt = attempt,
                        MaximumAttempts = MaximumExternalAttempts
                    }),
                    cancellationToken),
                exception => _usingExternal && ShouldFallbackToLocal(exception, cancellationToken),
                MaximumExternalAttempts,
                async (nextAttempt, exception) =>
                {
                    inferenceProgress?.Invoke(new WhisperInferenceProgress(
                        "retrying_remote",
                        0,
                        null,
                        Attempt: nextAttempt,
                        MaximumAttempts: MaximumExternalAttempts));
                    TimeSpan delay = ExternalRetryDelays[Math.Min(
                        nextAttempt - 2,
                        ExternalRetryDelays.Length - 1)];
                    _logger.LogWarning(
                        exception,
                        "The external Whisper server did not return the current section; retrying remote attempt {Attempt} of {MaximumAttempts} after {DelaySeconds} second(s).",
                        nextAttempt,
                        MaximumExternalAttempts,
                        delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (Exception exception) when (_usingExternal
            && ShouldFallbackToLocal(exception, cancellationToken))
        {
            inferenceProgress?.Invoke(new WhisperInferenceProgress(
                "retrying_local",
                0,
                null));
            await SwitchToLocalAsync(
                fallbackConfiguration,
                $"The external Whisper server failed after {MaximumExternalAttempts} attempts; retrying this audio section once with the NAS engine. {exception.GetType().Name}: {exception.Message}",
                cancellationToken).ConfigureAwait(false);
            return await TranscribeOnceAsync(
                wavPath,
                language,
                translateToEnglish,
                requireWordTimestamps,
                requireVadIntervals,
                bypassCache,
                inferenceProgress,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<WhisperTranscript> TranscribeOnceAsync(
        string wavPath,
        string language,
        bool translateToEnglish,
        bool requireWordTimestamps,
        bool requireVadIntervals,
        bool bypassCache,
        Action<WhisperInferenceProgress>? inferenceProgress,
        CancellationToken cancellationToken)
    {
        if (_baseUri is null || (!_usingExternal && (_process is null || _process.HasExited)))
        {
            throw new InvalidOperationException("whisper-server is not running.");
        }

        await EnsureTranslationBackendAsync(translateToEnglish, cancellationToken).ConfigureAwait(false);
        bool requestDualOutput = translateToEnglish && requireVadIntervals;

        WhisperRouteCapabilities? capabilities = await TryGetRouteCapabilitiesAsync(
                language,
                translateToEnglish,
                requireWordTimestamps,
                requireVadIntervals,
                requestDualOutput,
                cancellationToken)
            .ConfigureAwait(false);
        if (capabilities is { HasExtensionContract: true })
        {
            string task = translateToEnglish ? "translate" : "transcribe";
            if (!capabilities.SupportedTasks.Contains(task, StringComparer.Ordinal))
            {
                throw new WhisperApiException(
                    (int)HttpStatusCode.UnprocessableEntity,
                    "task_unavailable",
                    $"The selected speech route does not support {task}.",
                    string.Join(',', capabilities.SupportedTasks));
            }

            string requiredTiming = requireWordTimestamps ? "word" : "segment";
            if (!capabilities.TimingGranularities.Contains(requiredTiming, StringComparer.Ordinal))
            {
                throw new WhisperApiException(
                    (int)HttpStatusCode.UnprocessableEntity,
                    requireWordTimestamps ? "word_timestamps_unavailable" : "segment_timestamps_unavailable",
                    $"The selected speech route cannot provide required {requiredTiming} timestamps.",
                    string.Join(',', capabilities.TimingGranularities));
            }

            if (requireVadIntervals && capabilities.VadIntervals != true)
            {
                throw new WhisperApiException(
                    (int)HttpStatusCode.UnprocessableEntity,
                    "vad_intervals_unavailable",
                    "The selected speech route cannot provide required VAD intervals.",
                    string.Join(',', capabilities.Limitations));
            }
        }
        else if (requireWordTimestamps || requireVadIntervals)
        {
            throw new WhisperApiException(
                (int)HttpStatusCode.UnprocessableEntity,
                "capability_downgrade",
                "Individual-cue alignment requires contract-v1 timing provenance, but the selected speech route did not advertise it.",
                $"required={(requireWordTimestamps ? "word" : "vad")}; capabilities=unavailable");
        }

        TranscriptionCacheKey? cacheKey = null;
        PluginConfiguration? cacheConfiguration = _activeConfiguration;
        if (cacheConfiguration?.EnableTranscriptionCache == true
            && capabilities is { HasExtensionContract: true }
            && !string.IsNullOrWhiteSpace(capabilities.BuildIdentity))
        {
            try
            {
                cacheKey = await _transcriptionCache.CreateKeyAsync(
                        wavPath,
                        language,
                        translateToEnglish,
                        requireWordTimestamps,
                        requireVadIntervals,
                        capabilities,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!bypassCache)
                {
                    WhisperTranscript? cached = await _transcriptionCache.TryGetAsync(
                            cacheKey,
                            cacheConfiguration,
                            requireWordTimestamps,
                            requireVadIntervals,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (cached is not null)
                    {
                        inferenceProgress?.Invoke(new WhisperInferenceProgress("cache_hit", 1, 0));
                        _logger.LogInformation(
                            "Subtitle Aligner reused cached speech analysis for the current audio section ({Model}, {Backend}).",
                            cached.Metadata.SelectedModel,
                            cached.Metadata.Backend);
                        return cached;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "Subtitle Aligner could not read its transcription cache; inference will continue.");
                cacheKey = null;
            }
        }

        using MultipartFormDataContent form = new();
        await using FileStream stream = File.OpenRead(wavPath);
        using StreamContent audio = new(stream);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", Path.GetFileName(wavPath));
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("0.0"), "temperature");

        string endpoint;
        string? requestId = null;
        bool pollProgress = false;
        if (_apiStyle == WhisperApiStyle.OpenAi)
        {
            if (requestDualOutput && capabilities?.DualOutput != true)
            {
                throw new WhisperApiException(
                    (int)HttpStatusCode.UnprocessableEntity,
                    "capability_downgrade",
                    "Cross-language individual-cue alignment requires paired source and translation segments, but the selected route does not provide dual output.",
                    string.Join(',', capabilities?.Limitations ?? []));
            }

            if (!string.IsNullOrWhiteSpace(language))
            {
                form.Add(new StringContent(language), "language");
            }

            // "auto" is the explicit routing contract used by compatible servers that
            // select different models for transcription and translation.  Sending a
            // configured/default model here can pin a transcription-only model to the
            // translations endpoint.
            form.Add(new StringContent("auto"), "model");
            form.Add(new StringContent(requireWordTimestamps ? "word" : "segment"), "timestamp_granularities[]");
            if (requireVadIntervals)
            {
                form.Add(new StringContent("vad"), "timestamp_granularities[]");
            }

            if (requestDualOutput)
            {
                form.Add(new StringContent("true"), "dual_output");
            }

            if (capabilities is { HasExtensionContract: true })
            {
                requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                form.Add(new StringContent(requestId), "request_id");
                pollProgress = capabilities.Progress == true;
            }

            endpoint = translateToEnglish ? "v1/audio/translations" : "v1/audio/transcriptions";
        }
        else
        {
            form.Add(new StringContent(string.IsNullOrWhiteSpace(language) ? "auto" : language), "language");
            form.Add(new StringContent(translateToEnglish ? "true" : "false"), "translate");
            form.Add(new StringContent(requireWordTimestamps ? "true" : "false"), "token_timestamps");
            form.Add(new StringContent(requireWordTimestamps ? "true" : "false"), "split_on_word");
            endpoint = "inference";
        }

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(_baseUri, endpoint))
        {
            Content = form
        };
        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<HttpResponseMessage> responseTask = client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            requestCancellation.Token);
        Task progressTask = pollProgress
            ? PollRequestProgressAsync(
                client,
                _baseUri,
                requestId!,
                responseTask,
                inferenceProgress,
                requestCancellation.Token)
            : Task.CompletedTask;
        HttpResponseMessage response;
        try
        {
            Task firstCompleted = await Task.WhenAny(responseTask, progressTask).ConfigureAwait(false);
            if (ReferenceEquals(firstCompleted, progressTask))
            {
                await progressTask.ConfigureAwait(false);
            }

            response = await responseTask.ConfigureAwait(false);
            await progressTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            && requestId is not null
            && capabilities?.Cancellation == true)
        {
            await TryCancelRequestAsync(client, _baseUri, requestId).ConfigureAwait(false);
            throw;
        }
        catch
        {
            requestCancellation.Cancel();
            if (requestId is not null && capabilities?.Cancellation == true)
            {
                await TryCancelRequestAsync(client, _baseUri, requestId).ConfigureAwait(false);
            }

            throw;
        }

        try
        {
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw ParseApiError(response.StatusCode, errorBody);
                }

                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                WhisperTranscript transcript = ParseTranscript(responseBody);

                if (requestId is not null
                    && !string.Equals(transcript.Metadata.RequestId, requestId, StringComparison.Ordinal))
                {
                    throw new WhisperApiException(
                        (int)HttpStatusCode.BadGateway,
                        "response_contract_mismatch",
                        "The speech service response request ID does not match the submitted request.",
                        $"expected={requestId}; actual={transcript.Metadata.RequestId}");
                }

                transcript = transcript with
                {
                    Metadata = transcript.Metadata with
                    {
                        RouteCapabilitiesKnown = capabilities is { HasExtensionContract: true },
                        RouteTimingGranularities = capabilities?.TimingGranularities ?? [],
                        RouteVadIntervals = capabilities?.VadIntervals
                    }
                };
                ValidateResponseContract(
                    transcript,
                    translateToEnglish,
                    requireWordTimestamps,
                    requireVadIntervals,
                    requestDualOutput ? capabilities?.DualOutputDecodeCount : null);
                string actualTiming = transcript.Metadata.Timing is { Actual.Count: > 0 } timing
                    ? string.Join(',', timing.Actual)
                    : string.Join(',', transcript.Segments.Select(segment => segment.TimingGranularity).Distinct());
                _logger.LogDebug(
                    "Whisper response: request {RequestId}, endpoint {Endpoint}, operation {Operation}, source language {Language}, detected language {DetectedLanguage}, requested model {RequestedModel}, selected model {SelectedModel}, backend {Backend}, server {ServerVersion}, timing {Timing}, reliability {Reliability}, warnings {Warnings}",
                    transcript.Metadata.RequestId,
                    endpoint,
                    translateToEnglish ? "translate" : "transcribe",
                    string.IsNullOrWhiteSpace(language) ? "auto" : language,
                    transcript.Metadata.DetectedLanguage,
                    _apiStyle == WhisperApiStyle.OpenAi ? "auto" : "configured",
                    transcript.Metadata.SelectedModel,
                    string.IsNullOrWhiteSpace(transcript.Metadata.Backend) ? BackendDescription : transcript.Metadata.Backend,
                    transcript.Metadata.ServerVersion,
                    actualTiming,
                    transcript.Metadata.Timing?.Reliability ?? "unreported",
                    string.Join(',', transcript.Metadata.Warnings));
                if (cacheKey is not null && cacheConfiguration is not null)
                {
                    try
                    {
                        await _transcriptionCache.StoreAsync(
                                cacheKey,
                                transcript,
                                cacheConfiguration,
                                requireWordTimestamps,
                                requireVadIntervals,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _logger.LogWarning(exception, "Subtitle Aligner could not write its transcription cache; the result remains usable.");
                    }
                }

                return transcript;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            && requestId is not null
            && capabilities?.Cancellation == true)
        {
            await TryCancelRequestAsync(client, _baseUri, requestId).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task PollRequestProgressAsync(
        HttpClient client,
        Uri baseUri,
        string requestId,
        Task<HttpResponseMessage> responseTask,
        Action<WhisperInferenceProgress>? progress,
        CancellationToken cancellationToken)
    {
        double previousFraction = -1;
        while (!responseTask.IsCompleted)
        {
            await Task.Delay(ProgressPollInterval, cancellationToken).ConfigureAwait(false);
            if (responseTask.IsCompleted)
            {
                return;
            }

            using HttpRequestMessage request = new(
                HttpMethod.Get,
                new Uri(baseUri, $"v1/audio/requests/{Uri.EscapeDataString(requestId)}"));
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Registration can race the first status request. The inference response
                // remains authoritative, so retry while it is still in flight.
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw ParseApiError(response.StatusCode, body);
            }

            WhisperInferenceProgress? update = ParseRequestProgress(body, requestId, ref previousFraction);
            if (update is not null)
            {
                progress?.Invoke(update);
                if (string.Equals(update.State, "completed", StringComparison.Ordinal))
                {
                    progress?.Invoke(update with
                    {
                        Phase = "receiving_result",
                        EtaSeconds = null
                    });
                    await WaitForResponseDeliveryAsync(
                        responseTask,
                        requestId,
                        CompletedResponseGracePeriod,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    internal static async Task WaitForResponseDeliveryAsync(
        Task responseTask,
        string requestId,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        try
        {
            await responseTask.WaitAsync(gracePeriod, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new WhisperApiException(
                (int)HttpStatusCode.GatewayTimeout,
                "response_delivery_timeout",
                "The speech service completed inference but did not deliver its response promptly.",
                requestId,
                exception);
        }
    }

    internal static WhisperInferenceProgress? ParseRequestProgress(
        string responseBody,
        string requestId,
        ref double previousFraction)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        ValidateExtensionSchemaVersion(GetOptionalString(root, "schema_version"));
        string objectType = GetOptionalString(root, "object");
        string actualRequestId = GetOptionalString(root, "request_id");
        string state = GetOptionalString(root, "state");
        string phase = GetOptionalString(root, "phase");
        double? completedWork = GetOptionalDouble(root, "completed_work");
        double? totalWork = GetOptionalDouble(root, "total_work");
        double? fraction = GetOptionalDouble(root, "fraction_completed");
        double? eta = GetOptionalDouble(root, "eta_seconds");
        bool? cancellationRequested = GetOptionalBoolean(root, "cancellation_requested");
        bool valid = string.Equals(objectType, "audio.request", StringComparison.Ordinal)
            && string.Equals(actualRequestId, requestId, StringComparison.Ordinal)
            && state is "queued" or "running" or "completed" or "cancelled" or "failed"
            && !string.IsNullOrWhiteSpace(phase)
            && (completedWork is null or >= 0)
            && (totalWork is null or >= 0)
            && (completedWork is null || totalWork is null || totalWork.Value >= completedWork.Value)
            && (eta is null or >= 0)
            && (fraction is null or >= 0 and <= 1)
            && (fraction is null || fraction.Value >= previousFraction)
            && cancellationRequested is not null;
        if (!valid)
        {
            throw new WhisperApiException(
                (int)HttpStatusCode.BadGateway,
                "response_contract_mismatch",
                "The speech service returned an invalid request-progress response.",
                requestId);
        }

        if (fraction is null)
        {
            return null;
        }

        previousFraction = fraction.Value;
        return new WhisperInferenceProgress(
            phase,
            previousFraction,
            eta,
            state);
    }

    internal static async Task<WhisperTranscript> ReadEventStreamAsync(
        HttpContent content,
        string requestId,
        Action<WhisperInferenceProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        string eventName = string.Empty;
        StringBuilder data = new();
        double previousProcessedSeconds = -1;
        double previousFraction = -1;
        double? expectedTotalSeconds = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                WhisperTranscript? completed = ProcessEventStreamMessage(
                    eventName,
                    data,
                    requestId,
                    progress,
                    ref previousProcessedSeconds,
                    ref previousFraction,
                    ref expectedTotalSeconds);
                if (completed is not null)
                {
                    return completed;
                }

                eventName = string.Empty;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line["data:".Length..].TrimStart());
            }
        }

        WhisperTranscript? terminal = ProcessEventStreamMessage(
            eventName,
            data,
            requestId,
            progress,
            ref previousProcessedSeconds,
            ref previousFraction,
            ref expectedTotalSeconds);
        return terminal ?? throw new WhisperApiException(
            (int)HttpStatusCode.BadGateway,
            "response_contract_mismatch",
            "The speech-service event stream ended without a terminal result.",
            requestId);
    }

    private static WhisperTranscript? ProcessEventStreamMessage(
        string eventName,
        StringBuilder data,
        string requestId,
        Action<WhisperInferenceProgress>? progress,
        ref double previousProcessedSeconds,
        ref double previousFraction,
        ref double? expectedTotalSeconds)
    {
        if (data.Length == 0)
        {
            return null;
        }

        string payload = data.ToString();
        if (string.Equals(eventName, "result", StringComparison.Ordinal))
        {
            return ParseTranscript(payload);
        }

        if (string.Equals(eventName, "error", StringComparison.Ordinal))
        {
            throw ParseApiError(HttpStatusCode.BadGateway, payload);
        }

        if (!string.Equals(eventName, "progress", StringComparison.Ordinal))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        ValidateExtensionSchemaVersion(GetOptionalString(root, "schema_version"));
        string actualRequestId = GetOptionalString(root, "request_id");
        string phase = GetOptionalString(root, "phase");
        double? processed = GetOptionalDouble(root, "processed_audio_seconds");
        double? total = GetOptionalDouble(root, "total_audio_seconds");
        double? fraction = GetOptionalDouble(root, "fraction_completed");
        double? eta = GetOptionalDouble(root, "eta_seconds");
        bool valid = string.Equals(actualRequestId, requestId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(phase)
            && processed is >= 0
            && total >= processed
            && fraction is >= 0 and <= 1
            && (eta is null or >= 0)
            && processed!.Value >= previousProcessedSeconds
            && fraction!.Value >= previousFraction
            && (expectedTotalSeconds is null || Math.Abs(total!.Value - expectedTotalSeconds.Value) <= 0.001);
        if (!valid)
        {
            throw new WhisperApiException(
                (int)HttpStatusCode.BadGateway,
                "response_contract_mismatch",
                "The speech service returned an invalid progress event.",
                requestId);
        }

        previousProcessedSeconds = processed!.Value;
        previousFraction = fraction!.Value;
        expectedTotalSeconds ??= total!.Value;
        progress?.Invoke(new WhisperInferenceProgress(
            phase,
            previousFraction,
            eta));
        return null;
    }

    private async Task TryCancelRequestAsync(HttpClient client, Uri baseUri, string requestId)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            using HttpRequestMessage request = new(
                HttpMethod.Delete,
                new Uri(baseUri, $"v1/audio/requests/{Uri.EscapeDataString(requestId)}"));
            using HttpResponseMessage response = await client
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                _logger.LogWarning(
                    "Speech-service cancellation for request {RequestId} returned HTTP {StatusCode}.",
                    requestId,
                    (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Speech-service cancellation for request {RequestId} could not be confirmed.",
                requestId);
        }
    }

    internal static async Task<T> ExecuteWithFallbackAsync<T>(
        Func<Task<T>> operation,
        Func<Exception, bool> shouldFallback,
        Func<Exception, Task> activateFallback)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (shouldFallback(exception))
        {
            await activateFallback(exception).ConfigureAwait(false);
            return await operation().ConfigureAwait(false);
        }
    }

    internal static async Task<T> ExecuteWithRetriesAsync<T>(
        Func<int, Task<T>> operation,
        Func<Exception, bool> shouldRetry,
        int maximumAttempts,
        Func<int, Exception, Task> beforeRetry)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(attempt).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < maximumAttempts && shouldRetry(exception))
            {
                await beforeRetry(attempt + 1, exception).ConfigureAwait(false);
            }
        }
    }

    internal static bool ShouldFallbackToLocal(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (exception is HttpRequestException or TaskCanceledException)
        {
            return true;
        }

        if (exception is not WhisperApiException apiException
            || apiException.Code is "timing_granularity_downgraded"
                or "response_contract_mismatch"
                or "missing_extension_schema_version"
                or "unsupported_extension_schema_version"
                or "invalid_capability_response")
        {
            return false;
        }

        return apiException.StatusCode is 429 or 502 or 503 or 504
            || apiException.Code is "overloaded" or "backend_failure";
    }

    private async Task SwitchToLocalAsync(
        PluginConfiguration configuration,
        string reason,
        CancellationToken cancellationToken)
    {
        WhisperModelDefinition selected = WhisperModelManager.GetSelectedModel(configuration);
        string model = ResolveModel(configuration.WhisperModelPath, selected.FileName);
        _logger.LogWarning("{Reason}", reason);
        _baseUri = null;
        _usingExternal = false;
        await StartAsync(configuration, model, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateResponseContract(
        WhisperTranscript transcript,
        bool translateToEnglish,
        bool requireWordTimestamps,
        bool requireVadIntervals = false,
        int? expectedDecodeCount = null)
    {
        WhisperResponseMetadata metadata = transcript.Metadata;
        if (!metadata.HasExtensionMetadata)
        {
            if (requireWordTimestamps || requireVadIntervals)
            {
                throw new WhisperApiException(
                    (int)HttpStatusCode.BadGateway,
                    "capability_downgrade",
                    "The speech service returned precision timing without contract-v1 provenance.",
                    $"required={(requireWordTimestamps ? "word" : "vad")}; metadata=legacy");
            }

            return;
        }

        ValidateExtensionSchemaVersion(metadata.ExtensionSchemaVersion);

        string expectedTask = translateToEnglish ? "translate" : "transcribe";
        if (!string.Equals(metadata.Task, expectedTask, StringComparison.Ordinal))
        {
            throw new WhisperApiException(
                (int)HttpStatusCode.BadGateway,
                "response_contract_mismatch",
                $"The speech service returned task '{metadata.Task}' for a {expectedTask} request.",
                metadata.RequestId);
        }

        WhisperTimingProvenance? timing = metadata.Timing;
        if (requireVadIntervals
            && (timing?.Actual.Contains("vad", StringComparer.Ordinal) != true
                || timing.Channels?.TryGetValue("vad", out WhisperTimingChannelProvenance? vadChannel) != true
                || vadChannel is null
                || !string.Equals(vadChannel.Basis, "vad", StringComparison.Ordinal)))
        {
            string actual = timing is null ? "unreported" : string.Join(',', timing.Actual);
            throw new WhisperApiException(
                (int)HttpStatusCode.BadGateway,
                "timing_granularity_downgraded",
                "The speech service did not satisfy the requested VAD-timing contract.",
                $"reported={actual}; required=vad");
        }

        if (requireWordTimestamps)
        {
            bool reportsWordTiming = timing?.Actual.Contains("word", StringComparer.Ordinal) == true;
            bool reportsEligibleWordBasis = timing?.Channels?.TryGetValue(
                "word",
                out WhisperTimingChannelProvenance? wordChannel) == true
                && wordChannel.Basis is "decoder_word" or "forced_alignment";
            bool containsWordTiming = transcript.Segments.Any(segment =>
                segment.TimingGranularity == WhisperTimingGranularity.Word);
            if (!reportsWordTiming || !reportsEligibleWordBasis || !containsWordTiming)
            {
                string actual = timing is null ? "unreported" : string.Join(',', timing.Actual);
                throw new WhisperApiException(
                    (int)HttpStatusCode.BadGateway,
                    "timing_granularity_downgraded",
                    "The speech service did not satisfy the requested word-timing contract.",
                    $"reported={actual}; parsed_word_timing={containsWordTiming}");
            }
        }

        bool timingBasisValid = timing is not null
            && timing.Requested.Count > 0
            && timing.Actual.Count > 0
            && timing.Basis is "decoder_word" or "decoder_segment" or "forced_alignment" or "vad"
            && timing.Reliable is not null
            && !string.IsNullOrWhiteSpace(timing.Reliability)
            && timing.Channels is { Count: > 0 }
            && timing.Actual.All(actual =>
                timing.Channels.TryGetValue(actual, out WhisperTimingChannelProvenance? channel)
                && IsValidTimingChannel(channel));
        bool languageConfidenceValid = metadata.DetectedLanguageProbability is >= 0 and <= 1
            ? !string.IsNullOrWhiteSpace(metadata.DetectedLanguageProbabilityBasis)
            : metadata.DetectedLanguageProbability is null
                && metadata.Warnings.Contains("language_confidence_unavailable", StringComparer.Ordinal);
        bool detectedLanguageValid = !string.IsNullOrWhiteSpace(metadata.DetectedLanguage)
            || metadata.Warnings.Contains("language_confidence_unavailable", StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(metadata.RequestId)
            || !string.Equals(metadata.RequestedModel, "auto", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(metadata.SelectedModel)
            || string.IsNullOrWhiteSpace(metadata.Backend)
            || string.IsNullOrWhiteSpace(metadata.ServerVersion)
            || string.IsNullOrWhiteSpace(metadata.BuildIdentity)
            || string.IsNullOrWhiteSpace(metadata.RequestedLanguage)
            || !detectedLanguageValid
            || string.IsNullOrWhiteSpace(metadata.EffectiveLanguage)
            || !languageConfidenceValid
            || metadata.AudioDurationSeconds is null or < 0
            || metadata.QueueSeconds is null or < 0
            || metadata.PreparationSeconds is null or < 0
            || metadata.InferenceSeconds is null or < 0
            || metadata.ColdStartSeconds is null or < 0
            || metadata.InferenceIncludesPreparation is null
            || metadata.RealtimeFactor is null or < 0
            || string.IsNullOrWhiteSpace(metadata.CacheType)
            || metadata.CacheHit is null
            || metadata.DecodeCount is null or < 1
            || (expectedDecodeCount.HasValue && metadata.DecodeCount != expectedDecodeCount)
            || !timingBasisValid)
        {
            throw new WhisperApiException(
                (int)HttpStatusCode.BadGateway,
                "response_contract_mismatch",
                "The speech service response is missing required contract v1 provenance.",
                metadata.RequestId);
        }
    }

    private static bool IsValidTimingChannel(WhisperTimingChannelProvenance channel) =>
        channel.Basis is "decoder_word" or "decoder_segment" or "forced_alignment" or "vad"
        && channel.Reliable is not null
        && !string.IsNullOrWhiteSpace(channel.Reliability);

    private async Task<WhisperRouteCapabilities?> TryGetRouteCapabilitiesAsync(
        string language,
        bool translateToEnglish,
        bool requireWordTimestamps,
        bool requireVadIntervals,
        bool requireDualOutput,
        CancellationToken cancellationToken)
    {
        if (_apiStyle != WhisperApiStyle.OpenAi || _baseUri is null)
        {
            return null;
        }

        string task = translateToEnglish ? "translate" : "transcribe";
        string relative = $"v1/audio/capabilities?task={task}&model=auto";
        if (!string.IsNullOrWhiteSpace(language))
        {
            relative += $"&language={Uri.EscapeDataString(language)}";
        }

        List<string> requiredTimingGranularities = [requireWordTimestamps ? "word" : "segment"];
        if (requireVadIntervals)
        {
            requiredTimingGranularities.Add("vad");
        }

        relative += $"&timestamp_granularities={Uri.EscapeDataString(string.Join(',', requiredTimingGranularities))}";
        if (requireDualOutput)
        {
            relative += "&dual_output=true";
        }

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await client
            .GetAsync(new Uri(_baseUri, relative), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ParseApiError(response.StatusCode, responseBody);
        }

        try
        {
            WhisperRouteCapabilities capabilities = ParseCapabilities(responseBody);
            ValidateCapabilitiesContract(capabilities, task);

            _logger.LogDebug(
                "Whisper route capability: operation {Operation}, source language {Language}, requested model {RequestedModel}, selected model {SelectedModel}, backend {Backend}, timings {Timings}, VAD {Vad}, limitations {Limitations}",
                task,
                string.IsNullOrWhiteSpace(language) ? "auto" : language,
                capabilities.RequestedModel,
                capabilities.SelectedModel,
                capabilities.Backend,
                string.Join(',', capabilities.TimingGranularities),
                capabilities.VadIntervals,
                string.Join(',', capabilities.Limitations));
            return capabilities;
        }
        catch (JsonException exception)
        {
            throw new WhisperApiException(
                (int)HttpStatusCode.BadGateway,
                "invalid_capability_response",
                "The speech service returned an invalid capability response.",
                exception.Message);
        }
    }

    /// <summary>Stops the private server.</summary>
    public async Task StopAsync()
    {
        Process? process = Interlocked.Exchange(ref _process, null);
        _baseUri = null;
        _usingExternal = false;
        _apiStyle = WhisperApiStyle.WhisperCpp;
        _externalSupportsTranslation = true;
        _localFallbackConfiguration = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the checks.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Process? process = Interlocked.Exchange(ref _process, null);
        _baseUri = null;
        _usingExternal = false;
        _apiStyle = WhisperApiStyle.WhisperCpp;
        _externalSupportsTranslation = true;
        _localFallbackConfiguration = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            process.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static string PluginDirectory => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        ?? Environment.CurrentDirectory;

    /// <summary>Checks whether a trusted external whisper-server is reachable.</summary>
    public async Task<WhisperServerTestResult> TestExternalAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            Uri uri = NormalizeExternalUri(url);
            ExternalServerInfo? server = await DetectExternalServerAsync(
                uri,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            bool reachable = server is not null;
            return new WhisperServerTestResult
            {
                Reachable = reachable,
                Message = reachable
                    ? server!.ApiStyle == WhisperApiStyle.OpenAi
                        ? server.SupportsTranslation
                            ? "Connected to an OpenAI-compatible Whisper server with transcription and translation support."
                            : "Connected to an OpenAI-compatible Whisper server. Transcription is available; speech-to-English translation requires NAS fallback."
                        : "Connected to a whisper.cpp server. Subtitle audio can be sent to it."
                    : "No successful response was received within five seconds."
            };
        }
        catch (ArgumentException exception)
        {
            return new WhisperServerTestResult { Reachable = false, Message = exception.Message };
        }
        catch (HttpRequestException)
        {
            return new WhisperServerTestResult
            {
                Reachable = false,
                Message = "The server could not be reached. Check its address, port, and firewall."
            };
        }
    }

    private static Uri NormalizeExternalUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Enter an absolute HTTP or HTTPS URL without credentials, a query, or a fragment.",
                nameof(value));
        }

        UriBuilder builder = new(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/"
        };
        return builder.Uri;
    }

    private async Task<ExternalServerInfo?> DetectExternalServerAsync(
        Uri baseUri,
        TimeSpan timeoutValue,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutValue);
        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage modelsResponse = await client
                .GetAsync(new Uri(baseUri, "v1/models"), timeout.Token)
                .ConfigureAwait(false);
            if (modelsResponse.IsSuccessStatusCode)
            {
                bool supportsTranslation = await SupportsOpenAiTranslationAsync(client, baseUri, timeout.Token)
                    .ConfigureAwait(false);
                return new ExternalServerInfo(WhisperApiStyle.OpenAi, supportsTranslation);
            }

            using HttpResponseMessage response = await client.GetAsync(baseUri, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new ExternalServerInfo(WhisperApiStyle.WhisperCpp, true)
                : null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task EnsureTranslationBackendAsync(bool translateToEnglish, CancellationToken cancellationToken)
    {
        if (!translateToEnglish || _apiStyle != WhisperApiStyle.OpenAi || _externalSupportsTranslation)
        {
            return;
        }

        PluginConfiguration? configuration = _localFallbackConfiguration;
        if (configuration is null)
        {
            throw new InvalidOperationException(
                "The external Whisper server does not support speech-to-English translation. Enable NAS fallback or use a server that provides /v1/audio/translations.");
        }

        await SwitchToLocalAsync(
            configuration,
            "The external Whisper server does not support translation; using the NAS engine for this subtitle track and the remainder of the run.",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SupportsOpenAiTranslationAsync(
        HttpClient client,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        using MultipartFormDataContent probe = new();
        using HttpResponseMessage response = await client
            .PostAsync(new Uri(baseUri, "v1/audio/translations"), probe, cancellationToken)
            .ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound
            && response.StatusCode != HttpStatusCode.MethodNotAllowed;
    }

    private static string ResolveExecutable(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath.Trim();
        }

        string bundled = Path.Combine(PluginDirectory, "whisper-server");
        if (!File.Exists(bundled))
        {
            return "whisper-server";
        }

        string engineDirectory = WhisperModelCatalog.EngineDirectory;
        Directory.CreateDirectory(engineDirectory);
        string runnable = Path.Combine(engineDirectory, "whisper-server");
        File.Copy(bundled, runnable, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                runnable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return runnable;
    }

    private static string ResolveModel(string configuredPath, params string[] bundledNames)
    {
        string? resolved = ResolveOptionalModel(configuredPath, bundledNames);
        return resolved ?? throw new FileNotFoundException("No Whisper model was configured or bundled with the plugin.");
    }

    private static string? ResolveOptionalModel(string configuredPath, params string[] bundledNames)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string path = configuredPath.Trim();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The configured model file does not exist.", path);
            }

            return path;
        }

        return bundledNames
            .Select(name => Path.Combine(WhisperModelCatalog.ModelsDirectory, name))
            .Concat(bundledNames.Select(name => Path.Combine(PluginDirectory, name)))
            .FirstOrDefault(File.Exists);
    }

    private static int GetFreeLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task WaitUntilReadyAsync(Process process, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        while (!timeout.Token.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"whisper-server exited with code {process.ExitCode} while loading the model.");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(_baseUri, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The model is still loading and the listener is not ready.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Retry until the overall startup timeout expires.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException("whisper-server did not become ready within two minutes.");
    }

    internal static IReadOnlyList<WhisperSegment> ParseSegments(string json) => ParseTranscript(json).Segments;

    internal static WhisperTranscript ParseTranscript(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        List<WhisperSegment> segments = [];

        if (TryAppendDualOutputSegments(root, segments))
        {
            return new WhisperTranscript(
                segments,
                ParseResponseMetadata(root),
                ParseDecoderSegments(root));
        }

        if (root.TryGetProperty("words", out JsonElement openAiWords)
            && openAiWords.ValueKind == JsonValueKind.Array)
        {
            AppendOpenAiWords(openAiWords, segments);
            if (segments.Count > 0)
            {
                return new WhisperTranscript(segments, ParseResponseMetadata(root));
            }
        }

        if (root.TryGetProperty("segments", out JsonElement openAiSegments)
            && openAiSegments.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement segment in openAiSegments.EnumerateArray())
            {
                if (segment.TryGetProperty("words", out JsonElement words)
                    && words.ValueKind == JsonValueKind.Array)
                {
                    int wordCount = AppendOpenAiWords(
                        words,
                        segments,
                        GetOptionalDouble(segment, "avg_logprob"),
                        GetOptionalDouble(segment, "no_speech_prob"));

                    if (wordCount > 0)
                    {
                        continue;
                    }
                }

                if (segment.TryGetProperty("start", out JsonElement start)
                    && segment.TryGetProperty("end", out JsonElement end)
                    && segment.TryGetProperty("text", out JsonElement text))
                {
                    segments.Add(new WhisperSegment(
                        start.GetDouble(),
                        end.GetDouble(),
                        text.GetString() ?? string.Empty,
                        WhisperTimingGranularity.Segment,
                        GetOptionalDouble(segment, "avg_logprob"),
                        GetOptionalDouble(segment, "no_speech_prob"),
                        ParseDecoderTokenProbabilities(segment)));
                }
            }

            return new WhisperTranscript(segments, ParseResponseMetadata(root));
        }

        if (root.TryGetProperty("transcription", out JsonElement transcription)
            && transcription.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement segment in transcription.EnumerateArray())
            {
                if (!segment.TryGetProperty("offsets", out JsonElement offsets)
                    || !offsets.TryGetProperty("from", out JsonElement from)
                    || !offsets.TryGetProperty("to", out JsonElement to)
                    || !segment.TryGetProperty("text", out JsonElement text))
                {
                    continue;
                }

                string transcriptText = text.GetString() ?? string.Empty;
                WhisperTimingGranularity granularity = transcriptText
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Length <= 1
                        ? WhisperTimingGranularity.Token
                        : WhisperTimingGranularity.Segment;
                segments.Add(new WhisperSegment(
                    from.GetDouble() / 1000,
                    to.GetDouble() / 1000,
                    transcriptText,
                    granularity));
            }
        }

        return new WhisperTranscript(segments, ParseResponseMetadata(root));
    }

    private static IReadOnlyList<WhisperSegment> ParseDecoderSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out JsonElement segments)
            || segments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<WhisperSegment> result = [];
        foreach (JsonElement segment in segments.EnumerateArray())
        {
            double? start = GetOptionalDouble(segment, "start");
            double? end = GetOptionalDouble(segment, "end");
            string text = GetOptionalString(segment, "text");
            if (!start.HasValue
                || !end.HasValue
                || end.Value < start.Value
                || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            result.Add(new WhisperSegment(
                start.Value,
                end.Value,
                text,
                WhisperTimingGranularity.Segment,
                GetOptionalDouble(segment, "avg_logprob"),
                GetOptionalDouble(segment, "no_speech_prob"),
                ParseDecoderTokenProbabilities(segment)));
        }

        return result;
    }

    private static bool TryAppendDualOutputSegments(JsonElement root, ICollection<WhisperSegment> destination)
    {
        if (!root.TryGetProperty("source_segments", out JsonElement sourceSegments)
            || sourceSegments.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("translation_segments", out JsonElement translationSegments)
            || translationSegments.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        Dictionary<string, (double Start, double End)> sourceTimings = new(StringComparer.Ordinal);
        foreach (JsonElement segment in sourceSegments.EnumerateArray())
        {
            string segmentId = GetOptionalString(segment, "segment_id");
            double? start = GetOptionalDouble(segment, "start");
            double? end = GetOptionalDouble(segment, "end");
            if (string.IsNullOrWhiteSpace(segmentId)
                || !start.HasValue
                || !end.HasValue
                || end.Value < start.Value
                || !sourceTimings.TryAdd(segmentId, (start.Value, end.Value)))
            {
                throw new JsonException("The dual-output source segments are invalid or contain duplicate IDs.");
            }
        }

        HashSet<string> translatedIds = new(StringComparer.Ordinal);
        foreach (JsonElement segment in translationSegments.EnumerateArray())
        {
            string segmentId = GetOptionalString(segment, "segment_id");
            string text = GetOptionalString(segment, "text");
            if (string.IsNullOrWhiteSpace(segmentId)
                || !translatedIds.Add(segmentId)
                || !sourceTimings.TryGetValue(segmentId, out (double Start, double End) timing))
            {
                throw new JsonException("The dual-output translation segments do not pair exactly with the source segments.");
            }

            destination.Add(new WhisperSegment(
                timing.Start,
                timing.End,
                text,
                WhisperTimingGranularity.Segment));
        }

        if (sourceTimings.Count != translatedIds.Count)
        {
            throw new JsonException("The dual-output source and translation segment ID sets do not match.");
        }

        return true;
    }

    internal static WhisperRouteCapabilities ParseCapabilities(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement limits = root.TryGetProperty("limits", out JsonElement limitsValue)
            && limitsValue.ValueKind == JsonValueKind.Object
                ? limitsValue
                : default;
        string objectType = GetOptionalString(root, "object");
        return new WhisperRouteCapabilities(
            string.Equals(objectType, "audio.route.capabilities", StringComparison.Ordinal),
            objectType,
            GetOptionalString(root, "schema_version"),
            GetOptionalString(root, "task"),
            GetOptionalString(root, "requested_model"),
            GetOptionalString(root, "selected_model"),
            GetOptionalString(root, "backend"),
            GetOptionalBoolean(root, "model_files_cached"),
            GetStringArray(root, "supported_tasks"),
            GetStringArray(root, "timing_granularities"),
            GetOptionalBoolean(root, "vad_intervals"),
            GetStringArray(root, "supported_languages"),
            GetOptionalBoolean(root, "automatic_language_detection"),
            GetOptionalString(root, "effective_language"),
            limits.ValueKind == JsonValueKind.Object ? GetOptionalInt64(limits, "maximum_upload_bytes") : null,
            limits.ValueKind == JsonValueKind.Object ? GetOptionalDouble(limits, "maximum_audio_duration_seconds") : null,
            GetStringArray(root, "limitations"),
            GetOptionalBoolean(root, "configuration_can_change"),
            GetOptionalString(root, "build_identity"),
            GetStringArray(root, "timing_bases"),
            GetStringArray(root, "response_formats"),
            GetNestedOptionalBoolean(root, "optional_features", "dual_output"),
            GetNestedOptionalInt32(root, "optional_features", "dual_output_decode_count"),
            GetNestedOptionalBoolean(root, "optional_features", "progress"),
            GetNestedOptionalBoolean(root, "optional_features", "cancellation"),
            GetNestedOptionalBoolean(root, "confidence_evidence", "word_probability"),
            GetNestedOptionalBoolean(root, "confidence_evidence", "decoder_token_probabilities"),
            GetNestedOptionalBoolean(root, "confidence_evidence", "segment_average_log_probability"),
            GetNestedOptionalBoolean(root, "confidence_evidence", "segment_no_speech_probability"),
            GetNestedOptionalBoolean(root, "confidence_evidence", "detected_language_probability"),
            GetStringArray(root, "error_codes"));
    }

    internal static void ValidateCapabilitiesContract(
        WhisperRouteCapabilities capabilities,
        string expectedTask)
    {
        ValidateExtensionSchemaVersion(capabilities.ExtensionSchemaVersion);
        if (!capabilities.HasExtensionContract
            || !string.Equals(capabilities.Task, expectedTask, StringComparison.Ordinal)
            || !string.Equals(capabilities.RequestedModel, "auto", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(capabilities.SelectedModel)
            || string.IsNullOrWhiteSpace(capabilities.Backend)
            || string.IsNullOrWhiteSpace(capabilities.BuildIdentity)
            || capabilities.ModelFilesCached is null
            || !capabilities.SupportedTasks.Contains(expectedTask, StringComparer.Ordinal)
            || !capabilities.TimingGranularities.Contains("segment", StringComparer.Ordinal)
            || capabilities.TimingBases is not { Count: > 0 }
            || capabilities.TimingBases.Any(basis => basis is not ("decoder_word" or "decoder_segment" or "forced_alignment" or "vad"))
            || capabilities.VadIntervals is null
            || capabilities.SupportedLanguages.Count == 0
            || capabilities.AutomaticLanguageDetection is null
            || capabilities.ResponseFormats?.Contains("verbose_json", StringComparer.Ordinal) != true
            || capabilities.DualOutput is null
            || capabilities.DualOutputDecodeCount is null or < 1
            || capabilities.Progress is null
            || capabilities.Cancellation is null
            || capabilities.WordProbability is null
            || capabilities.DecoderTokenProbabilities is null
            || capabilities.SegmentAverageLogProbability is null
            || capabilities.SegmentNoSpeechProbability is null
            || capabilities.DetectedLanguageProbability is null
            || capabilities.ErrorCodes is null
            || RequiredErrorCodes.Any(code => !capabilities.ErrorCodes.Contains(code, StringComparer.Ordinal))
            || capabilities.MaximumUploadBytes is null or < 0
            || capabilities.MaximumAudioDurationSeconds is null or < 0
            || capabilities.ConfigurationCanChange is null)
        {
            throw new JsonException("The capability response does not satisfy speech-service contract v1.");
        }
    }

    internal static WhisperApiException ParseApiError(HttpStatusCode statusCode, string responseBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    string legacyMessage = error.GetString() ?? "The speech service rejected the request.";
                    return new WhisperApiException((int)statusCode, "speech_service_error", legacyMessage, string.Empty);
                }

                if (error.ValueKind == JsonValueKind.Object)
                {
                    string code = GetOptionalString(error, "code");
                    string message = GetOptionalString(error, "message");
                    return new WhisperApiException(
                        (int)statusCode,
                        string.IsNullOrWhiteSpace(code) ? "speech_service_error" : code,
                        string.IsNullOrWhiteSpace(message) ? "The speech service rejected the request." : message,
                        error.GetRawText());
                }
            }
        }
        catch (JsonException)
        {
            // Preserve compatibility with services that return plain-text errors.
        }

        string fallback = string.IsNullOrWhiteSpace(responseBody)
            ? "The speech service rejected the request without an error message."
            : responseBody.Trim();
        return new WhisperApiException((int)statusCode, "speech_service_error", fallback, string.Empty);
    }

    private static WhisperResponseMetadata ParseResponseMetadata(JsonElement root)
    {
        JsonElement extension = root.TryGetProperty("x_whisper_server", out JsonElement extensionValue)
            && extensionValue.ValueKind == JsonValueKind.Object
                ? extensionValue
                : default;
        bool hasExtension = extension.ValueKind == JsonValueKind.Object;
        WhisperTimingProvenance? timing = null;
        if (hasExtension
            && extension.TryGetProperty("timing", out JsonElement timingValue)
            && timingValue.ValueKind == JsonValueKind.Object)
        {
            timing = new WhisperTimingProvenance(
                GetStringArray(timingValue, "requested"),
                GetStringArray(timingValue, "actual"),
                GetOptionalString(timingValue, "basis"),
                GetOptionalBoolean(timingValue, "reliable"),
                GetOptionalString(timingValue, "reliability"),
                GetTimingChannels(timingValue));
        }

        string cacheType = string.Empty;
        bool? cacheHit = null;
        if (hasExtension
            && extension.TryGetProperty("cache", out JsonElement cache)
            && cache.ValueKind == JsonValueKind.Object)
        {
            cacheType = GetOptionalString(cache, "type");
            cacheHit = GetOptionalBoolean(cache, "hit");
        }

        List<WhisperSpeechInterval> intervals = [];
        if (root.TryGetProperty("speech_segments", out JsonElement speechSegments)
            && speechSegments.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement interval in speechSegments.EnumerateArray())
            {
                double? start = GetOptionalDouble(interval, "start");
                double? end = GetOptionalDouble(interval, "end");
                if (start.HasValue && end.HasValue && end.Value >= start.Value)
                {
                    intervals.Add(new WhisperSpeechInterval(
                        start.Value,
                        end.Value,
                        GetOptionalDouble(interval, "confidence")));
                }
            }
        }

        return new WhisperResponseMetadata(
            hasExtension,
            hasExtension ? GetOptionalString(extension, "schema_version") : string.Empty,
            hasExtension ? GetOptionalString(extension, "request_id") : string.Empty,
            hasExtension ? GetOptionalString(extension, "task") : string.Empty,
            hasExtension ? GetOptionalString(extension, "requested_model") : string.Empty,
            hasExtension ? GetOptionalString(extension, "selected_model") : string.Empty,
            hasExtension ? GetOptionalString(extension, "backend") : string.Empty,
            hasExtension ? GetOptionalString(extension, "server_version") : string.Empty,
            hasExtension ? GetOptionalString(extension, "detected_language") : GetOptionalString(root, "language"),
            hasExtension ? GetOptionalDouble(extension, "detected_language_probability") : null,
            hasExtension ? GetOptionalString(extension, "detected_language_probability_basis") : string.Empty,
            hasExtension ? GetOptionalString(extension, "effective_language") : string.Empty,
            hasExtension ? GetOptionalDouble(extension, "audio_duration_seconds") : null,
            hasExtension ? GetOptionalDouble(extension, "preparation_seconds") : null,
            hasExtension ? GetOptionalDouble(extension, "inference_seconds") : null,
            hasExtension ? GetOptionalDouble(extension, "realtime_factor") : null,
            cacheHit,
            hasExtension ? GetOptionalBoolean(extension, "inference_includes_preparation") : null,
            hasExtension ? GetOptionalInt32(extension, "decode_count") : null,
            timing,
            hasExtension ? GetStringArray(extension, "warnings") : [],
            intervals,
            BuildIdentity: hasExtension ? GetOptionalString(extension, "build_identity") : string.Empty,
            RequestedLanguage: hasExtension ? GetOptionalString(extension, "requested_language") : string.Empty,
            QueueSeconds: hasExtension ? GetOptionalDouble(extension, "queue_seconds") : null,
            ColdStartSeconds: hasExtension ? GetOptionalDouble(extension, "cold_start_seconds") : null,
            CacheType: cacheType);
    }

    internal static void ValidateExtensionSchemaVersion(string schemaVersion)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            throw new WhisperApiException(
                (int)HttpStatusCode.BadGateway,
                "missing_extension_schema_version",
                "The speech service returned extension metadata without a schema version.",
                $"supported_major={SupportedExtensionSchemaMajor}");
        }

        string majorText = schemaVersion.Split('.', 2, StringSplitOptions.TrimEntries)[0];
        if (!int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || major != SupportedExtensionSchemaMajor)
        {
            throw new WhisperApiException(
                (int)HttpStatusCode.BadGateway,
                "unsupported_extension_schema_version",
                $"The speech service uses unsupported extension schema version '{schemaVersion}'.",
                $"supported_major={SupportedExtensionSchemaMajor}");
        }
    }

    private static int AppendOpenAiWords(
        JsonElement words,
        ICollection<WhisperSegment> destination,
        double? averageLogProbability = null,
        double? noSpeechProbability = null)
    {
        int wordCount = 0;
        foreach (JsonElement word in words.EnumerateArray())
        {
            if (word.TryGetProperty("start", out JsonElement wordStart)
                && word.TryGetProperty("end", out JsonElement wordEnd)
                && word.TryGetProperty("word", out JsonElement wordText))
            {
                destination.Add(new WhisperSegment(
                    wordStart.GetDouble(),
                    wordEnd.GetDouble(),
                    wordText.GetString() ?? string.Empty,
                    WhisperTimingGranularity.Word,
                    averageLogProbability,
                    noSpeechProbability,
                    ParseDecoderTokenProbabilities(word)));
                wordCount++;
            }
        }

        return wordCount;
    }

    private static IReadOnlyList<double>? ParseDecoderTokenProbabilities(JsonElement value)
    {
        if (!value.TryGetProperty("x_whisper_server", out JsonElement extension)
            || extension.ValueKind != JsonValueKind.Object
            || !extension.TryGetProperty("decoder_token_probabilities", out JsonElement probabilities)
            || probabilities.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<double> result = [];
        foreach (JsonElement probability in probabilities.EnumerateArray())
        {
            if (probability.ValueKind == JsonValueKind.Number && probability.TryGetDouble(out double number))
            {
                result.Add(number);
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static string GetOptionalString(JsonElement value, string propertyName)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, WhisperTimingChannelProvenance> GetTimingChannels(
        JsonElement timing)
    {
        if (timing.ValueKind != JsonValueKind.Object
            || !timing.TryGetProperty("channels", out JsonElement channels)
            || channels.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, WhisperTimingChannelProvenance>(StringComparer.Ordinal);
        }

        Dictionary<string, WhisperTimingChannelProvenance> result = new(StringComparer.Ordinal);
        foreach (JsonProperty channel in channels.EnumerateObject())
        {
            if (channel.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            result[channel.Name] = new WhisperTimingChannelProvenance(
                GetOptionalString(channel.Value, "basis"),
                GetOptionalBoolean(channel.Value, "reliable"),
                GetOptionalString(channel.Value, "reliability"));
        }

        return result;
    }

    private static double? GetOptionalDouble(JsonElement value, string propertyName)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out double result)
                ? result
                : null;
    }

    private static int? GetOptionalInt32(JsonElement value, string propertyName)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out int result)
                ? result
                : null;
    }

    private static long? GetOptionalInt64(JsonElement value, string propertyName)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out long result)
                ? result
                : null;
    }

    private static bool? GetOptionalBoolean(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool? GetNestedOptionalBoolean(JsonElement value, string objectName, string propertyName) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(objectName, out JsonElement nested)
        && nested.ValueKind == JsonValueKind.Object
            ? GetOptionalBoolean(nested, propertyName)
            : null;

    private static int? GetNestedOptionalInt32(JsonElement value, string objectName, string propertyName) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(objectName, out JsonElement nested)
        && nested.ValueKind == JsonValueKind.Object
            ? GetOptionalInt32(nested, propertyName)
            : null;

    private enum WhisperApiStyle
    {
        WhisperCpp,
        OpenAi
    }

    private sealed record ExternalServerInfo(WhisperApiStyle ApiStyle, bool SupportsTranslation);
}
