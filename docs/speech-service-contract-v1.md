# Speech service extension contract v1

Subtitle Aligner uses the same speech-service contract for an external server
and for its plugin-managed NAS service. A backend may advertise fewer
capabilities, but it must use this schema to do so. The client must never infer
precision from the server identity, silently invent timing, or treat an
unadvertised feature as available.

The contract extends, rather than replaces, the OpenAI-compatible audio API.
Ordinary clients can ignore `x_whisper_server`. Extension-aware clients must
reject an unsupported `schema_version` major.

## Units and identifiers

- All durations and timestamps are finite JSON numbers in seconds.
- Fractions and probabilities are finite JSON numbers from 0 through 1.
- Byte limits are non-negative JSON integers.
- A request ID contains 1–128 ASCII letters, numbers, dots, underscores, or
  hyphens.
- Language values are normalized BCP 47 or ISO 639 codes, `auto`, `*`, or
  `null` where the field explicitly allows it.
- The current extension schema version is `1.0`.

## Route capabilities

`GET /v1/audio/capabilities?task=transcribe&model=auto&language=en&timestamp_granularities=word,vad`
resolves the actual route without running inference. The optional comma-separated
`timestamp_granularities` query parameter and optional `dual_output=true` query
parameter are hard route requirements. With `model=auto`, the server must filter
out incompatible backends before selecting its preferred model. With `default`
or an explicit model ID, model selection remains strict and the returned
capabilities may show that the requested feature is unavailable. The response is
not cacheable and has this shape:

```json
{
  "object": "audio.route.capabilities",
  "schema_version": "1.0",
  "task": "transcribe",
  "requested_model": "auto",
  "selected_model": "example-model",
  "backend": "example-backend",
  "build_identity": "example-build",
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
  "effective_language": "en",
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
    "maximum_upload_bytes": 1073741824,
    "maximum_audio_duration_seconds": 21600
  },
  "limitations": ["vad_confidence_unavailable"],
  "configuration_can_change": true
}
```

`selected_model`, `backend`, and `build_identity` identify the resolved route,
not a private model path. `timing_granularities` and `timing_bases` list only
measurements the route can genuinely return. When a feature is unavailable,
the corresponding Boolean is `false` and `limitations` contains a stable code.
`error_codes` must include the complete required v1 set shown above. It does
not imply that a live conformance run should deliberately induce overload or a
backend failure.
`confidence_evidence` makes absence explicit: every field is required, and a
`true` value promises that the corresponding raw decoder evidence will be
present whenever that response object exists. A service must not replace these
measurements with an undocumented aggregate score.

The optional dual output is an additional decode. A client must request it
explicitly with `dual_output=true`, and only after capabilities advertise it.
`dual_output_decode_count` states the expected total decode count so a
resource-constrained host can decline it before uploading audio.

Dual output returns `source_segments` and `translation_segments` with the same
non-empty, unique `segment_id` set. Each translation is assigned to at most one
source segment; a service must not copy one translated phrase onto every
overlapping source segment. When no translation can be paired confidently, the
corresponding translation text is empty. Clients use the source segment's
timing and the paired translation's text; translation-pass timestamps are not
promoted to source-speech timing. The ordinary translated `segments` remain in
the same response so a client can reject a source-linked onset when the
independent translation decode places the passage elsewhere. That channel is a
corroboration or veto signal, not the correction timestamp.

## Inference request

The service implements both:

- `POST /v1/audio/transcriptions`
- `POST /v1/audio/translations`

The multipart form uses the standard `file`, `model`, `language`,
`response_format`, and `temperature` fields and these extensions:

- `request_id`: caller-generated stable request identifier;
- `timestamp_granularities[]`: repeatable `segment`, `word`, or `vad` values;
- `dual_output=true`: opt-in source transcription plus English translation;
- `stream=true`: optional server-sent events for routes that support streaming.

`optional_features.progress=true` guarantees request-status polling; it does
not imply that every model/backend/response-format combination accepts
`stream=true`. A client that needs a transport-independent progress path sends
`request_id`, makes an ordinary non-streaming inference request, and polls the
request-status endpoint described below. This is also the required progress
path for `dual_output=true`.

`timestamp_granularities[]` requires `response_format=verbose_json`. The
service either satisfies every requested granularity or returns a
machine-readable error. It must not silently downgrade `word` to `segment`.

## Inference response

The standard `text`, `language`, `segments`, and `words` fields retain their
OpenAI-compatible meanings. Absolute VAD intervals use:

```json
"speech_segments": [
  { "start": 1.02, "end": 1.96, "confidence": 0.91 }
]
```

If the backend has no calibrated VAD confidence, it omits `confidence` and
adds `vad_confidence_unavailable` to `warnings`.

Speech intervals are request-scoped. A response must contain only intervals
derived from that request's audio; a no-speech response returns an empty
`speech_segments` array even when the backend reuses a model or inference
context that previously processed speech.

Every successful extension-aware response includes:

```json
"x_whisper_server": {
  "schema_version": "1.0",
  "request_id": "example-request",
  "task": "transcribe",
  "requested_model": "auto",
  "selected_model": "example-model",
  "backend": "example-backend",
  "server_version": "1.2.3",
  "build_identity": "example-build",
  "requested_language": "en",
  "detected_language": "en",
  "detected_language_probability": 0.99,
  "detected_language_probability_basis": "decoder_language_classifier",
  "effective_language": "en",
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
        "reliability": "speech-activity-detector"
      }
    }
  },
  "audio_duration_seconds": 3.0,
  "queue_seconds": 0.01,
  "preparation_seconds": 0.20,
  "inference_seconds": 0.60,
  "cold_start_seconds": 0.20,
  "inference_includes_preparation": false,
  "realtime_factor": 0.20,
  "cache": { "type": "model_files", "hit": true },
  "decode_count": 1,
  "warnings": []
}
```

`timing.basis` is the primary basis and may contain only `decoder_word`,
`decoder_segment`, `forced_alignment`, or `vad`; `timing.actual` records every
returned timing channel. `timing.channels` contains one entry for every value
in `timing.actual`, using the same allowed bases and explicit reliability
fields. Correctness logic uses the selected channel's provenance—not the
response's primary basis—to decide whether that channel is eligible. In
particular, a returned `vad` channel must have basis `vad`; decoder-segment
boundaries relabeled as VAD are not eligible onset evidence.
Raw decoder evidence stays attached to its natural object: word/token
probability on a word, and average log probability and no-speech probability on
a segment. A server must not invent an undocumented aggregate confidence.

When `dual_output=true`, the response additionally contains `source_segments`
and `translation_segments`. Corresponding entries share a non-empty stable
`segment_id`. The metadata `decode_count` reports the actual number of decodes.

## Progress and cancellation

When capabilities advertise `optional_features.progress=true`, status is
available at `GET /v1/audio/requests/{request_id}` while the ordinary inference
request is in flight. The response contains `phase`, completed and total work
when known, `fraction_completed`, and `eta_seconds` when estimable. A numeric
fraction must never decrease. Unknown measurements are `null`.

When capabilities advertise `optional_features.cancellation=true`, cancellation
is available at both `DELETE /v1/audio/requests/{request_id}` and
`POST /v1/audio/cancellations/{request_id}`. Repeated cancellation is
idempotent: it returns the same terminal state and never starts new work.
Status and cancellation responses use:

```json
{
  "object": "audio.request",
  "schema_version": "1.0",
  "request_id": "example-request",
  "state": "running",
  "phase": "inference",
  "completed_work": null,
  "total_work": null,
  "fraction_completed": 0.5,
  "eta_seconds": 12.4,
  "cancellation_requested": false
}
```

`state` is one of `queued`, `running`, `completed`, `cancelled`, or `failed`.
Cancelling an already-terminal request returns its existing terminal state.

If a route separately accepts `stream=true`, it returns `text/event-stream`.
Its progress payloads follow the same monotonicity rules and its terminal
result contains the ordinary inference response. Streaming is an optional
transport optimization, not the contract-v1 interoperability baseline.

## Errors and warnings

Non-success responses use:

```json
{
  "error": {
    "code": "word_timestamps_unavailable",
    "message": "The resolved route cannot return genuine word timestamps",
    "request_id": "example-request",
    "details": {}
  }
}
```

Stable v1 codes include `unsupported_timing`, `word_timestamps_unavailable`,
`vad_intervals_unavailable`, `model_task_mismatch`, `invalid_language`,
`capability_downgrade`, `overloaded`,
`request_cancelled`, `request_not_found`, `backend_failure`, and
`unsupported_extension_schema_version`. Human-readable messages may change;
correctness logic uses `code`.

## Conformance

Run [`tools/check-speech-service-contract.sh`](../tools/check-speech-service-contract.sh)
against every implementation. The exact same script and assertions must pass
for the external service and the plugin-managed NAS service. A release cannot
claim backend parity based only on unit fixtures.

The checker rejects unsupported schema majors, resolves each advertised route before inference, requests every
timing channel that route claims, verifies routing/timing/performance/cache
provenance, exercises translation and opt-in dual output when advertised, and
requires stable machine-readable errors for unavailable word timing, VAD,
invalid language, and missing cancellation targets. Capability advertisement
is therefore a tested promise rather than descriptive metadata.
