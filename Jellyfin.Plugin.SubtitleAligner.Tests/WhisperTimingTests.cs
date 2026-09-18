namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using System.Net;
using System.Text.Json;
using Xunit;

public sealed class WhisperTimingTests
{
    [Fact]
    public void ParseRequestProgress_AcceptsLivePollingShapeWithUnknownWorkCounts()
    {
        const string Response = """
            {
              "object":"audio.request",
              "schema_version":"1.0",
              "request_id":"synthetic-request",
              "state":"running",
              "phase":"inference",
              "completed_work":null,
              "total_work":null,
              "fraction_completed":0.5,
              "eta_seconds":12.4,
              "cancellation_requested":false
            }
            """;
        double previousFraction = -1;

        WhisperInferenceProgress? progress = WhisperServer.ParseRequestProgress(
            Response,
            "synthetic-request",
            ref previousFraction);

        Assert.NotNull(progress);
        Assert.Equal("inference", progress.Phase);
        Assert.Equal(0.5, progress.FractionCompleted);
        Assert.Equal(12.4, progress.EtaSeconds);
        Assert.Equal("running", progress.State);
        Assert.Equal(0.5, previousFraction);
    }

    [Fact]
    public void ParseRequestProgress_PreservesCompletedState()
    {
        const string Response = """
            {
              "object":"audio.request",
              "schema_version":"1.0",
              "request_id":"synthetic-request",
              "state":"completed",
              "phase":"completed",
              "completed_work":10,
              "total_work":10,
              "fraction_completed":1.0,
              "eta_seconds":0,
              "cancellation_requested":false
            }
            """;
        double previousFraction = 0.9;

        WhisperInferenceProgress progress = Assert.IsType<WhisperInferenceProgress>(
            WhisperServer.ParseRequestProgress(Response, "synthetic-request", ref previousFraction));

        Assert.Equal("completed", progress.State);
        Assert.Equal(1.0, progress.FractionCompleted);
    }

    [Fact]
    public async Task WaitForResponseDeliveryAsync_FailsFastAfterCompletedStatusStalls()
    {
        TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        WhisperApiException exception = await Assert.ThrowsAsync<WhisperApiException>(() =>
            WhisperServer.WaitForResponseDeliveryAsync(
                stalled.Task,
                "synthetic-request",
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken));

        Assert.Equal(504, exception.StatusCode);
        Assert.Equal("response_delivery_timeout", exception.Code);
        Assert.Equal("synthetic-request", exception.Details);
    }

    [Fact]
    public async Task WaitForResponseDeliveryAsync_AcceptsPromptCompletedResponse()
    {
        await WhisperServer.WaitForResponseDeliveryAsync(
            Task.CompletedTask,
            "synthetic-request",
            TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ParseRequestProgress_AllowsUnknownFractionWithoutInventingProgress()
    {
        const string Response = """
            {
              "object":"audio.request",
              "schema_version":"1.0",
              "request_id":"synthetic-request",
              "state":"queued",
              "phase":"queued",
              "completed_work":null,
              "total_work":null,
              "fraction_completed":null,
              "eta_seconds":null,
              "cancellation_requested":false
            }
            """;
        double previousFraction = -1;

        WhisperInferenceProgress? progress = WhisperServer.ParseRequestProgress(
            Response,
            "synthetic-request",
            ref previousFraction);

        Assert.Null(progress);
        Assert.Equal(-1, previousFraction);
    }

    [Fact]
    public void ParseRequestProgress_RejectsRegressingFraction()
    {
        const string Response = """
            {
              "object":"audio.request",
              "schema_version":"1.0",
              "request_id":"synthetic-request",
              "state":"running",
              "phase":"inference",
              "completed_work":null,
              "total_work":null,
              "fraction_completed":0.4,
              "eta_seconds":12.4,
              "cancellation_requested":false
            }
            """;
        double previousFraction = 0.5;

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ParseRequestProgress(
                Response,
                "synthetic-request",
                ref previousFraction));

        Assert.Equal("response_contract_mismatch", exception.Code);
    }

    [Fact]
    public async Task ReadEventStreamAsync_ReportsProgressAndReturnsTerminalResult()
    {
        using JsonDocument resultDocument = JsonDocument.Parse(CompleteContractResponse(includeBuildIdentity: true));
        string compactResult = JsonSerializer.Serialize(resultDocument.RootElement);
        string stream = """
            event: progress
            data: {"schema_version":"1.0","request_id":"synthetic-request","phase":"inference","processed_audio_seconds":1.5,"total_audio_seconds":3.0,"fraction_completed":0.5,"eta_seconds":0.2}

            event: result
            data: RESULT_PLACEHOLDER

            """.Replace("RESULT_PLACEHOLDER", compactResult, StringComparison.Ordinal);
        using StringContent content = new(stream);
        List<WhisperInferenceProgress> updates = [];

        WhisperTranscript transcript = await WhisperServer.ReadEventStreamAsync(
            content,
            "synthetic-request",
            updates.Add,
            CancellationToken.None);

        WhisperInferenceProgress update = Assert.Single(updates);
        Assert.Equal("inference", update.Phase);
        Assert.Equal(0.5, update.FractionCompleted);
        Assert.Equal("synthetic-request", transcript.Metadata.RequestId);
    }

    [Fact]
    public async Task ReadEventStreamAsync_RejectsProgressForAnotherRequest()
    {
        const string Stream = """
            event: progress
            data: {"schema_version":"1.0","request_id":"wrong-request","phase":"inference","processed_audio_seconds":1.5,"total_audio_seconds":3.0,"fraction_completed":0.5,"eta_seconds":0.2}

            """;
        using StringContent content = new(Stream);

        WhisperApiException exception = await Assert.ThrowsAsync<WhisperApiException>(() =>
            WhisperServer.ReadEventStreamAsync(
                content,
                "synthetic-request",
                progress: null,
                CancellationToken.None));

        Assert.Equal("response_contract_mismatch", exception.Code);
    }

    [Fact]
    public async Task ReadEventStreamAsync_RejectsStreamWithoutTerminalResult()
    {
        const string Stream = """
            event: progress
            data: {"schema_version":"1.0","request_id":"synthetic-request","phase":"inference","processed_audio_seconds":3.0,"total_audio_seconds":3.0,"fraction_completed":1.0,"eta_seconds":0.0}

            """;
        using StringContent content = new(Stream);

        WhisperApiException exception = await Assert.ThrowsAsync<WhisperApiException>(() =>
            WhisperServer.ReadEventStreamAsync(
                content,
                "synthetic-request",
                progress: null,
                CancellationToken.None));

        Assert.Equal("response_contract_mismatch", exception.Code);
        Assert.Contains("without a terminal result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadEventStreamAsync_RejectsRegressingProgress()
    {
        const string Stream = """
            event: progress
            data: {"schema_version":"1.0","request_id":"synthetic-request","phase":"inference","processed_audio_seconds":2.0,"total_audio_seconds":10.0,"fraction_completed":0.2,"eta_seconds":8.0}

            event: progress
            data: {"schema_version":"1.0","request_id":"synthetic-request","phase":"inference","processed_audio_seconds":1.0,"total_audio_seconds":10.0,"fraction_completed":0.1,"eta_seconds":9.0}

            """;
        using StringContent content = new(Stream);

        WhisperApiException exception = await Assert.ThrowsAsync<WhisperApiException>(() =>
            WhisperServer.ReadEventStreamAsync(
                content,
                "synthetic-request",
                progress: null,
                CancellationToken.None));

        Assert.Equal("response_contract_mismatch", exception.Code);
    }

    [Fact]
    public void ParseSegments_PreservesSegmentTimingGranularity()
    {
        const string Json = """
            {
              "segments": [
                { "start": 1.25, "end": 3.75, "text": "A complete translated sentence." }
              ]
            }
            """;

        WhisperSegment segment = Assert.Single(WhisperServer.ParseSegments(Json));

        Assert.Equal(WhisperTimingGranularity.Segment, segment.TimingGranularity);
        Assert.Equal(1.25, segment.StartSeconds);
        Assert.Equal(3.75, segment.EndSeconds);
    }

    [Fact]
    public void ParseSegments_UsesPairedSourceTimingForDualOutputTranslationText()
    {
        const string Json = """
            {
              "segments": [
                { "start": 9.0, "end": 12.0, "text": "Unpaired decoder translation" }
              ],
              "source_segments": [
                { "segment_id": "source-1", "start": 2.0, "end": 4.0, "text": "source words" }
              ],
              "translation_segments": [
                { "segment_id": "source-1", "start": 8.0, "end": 10.0, "text": "Paired English text" }
              ]
            }
            """;

        WhisperTranscript transcript = WhisperServer.ParseTranscript(Json);
        WhisperSegment segment = Assert.Single(transcript.Segments);
        WhisperSegment decoderSegment = Assert.Single(transcript.DecoderSegments!);

        Assert.Equal("Paired English text", segment.Text);
        Assert.Equal(2.0, segment.StartSeconds);
        Assert.Equal(4.0, segment.EndSeconds);
        Assert.Equal(WhisperTimingGranularity.Segment, segment.TimingGranularity);
        Assert.Equal("Unpaired decoder translation", decoderSegment.Text);
        Assert.Equal(9.0, decoderSegment.StartSeconds);
        Assert.Equal(12.0, decoderSegment.EndSeconds);
    }

    [Fact]
    public void ParseSegments_RejectsMismatchedDualOutputIds()
    {
        const string Json = """
            {
              "source_segments": [
                { "segment_id": "source-1", "start": 2.0, "end": 4.0, "text": "source words" }
              ],
              "translation_segments": [
                { "segment_id": "source-2", "start": 2.0, "end": 4.0, "text": "Unpaired English text" }
              ]
            }
            """;

        Assert.Throws<JsonException>(() => WhisperServer.ParseSegments(Json));
    }

    [Fact]
    public void ParseSegments_PreservesNestedWordTimestamps()
    {
        const string Json = """
            {
              "segments": [
                {
                  "start": 1.0,
                  "end": 2.0,
                  "text": "Hello world",
                  "words": [
                    { "start": 1.1, "end": 1.4, "word": "Hello" },
                    { "start": 1.6, "end": 1.9, "word": "world" }
                  ]
                }
              ]
            }
            """;

        IReadOnlyList<WhisperSegment> words = WhisperServer.ParseSegments(Json);

        Assert.Collection(
            words,
            word =>
            {
                Assert.Equal(WhisperTimingGranularity.Word, word.TimingGranularity);
                Assert.Equal(1.1, word.StartSeconds);
                Assert.Equal("Hello", word.Text);
            },
            word =>
            {
                Assert.Equal(WhisperTimingGranularity.Word, word.TimingGranularity);
                Assert.Equal(1.6, word.StartSeconds);
                Assert.Equal("world", word.Text);
            });
    }

    [Fact]
    public void ParseSegments_PreservesRootWordTimestamps()
    {
        const string Json = """
            {
              "text": "Hello world",
              "words": [
                { "start": 2.1, "end": 2.4, "word": "Hello" },
                { "start": 2.6, "end": 2.9, "word": "world" }
              ],
              "segments": [
                { "start": 2.0, "end": 3.0, "text": "Hello world" }
              ]
            }
            """;

        IReadOnlyList<WhisperSegment> words = WhisperServer.ParseSegments(Json);

        Assert.Equal(2, words.Count);
        Assert.All(words, word => Assert.Equal(WhisperTimingGranularity.Word, word.TimingGranularity));
        Assert.Equal(2.1, words[0].StartSeconds);
        Assert.Equal(2.6, words[1].StartSeconds);
    }

    [Fact]
    public void ParseSegments_PreservesWhisperCppTokenTiming()
    {
        const string Json = """
            {
              "transcription": [
                {
                  "offsets": { "from": 1250, "to": 1500 },
                  "text": "Hello"
                }
              ]
            }
            """;

        WhisperSegment token = Assert.Single(WhisperServer.ParseSegments(Json));

        Assert.Equal(WhisperTimingGranularity.Token, token.TimingGranularity);
        Assert.Equal(1.25, token.StartSeconds);
        Assert.Equal(1.5, token.EndSeconds);
    }

    [Fact]
    public void ParseSegments_DoesNotTreatWhisperCppPhraseAsTokenTiming()
    {
        const string Json = """
            {
              "transcription": [
                {
                  "offsets": { "from": 1250, "to": 2500 },
                  "text": "Hello world"
                }
              ]
            }
            """;

        WhisperSegment segment = Assert.Single(WhisperServer.ParseSegments(Json));

        Assert.Equal(WhisperTimingGranularity.Segment, segment.TimingGranularity);
    }

    [Fact]
    public void ParseTranscript_PreservesExtensionMetadataVadAndConfidenceEvidence()
    {
        const string Json = """
            {
              "text": "Synthetic dialogue",
              "language": "ja",
              "segments": [
                {
                  "start": 1.0,
                  "end": 2.0,
                  "text": "Synthetic dialogue",
                  "avg_logprob": -0.21,
                  "no_speech_prob": 0.03,
                  "words": [
                    {
                      "start": 1.1,
                      "end": 1.4,
                      "word": "Synthetic",
                      "x_whisper_server": {
                        "decoder_token_probabilities": [0.91, 0.87]
                      }
                    },
                    { "start": 1.5, "end": 1.9, "word": "dialogue" }
                  ]
                }
              ],
              "speech_segments": [
                { "start": 1.02, "end": 1.96 }
              ],
              "x_whisper_server": {
                "schema_version": "1.0",
                "request_id": "synthetic-request",
                "task": "translate",
                "requested_model": "auto",
                "selected_model": "synthetic-model",
                "backend": "whisper",
                "server_version": "1.2.3 (4)",
                "detected_language": "ja",
                "detected_language_probability": 0.98,
                "detected_language_probability_basis": "whisper_language_classifier",
                "effective_language": "ja",
                "audio_duration_seconds": 3.0,
                "preparation_seconds": 0.2,
                "inference_seconds": 0.6,
                "inference_includes_preparation": false,
                "realtime_factor": 0.2,
                "cache": { "type": "model_files", "hit": true },
                "decode_count": 1,
                "warnings": ["translated_word_timestamps_approximate", "vad_confidence_unavailable"],
                "timing": {
                  "requested": ["word"],
                  "actual": ["word", "vad"],
                  "basis": "decoder_word",
                  "reliable": false,
                  "reliability": "approximate",
                  "channels": {
                    "word": {
                      "basis": "decoder_word",
                      "reliable": false,
                      "reliability": "approximate"
                    },
                    "vad": {
                      "basis": "vad",
                      "reliable": true,
                      "reliability": "synthetic-vad"
                    }
                  }
                }
              }
            }
            """;

        WhisperTranscript transcript = WhisperServer.ParseTranscript(Json);

        Assert.Equal(2, transcript.Segments.Count);
        Assert.All(transcript.Segments, segment => Assert.Equal(WhisperTimingGranularity.Word, segment.TimingGranularity));
        Assert.All(transcript.Segments, segment => Assert.Equal(-0.21, segment.AverageLogProbability));
        Assert.All(transcript.Segments, segment => Assert.Equal(0.03, segment.NoSpeechProbability));
        Assert.Equal([0.91, 0.87], transcript.Segments[0].DecoderTokenProbabilities);
        Assert.True(transcript.Metadata.HasExtensionMetadata);
        Assert.Equal("1.0", transcript.Metadata.ExtensionSchemaVersion);
        Assert.Equal("synthetic-request", transcript.Metadata.RequestId);
        Assert.Equal("translate", transcript.Metadata.Task);
        Assert.Equal("auto", transcript.Metadata.RequestedModel);
        Assert.Equal("synthetic-model", transcript.Metadata.SelectedModel);
        Assert.Equal("ja", transcript.Metadata.DetectedLanguage);
        Assert.Equal(0.98, transcript.Metadata.DetectedLanguageProbability);
        Assert.Equal("whisper_language_classifier", transcript.Metadata.DetectedLanguageProbabilityBasis);
        Assert.Equal(0.2, transcript.Metadata.RealtimeFactor);
        Assert.Equal("model_files", transcript.Metadata.CacheType);
        Assert.True(transcript.Metadata.CacheHit);
        Assert.False(transcript.Metadata.InferenceIncludesPreparation);
        Assert.Equal("decoder_word", transcript.Metadata.Timing?.Basis);
        Assert.False(transcript.Metadata.Timing?.Reliable);
        Assert.Equal("approximate", transcript.Metadata.Timing?.Reliability);
        Assert.Equal(["word", "vad"], transcript.Metadata.Timing?.Actual);
        Assert.Equal("decoder_word", transcript.Metadata.Timing?.Channels?["word"].Basis);
        Assert.Equal("vad", transcript.Metadata.Timing?.Channels?["vad"].Basis);
        Assert.Contains("translated_word_timestamps_approximate", transcript.Metadata.Warnings);
        WhisperSpeechInterval speech = Assert.Single(transcript.Metadata.SpeechIntervals);
        Assert.Equal(1.02, speech.StartSeconds);
        Assert.Equal(1.96, speech.EndSeconds);
        Assert.Null(speech.Confidence);
    }

    [Fact]
    public void ParseCapabilities_PreservesResolvedRouteContract()
    {
        const string Json = """
            {
              "object": "audio.route.capabilities",
              "schema_version": "1.0",
              "task": "translate",
              "requested_model": "auto",
              "selected_model": "synthetic-model",
              "backend": "whisper",
              "build_identity": "synthetic-build",
              "model_files_cached": true,
              "supported_tasks": ["transcribe", "translate"],
              "timing_granularities": ["segment", "word", "vad"],
              "timing_bases": ["decoder_segment", "decoder_word", "vad"],
              "vad_intervals": true,
              "supported_languages": ["*"],
              "error_codes": [
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
              ],
              "automatic_language_detection": true,
              "effective_language": "ja",
              "response_formats": ["json", "verbose_json"],
              "confidence_evidence": {
                "word_probability": true,
                "decoder_token_probabilities": true,
                "segment_average_log_probability": true,
                "segment_no_speech_probability": true,
                "detected_language_probability": true
              },
              "optional_features": {
                "dual_output": true,
                "dual_output_decode_count": 2,
                "progress": true,
                "cancellation": true
              },
              "limits": {
                "maximum_upload_bytes": 123456,
                "maximum_audio_duration_seconds": 7200
              },
              "limitations": ["vad_confidence_unavailable"],
              "configuration_can_change": true
            }
            """;

        WhisperRouteCapabilities capabilities = WhisperServer.ParseCapabilities(Json);

        Assert.True(capabilities.HasExtensionContract);
        Assert.Equal("1.0", capabilities.ExtensionSchemaVersion);
        Assert.Equal("translate", capabilities.Task);
        Assert.Equal("synthetic-model", capabilities.SelectedModel);
        Assert.True(capabilities.ModelFilesCached);
        Assert.Equal(["segment", "word", "vad"], capabilities.TimingGranularities);
        Assert.True(capabilities.VadIntervals);
        Assert.Equal(123456, capabilities.MaximumUploadBytes);
        Assert.Equal(7200, capabilities.MaximumAudioDurationSeconds);
        Assert.Contains("vad_confidence_unavailable", capabilities.Limitations);
        Assert.Equal("synthetic-build", capabilities.BuildIdentity);
        Assert.Equal(["decoder_segment", "decoder_word", "vad"], capabilities.TimingBases);
        Assert.Equal(["json", "verbose_json"], capabilities.ResponseFormats);
        Assert.True(capabilities.DualOutput);
        Assert.Equal(2, capabilities.DualOutputDecodeCount);
        Assert.True(capabilities.Progress);
        Assert.True(capabilities.Cancellation);
        Assert.True(capabilities.WordProbability);
        Assert.True(capabilities.DecoderTokenProbabilities);
        Assert.True(capabilities.SegmentAverageLogProbability);
        Assert.True(capabilities.SegmentNoSpeechProbability);
        Assert.True(capabilities.DetectedLanguageProbability);
        Assert.Contains("backend_failure", capabilities.ErrorCodes!);
        WhisperServer.ValidateCapabilitiesContract(capabilities, "translate");

        Assert.Throws<JsonException>(() => WhisperServer.ValidateCapabilitiesContract(
            capabilities with { ModelFilesCached = null },
            "translate"));
        Assert.Throws<JsonException>(() => WhisperServer.ValidateCapabilitiesContract(
            capabilities with { TimingBases = ["synthetic_timing"] },
            "translate"));
        Assert.Throws<JsonException>(() => WhisperServer.ValidateCapabilitiesContract(
            capabilities with { ResponseFormats = ["json"] },
            "translate"));
        Assert.Throws<JsonException>(() => WhisperServer.ValidateCapabilitiesContract(
            capabilities with { WordProbability = null },
            "translate"));
        Assert.Throws<JsonException>(() => WhisperServer.ValidateCapabilitiesContract(
            capabilities with { ErrorCodes = ["request_not_found"] },
            "translate"));
    }

    [Theory]
    [InlineData("transcribe", false, true)]
    [InlineData("transcribe", true, true)]
    [InlineData("translate", false, true)]
    [InlineData("translate", true, false)]
    public void ParseTranscript_DistinguishesTaskTimingAndReliability(
        string task,
        bool includeWordTiming,
        bool reliable)
    {
        string words = includeWordTiming
            ? """, "words": [{ "start": 1.1, "end": 1.8, "word": "Synthetic" }]"""
            : string.Empty;
        string actual = includeWordTiming ? "word" : "segment";
        string basis = includeWordTiming ? "decoder_word" : "decoder_segment";
        string json = $$"""
            {
              "segments": [
                { "start": 1.0, "end": 2.0, "text": "Synthetic"{{words}} }
              ],
              "x_whisper_server": {
                "task": "{{task}}",
                "timing": {
                  "requested": ["{{actual}}"],
                  "actual": ["{{actual}}"],
                  "basis": "{{basis}}",
                  "reliable": {{reliable.ToString().ToLowerInvariant()}},
                  "reliability": "{{(reliable ? "decoder-provided" : "approximate")}}"
                }
              }
            }
            """;

        WhisperTranscript transcript = WhisperServer.ParseTranscript(json);

        Assert.Equal(task, transcript.Metadata.Task);
        Assert.Equal(actual, Assert.Single(transcript.Metadata.Timing?.Actual ?? []));
        Assert.Equal(reliable, transcript.Metadata.Timing?.Reliable);
        Assert.All(
            transcript.Segments,
            segment => Assert.Equal(
                includeWordTiming ? WhisperTimingGranularity.Word : WhisperTimingGranularity.Segment,
                segment.TimingGranularity));
    }

    [Fact]
    public void ParseApiError_PreservesStableMachineReadableCode()
    {
        const string Json = """
            {
              "error": {
                "code": "word_timestamps_unavailable",
                "message": "The selected backend cannot provide genuine word timestamps",
                "selected_model": "synthetic-model",
                "available": ["segment", "vad"]
              }
            }
            """;

        WhisperApiException exception = WhisperServer.ParseApiError(HttpStatusCode.UnprocessableEntity, Json);

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("word_timestamps_unavailable", exception.Code);
        Assert.Contains("genuine word timestamps", exception.Message, StringComparison.Ordinal);
        Assert.Contains("synthetic-model", exception.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateResponseContract_RejectsSilentWordTimingDowngrade()
    {
        const string Json = """
            {
              "segments": [
                { "start": 1.0, "end": 2.0, "text": "Synthetic phrase" }
              ],
              "x_whisper_server": {
                "schema_version": "1.0",
                "request_id": "synthetic-request",
                "task": "transcribe",
                "timing": {
                  "requested": ["word"],
                  "actual": ["segment"],
                  "basis": "decoder_segment",
                  "reliable": true,
                  "reliability": "decoder-provided"
                }
              }
            }
            """;
        WhisperTranscript transcript = WhisperServer.ParseTranscript(Json);

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: false,
                requireWordTimestamps: true));

        Assert.Equal("timing_granularity_downgraded", exception.Code);
        Assert.Contains("reported=segment", exception.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateResponseContract_RejectsSilentVadTimingDowngrade()
    {
        const string Json = """
            {
              "segments": [
                { "start": 1.0, "end": 2.0, "text": "Synthetic phrase" }
              ],
              "x_whisper_server": {
                "schema_version": "1.0",
                "request_id": "synthetic-request",
                "task": "translate",
                "timing": {
                  "requested": ["segment", "vad"],
                  "actual": ["segment"],
                  "basis": "decoder_segment",
                  "reliable": true,
                  "reliability": "decoder-provided"
                }
              }
            }
            """;
        WhisperTranscript transcript = WhisperServer.ParseTranscript(Json);

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: true,
                requireWordTimestamps: false,
                requireVadIntervals: true));

        Assert.Equal("timing_granularity_downgraded", exception.Code);
        Assert.Contains("required=vad", exception.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateResponseContract_RejectsWrongTask()
    {
        const string Json = """
            {
              "segments": [
                {
                  "start": 1.0,
                  "end": 2.0,
                  "text": "Synthetic",
                  "words": [{ "start": 1.1, "end": 1.8, "word": "Synthetic" }]
                }
              ],
              "x_whisper_server": {
                "schema_version": "1.0",
                "request_id": "synthetic-request",
                "task": "transcribe",
                "timing": {
                  "requested": ["word"],
                  "actual": ["word"],
                  "basis": "decoder_word",
                  "reliable": true,
                  "reliability": "decoder-provided",
                  "channels": {
                    "word": {
                      "basis": "decoder_word",
                      "reliable": true,
                      "reliability": "decoder-provided"
                    },
                    "vad": {
                      "basis": "vad",
                      "reliable": true,
                      "reliability": "synthetic-vad"
                    }
                  }
                }
              }
            }
            """;
        WhisperTranscript transcript = WhisperServer.ParseTranscript(Json);

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: true,
                requireWordTimestamps: true));

        Assert.Equal("response_contract_mismatch", exception.Code);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("2.0")]
    [InlineData("future")]
    public void ValidateResponseContract_RejectsUnsupportedExtensionSchemaMajor(string schemaVersion)
    {
        string json = $$"""
            {
              "segments": [
                { "start": 1.0, "end": 2.0, "text": "Synthetic phrase" }
              ],
              "x_whisper_server": {
                "schema_version": "{{schemaVersion}}",
                "task": "transcribe"
              }
            }
            """;
        WhisperTranscript transcript = WhisperServer.ParseTranscript(json);

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: false,
                requireWordTimestamps: false));

        Assert.Equal("unsupported_extension_schema_version", exception.Code);
        Assert.Contains("supported_major=1", exception.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateResponseContract_RejectsMissingExtensionSchemaVersion()
    {
        const string Json = """
            {
              "segments": [
                { "start": 1.0, "end": 2.0, "text": "Synthetic phrase" }
              ],
              "x_whisper_server": {
                "task": "transcribe"
              }
            }
            """;
        WhisperTranscript transcript = WhisperServer.ParseTranscript(Json);

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: false,
                requireWordTimestamps: false));

        Assert.Equal("missing_extension_schema_version", exception.Code);
    }

    [Fact]
    public void ValidateResponseContract_AcceptsCompleteVersionOneProvenance()
    {
        WhisperTranscript transcript = WhisperServer.ParseTranscript(CompleteContractResponse(includeBuildIdentity: true));

        WhisperServer.ValidateResponseContract(
            transcript,
            translateToEnglish: false,
            requireWordTimestamps: true,
            requireVadIntervals: true);
    }

    [Fact]
    public void ValidateResponseContract_RejectsVadRelabeledFromDecoderSegments()
    {
        WhisperTranscript transcript = WhisperServer.ParseTranscript(CompleteContractResponse(includeBuildIdentity: true));
        WhisperTimingProvenance timing = Assert.IsType<WhisperTimingProvenance>(transcript.Metadata.Timing);
        Dictionary<string, WhisperTimingChannelProvenance> channels = new(
            timing.Channels!,
            StringComparer.Ordinal)
        {
            ["vad"] = new("decoder_segment", true, "decoder-provided")
        };
        transcript = transcript with
        {
            Metadata = transcript.Metadata with
            {
                Timing = timing with { Channels = channels }
            }
        };

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: false,
                requireWordTimestamps: true,
                requireVadIntervals: true));

        Assert.Equal("timing_granularity_downgraded", exception.Code);
        Assert.Contains("required=vad", exception.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateResponseContract_RejectsIncompleteVersionOneProvenance()
    {
        WhisperTranscript transcript = WhisperServer.ParseTranscript(CompleteContractResponse(includeBuildIdentity: false));

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: false,
                requireWordTimestamps: true,
                requireVadIntervals: true));

        Assert.Equal("response_contract_mismatch", exception.Code);
        Assert.Contains("provenance", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateResponseContract_RequiresLanguageConfidenceOrWarning()
    {
        WhisperTranscript transcript = WhisperServer.ParseTranscript(CompleteContractResponse(includeBuildIdentity: true));
        transcript = transcript with
        {
            Metadata = transcript.Metadata with
            {
                DetectedLanguageProbability = null,
                DetectedLanguageProbabilityBasis = string.Empty
            }
        };

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: false,
                requireWordTimestamps: true,
                requireVadIntervals: true));
        Assert.Equal("response_contract_mismatch", exception.Code);

        transcript = transcript with
        {
            Metadata = transcript.Metadata with
            {
                Warnings = ["language_confidence_unavailable"]
            }
        };
        WhisperServer.ValidateResponseContract(
            transcript,
            translateToEnglish: false,
            requireWordTimestamps: true,
            requireVadIntervals: true);
    }

    [Fact]
    public void ValidateResponseContract_AllowsUnavailableDetectedLanguageOnlyWithWarning()
    {
        WhisperTranscript transcript = WhisperServer.ParseTranscript(CompleteContractResponse(includeBuildIdentity: true));
        transcript = transcript with
        {
            Metadata = transcript.Metadata with
            {
                DetectedLanguage = string.Empty,
                DetectedLanguageProbability = null,
                DetectedLanguageProbabilityBasis = string.Empty,
                Warnings = ["language_confidence_unavailable"]
            }
        };

        WhisperServer.ValidateResponseContract(
            transcript,
            translateToEnglish: false,
            requireWordTimestamps: true,
            requireVadIntervals: true);

        transcript = transcript with
        {
            Metadata = transcript.Metadata with
            {
                Warnings = []
            }
        };
        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: false,
                requireWordTimestamps: true,
                requireVadIntervals: true));
        Assert.Equal("response_contract_mismatch", exception.Code);
    }

    [Fact]
    public void ValidateResponseContract_AllowsLegacyResponseForWholeTrackAnalysis()
    {
        const string Json = """
            {
              "segments": [
                { "start": 1.0, "end": 2.0, "text": "Legacy segment" }
              ]
            }
            """;
        WhisperTranscript transcript = WhisperServer.ParseTranscript(Json);

        WhisperServer.ValidateResponseContract(
            transcript,
            translateToEnglish: false,
            requireWordTimestamps: false);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ValidateResponseContract_RejectsLegacyResponseForPrecisionTiming(
        bool requireWordTimestamps,
        bool requireVadIntervals)
    {
        const string Json = """
            {
              "segments": [
                {
                  "start": 1.0,
                  "end": 2.0,
                  "text": "Legacy word",
                  "words": [{ "start": 1.0, "end": 2.0, "word": "Legacy" }]
                }
              ]
            }
            """;
        WhisperTranscript transcript = WhisperServer.ParseTranscript(Json);

        WhisperApiException exception = Assert.Throws<WhisperApiException>(() =>
            WhisperServer.ValidateResponseContract(
                transcript,
                translateToEnglish: false,
                requireWordTimestamps,
                requireVadIntervals));

        Assert.Equal("capability_downgrade", exception.Code);
    }

    private static string CompleteContractResponse(bool includeBuildIdentity)
    {
        string buildIdentity = includeBuildIdentity
            ? "\"build_identity\": \"synthetic-build\","
            : string.Empty;
        return $$"""
            {
              "segments": [
                {
                  "start": 1.0,
                  "end": 2.0,
                  "text": "Synthetic phrase",
                  "words": [{ "start": 1.0, "end": 2.0, "word": "Synthetic" }]
                }
              ],
              "speech_segments": [{ "start": 1.0, "end": 2.0 }],
              "x_whisper_server": {
                "schema_version": "1.0",
                "request_id": "synthetic-request",
                "task": "transcribe",
                "requested_model": "auto",
                "selected_model": "synthetic-model",
                "backend": "synthetic-backend",
                {{buildIdentity}}
                "server_version": "1.2.3",
                "requested_language": "en",
                "detected_language": "en",
                "detected_language_probability": 0.99,
                "detected_language_probability_basis": "synthetic-classifier",
                "effective_language": "en",
                "audio_duration_seconds": 3.0,
                "queue_seconds": 0.01,
                "preparation_seconds": 0.2,
                "inference_seconds": 0.6,
                "cold_start_seconds": 0.0,
                "inference_includes_preparation": false,
                "realtime_factor": 0.2,
                "cache": { "type": "model_files", "hit": true },
                "decode_count": 1,
                "warnings": [],
                "timing": {
                  "requested": ["word", "vad"],
                  "actual": ["word", "vad"],
                  "basis": "decoder_word",
                  "reliable": true,
                  "reliability": "decoder-provided",
                  "channels": {
                    "word": {
                      "basis": "decoder_word",
                      "reliable": true,
                      "reliability": "decoder-provided"
                    },
                    "vad": {
                      "basis": "vad",
                      "reliable": true,
                      "reliability": "synthetic-vad"
                    }
                  }
                }
              }
            }
            """;
    }

    [Fact]
    public void MatchIndividualCues_RejectsSegmentOnlyTiming()
    {
        TranscriptSection section = new(
            0,
            30,
            [new WhisperSegment(10, 14, "distinctive synthetic dialogue phrase", WhisperTimingGranularity.Segment)]);
        SubtitleCue cue = new(10, 14, "distinctive synthetic dialogue phrase");

        IReadOnlyList<CueTimingMatch> matches = TextMatcher.MatchIndividualCues(
            [section],
            [cue],
            60,
            0.6,
            0.05,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions());

        Assert.Empty(matches);
    }

    [Fact]
    public void MatchIndividualCues_UsesGenuineWordTiming()
    {
        TranscriptSection section = new(
            0,
            30,
            [
                new WhisperSegment(10.75, 11.0, "distinctive", WhisperTimingGranularity.Word),
                new WhisperSegment(11.75, 12.0, "synthetic", WhisperTimingGranularity.Word),
                new WhisperSegment(12.75, 13.0, "dialogue", WhisperTimingGranularity.Word),
                new WhisperSegment(13.75, 14.0, "phrase", WhisperTimingGranularity.Word)
            ]);
        SubtitleCue cue = new(10, 14, "distinctive synthetic dialogue phrase");

        CueTimingMatch match = Assert.Single(TextMatcher.MatchIndividualCues(
            [section],
            [cue],
            60,
            0.6,
            0.05,
            enableFuzzyMatching: false,
            DefaultFuzzyOptions()));

        Assert.Equal(0.25, match.OffsetSeconds, precision: 6);
        Assert.Equal(4, match.MatchedWords);
        Assert.False(match.Fuzzy);
    }

    private static GuidedFuzzyMatchOptions DefaultFuzzyOptions() => new(
        0.55,
        0.08,
        3,
        0.82,
        UseStemming: true,
        UsePhoneticMatching: true,
        0.4);
}
