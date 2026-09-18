namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using System.Net;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.SubtitleAligner.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class WhisperProgressCancellationTests
{
    [Fact]
    public async Task TranscribeAsync_ReusesValidatedResponseUnlessRetranscriptionIsRequested()
    {
        string cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "subtitle-aligner-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using TranscriptionCache cache = new(NullLogger<TranscriptionCache>.Instance, cacheDirectory);
            using CachingContractHandler handler = new();
            using HttpClient client = new(handler) { BaseAddress = new Uri("http://speech.test/") };
            WhisperServer server = new(
                new StaticHttpClientFactory(client),
                NullLogger<WhisperServer>.Instance,
                cache);
            ConfigureExternalServer(server, client.BaseAddress!);
            typeof(WhisperServer)
                .GetField("_activeConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(server, new PluginConfiguration { EnableTranscriptionCache = true });
            string wavPath = Path.Combine(cacheDirectory, "sample.wav");
            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllBytesAsync(
                wavPath,
                [0x52, 0x49, 0x46, 0x46, 0x01],
                TestContext.Current.CancellationToken);

            WhisperTranscript first = await server.TranscribeAsync(
                wavPath,
                "en",
                translateToEnglish: false,
                requireWordTimestamps: false,
                requireVadIntervals: false,
                bypassCache: false,
                inferenceProgress: null,
                cancellationToken: TestContext.Current.CancellationToken);
            WhisperTranscript cached = await server.TranscribeAsync(
                wavPath,
                "en",
                translateToEnglish: false,
                requireWordTimestamps: false,
                requireVadIntervals: false,
                bypassCache: false,
                inferenceProgress: null,
                cancellationToken: TestContext.Current.CancellationToken);
            WhisperTranscript refreshed = await server.TranscribeAsync(
                wavPath,
                "en",
                translateToEnglish: false,
                requireWordTimestamps: false,
                requireVadIntervals: false,
                bypassCache: true,
                inferenceProgress: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(first.Metadata.CacheHit);
            Assert.True(cached.Metadata.CacheHit);
            Assert.Equal("plugin_disk", cached.Metadata.CacheType);
            Assert.False(refreshed.Metadata.CacheHit);
            Assert.Equal(2, handler.InferenceRequests);
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TranscribeAsync_PollsProgressAndCancelsActiveRequestOnService()
    {
        using TranscriptionCache cache = CreateCache();
        using CancellationContractHandler handler = new();
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://speech.test/") };
        WhisperServer server = new(
            new StaticHttpClientFactory(client),
            NullLogger<WhisperServer>.Instance,
            cache);
        ConfigureExternalServer(server, client.BaseAddress!);

        string wavPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                wavPath,
                [0x52, 0x49, 0x46, 0x46],
                TestContext.Current.CancellationToken);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(5));
            TaskCompletionSource<WhisperInferenceProgress> progressObserved = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task<WhisperTranscript> transcription = server.TranscribeAsync(
                wavPath,
                "en",
                translateToEnglish: false,
                requireWordTimestamps: false,
                requireVadIntervals: false,
                bypassCache: false,
                inferenceProgress: update => progressObserved.TrySetResult(update),
                cancellationToken: cancellation.Token);

            WhisperInferenceProgress progress = await progressObserved.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.Equal("inference", progress.Phase);
            Assert.Equal(0.1, progress.FractionCompleted);
            Assert.False(handler.MultipartBody.Contains("name=stream", StringComparison.Ordinal));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcription);
            Assert.Equal(handler.ActiveRequestId, handler.CancelledRequestId);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public async Task TranscribeAsync_RequestsPairedDualOutputForCrossLanguageVadAlignment()
    {
        using TranscriptionCache cache = CreateCache();
        using DualOutputRequestHandler handler = new();
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://speech.test/") };
        WhisperServer server = new(
            new StaticHttpClientFactory(client),
            NullLogger<WhisperServer>.Instance,
            cache);
        ConfigureExternalServer(server, client.BaseAddress!);

        string wavPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                wavPath,
                [0x52, 0x49, 0x46, 0x46],
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<WhisperApiException>(() => server.TranscribeAsync(
                wavPath,
                "ja",
                translateToEnglish: true,
                requireWordTimestamps: false,
                requireVadIntervals: true,
                bypassCache: false,
                inferenceProgress: null,
                cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal("auto", ReadFormValues(handler.MultipartBody, "model").Single());
            Assert.Equal("ja", ReadFormValues(handler.MultipartBody, "language").Single());
            Assert.Equal("true", ReadFormValues(handler.MultipartBody, "dual_output").Single());
            Assert.Empty(ReadFormValues(handler.MultipartBody, "stream"));
            Assert.Contains("timestamp_granularities=segment%2Cvad", handler.CapabilityQuery, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dual_output=true", handler.CapabilityQuery, StringComparison.Ordinal);
            Assert.Equal(
                ["segment", "vad"],
                ReadFormValues(handler.MultipartBody, "timestamp_granularities[]"));
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    private static void ConfigureExternalServer(WhisperServer server, Uri baseUri)
    {
        Type type = typeof(WhisperServer);
        type.GetField("_baseUri", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, baseUri);
        type.GetField("_usingExternal", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, true);
        FieldInfo apiStyle = type.GetField("_apiStyle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apiStyle.SetValue(server, Enum.Parse(apiStyle.FieldType, "OpenAi", ignoreCase: false));
        type.GetField("_externalSupportsTranslation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, true);
    }

    private static TranscriptionCache CreateCache()
    {
        return new TranscriptionCache(
            NullLogger<TranscriptionCache>.Instance,
            Path.Combine(Path.GetTempPath(), "subtitle-aligner-tests", Guid.NewGuid().ToString("N")));
    }

    private static string[] ReadFormValues(string body, string name)
    {
        List<string> values = [];
        int searchStart = 0;
        while (searchStart < body.Length)
        {
            int marker = body.IndexOf($"name={name}", searchStart, StringComparison.Ordinal);
            int quotedMarker = body.IndexOf($"name=\"{name}\"", searchStart, StringComparison.Ordinal);
            if (marker < 0 || (quotedMarker >= 0 && quotedMarker < marker))
            {
                marker = quotedMarker;
            }

            if (marker < 0)
            {
                break;
            }

            int valueStart = body.IndexOf("\r\n\r\n", marker, StringComparison.Ordinal);
            if (valueStart < 0)
            {
                break;
            }

            valueStart += 4;
            int valueEnd = body.IndexOf("\r\n", valueStart, StringComparison.Ordinal);
            values.Add(body[valueStart..valueEnd]);
            searchStart = valueEnd;
        }

        return [.. values];
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CancellationContractHandler : HttpMessageHandler
    {
        private const string ErrorCodes = """
            ["unsupported_timing","word_timestamps_unavailable","vad_intervals_unavailable","model_task_mismatch","invalid_language","capability_downgrade","overloaded","request_cancelled","request_not_found","backend_failure","unsupported_extension_schema_version"]
            """;

        internal string ActiveRequestId { get; private set; } = string.Empty;

        internal string CancelledRequestId { get; private set; } = string.Empty;

        internal string MultipartBody { get; private set; } = string.Empty;

        internal string CapabilityQuery { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == "/v1/models")
            {
                return Json(HttpStatusCode.OK, "{\"object\":\"list\",\"data\":[]}");
            }

            if (request.Method == HttpMethod.Post && path == "/v1/audio/translations")
            {
                return Json(HttpStatusCode.BadRequest, "{\"error\":\"probe\"}");
            }

            if (request.Method == HttpMethod.Get && path == "/v1/audio/capabilities")
            {
                CapabilityQuery = request.RequestUri?.Query ?? string.Empty;
                return Json(HttpStatusCode.OK, $$"""
                    {
                      "object":"audio.route.capabilities",
                      "schema_version":"1.0",
                      "task":"transcribe",
                      "requested_model":"auto",
                      "selected_model":"synthetic-model",
                      "backend":"synthetic",
                      "build_identity":"synthetic-build",
                      "model_files_cached":true,
                      "supported_tasks":["transcribe"],
                      "timing_granularities":["segment"],
                      "timing_bases":["decoder_segment"],
                      "vad_intervals":false,
                      "supported_languages":["*"],
                      "automatic_language_detection":true,
                      "effective_language":"en",
                      "response_formats":["verbose_json"],
                      "confidence_evidence":{
                        "word_probability":false,
                        "decoder_token_probabilities":false,
                        "segment_average_log_probability":false,
                        "segment_no_speech_probability":false,
                        "detected_language_probability":false
                      },
                      "optional_features":{
                        "dual_output":false,
                        "dual_output_decode_count":1,
                        "progress":true,
                        "cancellation":true
                      },
                      "error_codes":{{ErrorCodes}},
                      "limits":{"maximum_upload_bytes":1048576,"maximum_audio_duration_seconds":600},
                      "limitations":[],
                      "configuration_can_change":false
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/audio/requests/", StringComparison.Ordinal))
            {
                string requestId = Uri.UnescapeDataString(path["/v1/audio/requests/".Length..]);
                return Json(HttpStatusCode.OK, $$"""
                    {
                      "object":"audio.request",
                      "schema_version":"1.0",
                      "request_id":"{{requestId}}",
                      "state":"running",
                      "phase":"inference",
                      "completed_work":null,
                      "total_work":null,
                      "fraction_completed":0.1,
                      "eta_seconds":9.0,
                      "cancellation_requested":false
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post && path == "/v1/audio/transcriptions")
            {
                MultipartBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                ActiveRequestId = ReadMultipartValue(MultipartBody, "request_id");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The synthetic request unexpectedly completed.");
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/v1/audio/requests/", StringComparison.Ordinal))
            {
                CancelledRequestId = Uri.UnescapeDataString(path["/v1/audio/requests/".Length..]);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        private static string ReadMultipartValue(string body, string name)
        {
            int marker = body.IndexOf($"name={name}", StringComparison.Ordinal);
            if (marker < 0)
            {
                marker = body.IndexOf($"name=\"{name}\"", StringComparison.Ordinal);
            }

            int valueStart = marker < 0 ? -1 : body.IndexOf("\r\n\r\n", marker, StringComparison.Ordinal);
            if (valueStart < 0)
            {
                throw new InvalidDataException($"Multipart field {name} was missing.");
            }

            valueStart += 4;
            int valueEnd = body.IndexOf("\r\n", valueStart, StringComparison.Ordinal);
            return body[valueStart..valueEnd];
        }
    }

    private sealed class DualOutputRequestHandler : HttpMessageHandler
    {
        private const string ErrorCodes = """
            ["unsupported_timing","word_timestamps_unavailable","vad_intervals_unavailable","model_task_mismatch","invalid_language","capability_downgrade","overloaded","request_cancelled","request_not_found","backend_failure","unsupported_extension_schema_version"]
            """;

        internal string MultipartBody { get; private set; } = string.Empty;

        internal string CapabilityQuery { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == "/v1/audio/capabilities")
            {
                CapabilityQuery = request.RequestUri?.Query ?? string.Empty;
                return Json(HttpStatusCode.OK, $$"""
                    {
                      "object":"audio.route.capabilities",
                      "schema_version":"1.0",
                      "task":"translate",
                      "requested_model":"auto",
                      "selected_model":"synthetic-model",
                      "backend":"synthetic",
                      "build_identity":"synthetic-build",
                      "model_files_cached":true,
                      "supported_tasks":["transcribe","translate"],
                      "timing_granularities":["segment","vad"],
                      "timing_bases":["decoder_segment","vad"],
                      "vad_intervals":true,
                      "supported_languages":["*"],
                      "automatic_language_detection":true,
                      "effective_language":"ja",
                      "response_formats":["verbose_json"],
                      "confidence_evidence":{
                        "word_probability":false,
                        "decoder_token_probabilities":false,
                        "segment_average_log_probability":true,
                        "segment_no_speech_probability":true,
                        "detected_language_probability":true
                      },
                      "optional_features":{
                        "dual_output":true,
                        "dual_output_decode_count":2,
                        "progress":true,
                        "cancellation":false
                      },
                      "error_codes":{{ErrorCodes}},
                      "limits":{"maximum_upload_bytes":1048576,"maximum_audio_duration_seconds":600},
                      "limitations":[],
                      "configuration_can_change":false
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post && path == "/v1/audio/translations")
            {
                MultipartBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(
                    HttpStatusCode.InternalServerError,
                    "{\"error\":{\"code\":\"backend_failure\",\"message\":\"synthetic stop after request inspection\"}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CachingContractHandler : HttpMessageHandler
    {
        private const string ErrorCodes = """
            ["unsupported_timing","word_timestamps_unavailable","vad_intervals_unavailable","model_task_mismatch","invalid_language","capability_downgrade","overloaded","request_cancelled","request_not_found","backend_failure","unsupported_extension_schema_version"]
            """;

        internal int InferenceRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == "/v1/audio/capabilities")
            {
                return Json(HttpStatusCode.OK, $$"""
                    {
                      "object":"audio.route.capabilities",
                      "schema_version":"1.0",
                      "task":"transcribe",
                      "requested_model":"auto",
                      "selected_model":"synthetic-model",
                      "backend":"synthetic",
                      "build_identity":"synthetic-build",
                      "model_files_cached":true,
                      "supported_tasks":["transcribe"],
                      "timing_granularities":["segment"],
                      "timing_bases":["decoder_segment"],
                      "vad_intervals":false,
                      "supported_languages":["*"],
                      "automatic_language_detection":true,
                      "effective_language":"en",
                      "response_formats":["verbose_json"],
                      "confidence_evidence":{
                        "word_probability":false,
                        "decoder_token_probabilities":false,
                        "segment_average_log_probability":true,
                        "segment_no_speech_probability":true,
                        "detected_language_probability":true
                      },
                      "optional_features":{
                        "dual_output":false,
                        "dual_output_decode_count":1,
                        "progress":false,
                        "cancellation":false
                      },
                      "error_codes":{{ErrorCodes}},
                      "limits":{"maximum_upload_bytes":1048576,"maximum_audio_duration_seconds":600},
                      "limitations":[],
                      "configuration_can_change":false
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post && path == "/v1/audio/transcriptions")
            {
                InferenceRequests++;
                string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                string requestId = ReadMultipartValue(body, "request_id");
                return Json(HttpStatusCode.OK, $$"""
                    {
                      "segments":[{"start":0.0,"end":1.0,"text":"Synthetic phrase"}],
                      "x_whisper_server":{
                        "schema_version":"1.0",
                        "request_id":"{{requestId}}",
                        "task":"transcribe",
                        "requested_model":"auto",
                        "selected_model":"synthetic-model",
                        "backend":"synthetic",
                        "build_identity":"synthetic-build",
                        "server_version":"1.0",
                        "requested_language":"en",
                        "detected_language":"en",
                        "detected_language_probability":0.99,
                        "detected_language_probability_basis":"synthetic-classifier",
                        "effective_language":"en",
                        "audio_duration_seconds":1.0,
                        "queue_seconds":0.0,
                        "preparation_seconds":0.0,
                        "inference_seconds":0.1,
                        "cold_start_seconds":0.0,
                        "inference_includes_preparation":false,
                        "realtime_factor":0.1,
                        "cache":{"type":"none","hit":false},
                        "decode_count":1,
                        "warnings":[],
                        "timing":{
                          "requested":["segment"],
                          "actual":["segment"],
                          "basis":"decoder_segment",
                          "reliable":true,
                          "reliability":"decoder-provided",
                          "channels":{
                            "segment":{
                              "basis":"decoder_segment",
                              "reliable":true,
                              "reliability":"decoder-provided"
                            }
                          }
                        }
                      }
                    }
                    """);
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        private static string ReadMultipartValue(string body, string name)
        {
            int marker = body.IndexOf($"name={name}", StringComparison.Ordinal);
            if (marker < 0)
            {
                marker = body.IndexOf($"name=\"{name}\"", StringComparison.Ordinal);
            }

            int valueStart = marker < 0 ? -1 : body.IndexOf("\r\n\r\n", marker, StringComparison.Ordinal);
            if (valueStart < 0)
            {
                throw new InvalidDataException($"Multipart field {name} was missing.");
            }

            valueStart += 4;
            int valueEnd = body.IndexOf("\r\n", valueStart, StringComparison.Ordinal);
            return body[valueStart..valueEnd];
        }
    }

}
