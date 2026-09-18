#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -eq 0 ]; then
    echo "usage: $0 BASE_URL [BASE_URL ...]" >&2
    exit 64
fi

for command_name in curl jq; do
    command -v "$command_name" >/dev/null 2>&1 || {
        echo "$command_name is required" >&2
        exit 69
    }
done

work_dir=$(mktemp -d "${TMPDIR:-/tmp}/subtitle-aligner-contract.XXXXXX")
trap 'rm -rf "$work_dir"' EXIT HUP INT TERM

if [ -n "${SPEECH_CONTRACT_PROBE_FILE:-}" ]; then
    [ -r "$SPEECH_CONTRACT_PROBE_FILE" ] || {
        echo "SPEECH_CONTRACT_PROBE_FILE is not readable" >&2
        exit 66
    }
    cp -- "$SPEECH_CONTRACT_PROBE_FILE" "$work_dir/probe.wav"
else
    command -v ffmpeg >/dev/null 2>&1 || {
        echo "ffmpeg is required when SPEECH_CONTRACT_PROBE_FILE is unset" >&2
        exit 69
    }
    ffmpeg -nostdin -hide_banner -loglevel error \
        -f lavfi -i 'anullsrc=r=16000:cl=mono' -t 1 \
        -ac 1 -ar 16000 -c:a pcm_s16le "$work_dir/probe.wav"
fi

check_error_code() {
    local response_file=$1
    local expected_code=$2
    jq -e --arg code "$expected_code" \
        '.error.code == $code
        and (.error.message | type == "string" and length > 0)
        and (.error.details | type == "object")
        and ((.error.request_id == null) or (.error.request_id | type == "string" and length > 0))' \
        "$response_file" >/dev/null
}

validate_request_state() {
    local response_file=$1
    local request_id=$2
    jq -e --arg request_id "$request_id" '
        .object == "audio.request"
        and (.schema_version | type == "string" and test("^1\\."))
        and .request_id == $request_id
        and (.state | IN("queued", "running", "completed", "cancelled", "failed"))
        and (.phase | type == "string" and length > 0)
        and ((.completed_work == null) or (.completed_work | type == "number" and . >= 0))
        and ((.total_work == null) or (.total_work | type == "number" and . >= 0))
        and ((.fraction_completed == null)
            or (.fraction_completed | type == "number" and . >= 0 and . <= 1))
        and ((.eta_seconds == null) or (.eta_seconds | type == "number" and . >= 0))
        and (.cancellation_requested | type == "boolean")
    ' "$response_file" >/dev/null
}

validate_capabilities() {
    local capability_file=$1
    local expected_task=$2
    jq -e --arg task "$expected_task" '
        .error_codes as $error_codes
        | .object == "audio.route.capabilities"
        and (.schema_version | type == "string" and test("^1\\."))
        and .task == $task
        and .requested_model == "auto"
        and (.selected_model | type == "string" and length > 0)
        and (.backend | type == "string" and length > 0)
        and (.build_identity | type == "string" and length > 0)
        and (.model_files_cached | type == "boolean")
        and (.supported_tasks | type == "array" and index($task) != null)
        and (.timing_granularities | type == "array" and index("segment") != null)
        and (.timing_bases | type == "array" and length > 0)
        and (.vad_intervals | type == "boolean")
        and (.supported_languages | type == "array" and length > 0)
        and ($error_codes | type == "array")
        and ([
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
        ] | all(. as $code | $error_codes | index($code) != null))
        and (.automatic_language_detection | type == "boolean")
        and ((.effective_language == null) or (.effective_language | type == "string" and length > 0))
        and (.response_formats | index("verbose_json") != null)
        and (.confidence_evidence.word_probability | type == "boolean")
        and (.confidence_evidence.decoder_token_probabilities | type == "boolean")
        and (.confidence_evidence.segment_average_log_probability | type == "boolean")
        and (.confidence_evidence.segment_no_speech_probability | type == "boolean")
        and (.confidence_evidence.detected_language_probability | type == "boolean")
        and (.optional_features.dual_output | type == "boolean")
        and (.optional_features.dual_output_decode_count | type == "number" and . >= 1)
        and (.optional_features.progress | type == "boolean")
        and (.optional_features.cancellation | type == "boolean")
        and (.limits.maximum_upload_bytes | type == "number" and . >= 0)
        and (.limits.maximum_audio_duration_seconds | type == "number" and . >= 0)
        and (.limitations | type == "array")
        and (.configuration_can_change | type == "boolean")
    ' "$capability_file" >/dev/null
}

validate_response() {
    local response_file=$1
    local request_id=$2
    local expected_task=$3
    local requested_json=$4
    local capability_file=$5
    jq -e \
        --arg request_id "$request_id" \
        --arg task "$expected_task" \
        --argjson requested "$requested_json" \
        --slurpfile capabilities "$capability_file" '
        . as $root
        | $capabilities[0] as $route
        | def words: [($root.words // [])[], (($root.segments // [])[] | (.words // [])[])];
        (.x_whisper_server.schema_version | type == "string" and test("^1\\."))
        and .x_whisper_server.request_id == $request_id
        and .x_whisper_server.task == $task
        and .x_whisper_server.requested_model == "auto"
        and (.x_whisper_server.selected_model | type == "string" and length > 0)
        and (.x_whisper_server.backend | type == "string" and length > 0)
        and (.x_whisper_server.server_version | type == "string" and length > 0)
        and (.x_whisper_server.build_identity | type == "string" and length > 0)
        and (.x_whisper_server.requested_language | type == "string" and length > 0)
        and ((.x_whisper_server.detected_language | type == "string" and length > 0)
            or (.x_whisper_server.detected_language == null
                and (.x_whisper_server.warnings | index("language_confidence_unavailable") != null)))
        and (.x_whisper_server.effective_language | type == "string" and length > 0)
        and ($requested | all(. as $granularity | $root.x_whisper_server.timing.requested | index($granularity) != null))
        and ($requested | all(. as $granularity | $root.x_whisper_server.timing.actual | index($granularity) != null))
        and (.x_whisper_server.timing.basis | IN("decoder_word", "decoder_segment", "forced_alignment", "vad"))
        and (.x_whisper_server.timing.reliable | type == "boolean")
        and (.x_whisper_server.timing.reliability | type == "string" and length > 0)
        and (.x_whisper_server.timing.actual | all(
            . as $granularity
            | $root.x_whisper_server.timing.channels[$granularity] as $channel
            | ($channel | type == "object")
            and ($channel.basis | IN("decoder_word", "decoder_segment", "forced_alignment", "vad"))
            and ($channel.reliable | type == "boolean")
            and ($channel.reliability | type == "string" and length > 0)))
        and ((.x_whisper_server.timing.actual | index("vad") == null)
            or .x_whisper_server.timing.channels.vad.basis == "vad")
        and ((.x_whisper_server.timing.actual | index("word") == null)
            or (.x_whisper_server.timing.channels.word.basis
                | IN("decoder_word", "forced_alignment")))
        and (.x_whisper_server.audio_duration_seconds | type == "number" and . >= 0)
        and (.x_whisper_server.queue_seconds | type == "number" and . >= 0)
        and (.x_whisper_server.preparation_seconds | type == "number" and . >= 0)
        and (.x_whisper_server.inference_seconds | type == "number" and . >= 0)
        and (.x_whisper_server.cold_start_seconds | type == "number" and . >= 0)
        and (.x_whisper_server.inference_includes_preparation | type == "boolean")
        and (.x_whisper_server.realtime_factor | type == "number" and . >= 0)
        and (.x_whisper_server.cache.type | type == "string" and length > 0)
        and (.x_whisper_server.cache.hit | type == "boolean")
        and (.x_whisper_server.decode_count | type == "number" and . >= 1)
        and (.x_whisper_server.warnings | type == "array")
        and (.speech_segments | type == "array")
        and ([.speech_segments[]] | all(
            . as $interval
            | (.start | type == "number" and . >= 0)
            and ($interval.end | type == "number" and . >= $interval.start
                and . <= $root.x_whisper_server.audio_duration_seconds)
            and (($interval.confidence == null)
                or ($interval.confidence | type == "number" and . >= 0 and . <= 1))))
        and (($route.confidence_evidence.detected_language_probability | not)
            or ((.x_whisper_server.detected_language_probability | type == "number" and . >= 0 and . <= 1)
                and (.x_whisper_server.detected_language_probability_basis | type == "string" and length > 0))
            or (.x_whisper_server.warnings | index("language_confidence_unavailable") != null))
        and (($route.confidence_evidence.segment_average_log_probability | not)
            or ([.segments[]] | all(.avg_logprob | type == "number")))
        and (($route.confidence_evidence.segment_no_speech_probability | not)
            or ([.segments[]] | all(.no_speech_prob | type == "number" and . >= 0 and . <= 1)))
        and ((($requested | index("word")) == null)
            or (($route.confidence_evidence.word_probability | not)
                or (words | all(.probability | type == "number" and . >= 0 and . <= 1))))
        and ((($requested | index("word")) == null)
            or (($route.confidence_evidence.decoder_token_probabilities | not)
                or (words | all(
                    (.x_whisper_server.decoder_token_probabilities | type == "array" and length > 0
                        and all(type == "number" and . >= 0 and . <= 1))
                    and (.x_whisper_server.probability_basis | type == "string" and length > 0)))))
    ' "$response_file" >/dev/null
}

expect_capability_error() {
    local base_url=$1
    local probe_file=$2
    local field_name=$3
    local field_value=$4
    local expected_code=$5
    local response_file=$6
    local status
    status=$(curl --silent --show-error -o "$response_file" -w '%{http_code}' \
        "$base_url/v1/audio/transcriptions" \
        -F "file=@$probe_file;type=audio/wav" \
        -F 'model=auto' \
        -F 'language=en' \
        -F 'response_format=verbose_json' \
        -F "$field_name=$field_value")
    [[ "$status" =~ ^4 ]]
    check_error_code "$response_file" "$expected_code"
}

check_base_url() {
    local base_url=${1%/}
    local endpoint_index=$2
    local capability_file="$work_dir/capabilities.json"
    local word_capability_file="$work_dir/word-capabilities.json"
    local resolved_capability_file="$work_dir/resolved-capabilities.json"
    local response_file="$work_dir/response.json"
    local error_file="$work_dir/error.json"
    local request_id requested_json status word_capability_status required_timing
    local translation_capability_file translation_response_file resolved_translation_capability_file
    local translation_request_id translation_requested expected_decodes granularity
    local state_file cancel_file cancel_again_file cancel_alias_file terminal_state
    local -a granularities curl_args translation_args translation_granularities

    curl --fail-with-body --silent --show-error \
        "$base_url/v1/audio/capabilities?task=transcribe&model=auto&language=en" \
        -o "$capability_file"
    if ! validate_capabilities "$capability_file" transcribe; then
        echo "speech-service contract v1 failed: transcribe capabilities are incomplete at $base_url" >&2
        return 1
    fi

    # model=auto may resolve a fast default route until the caller declares a
    # hard timing requirement. Probe word timing through the same negotiated
    # capability route used by the real request instead of assuming the
    # unconstrained route describes every compatible backend.
    word_capability_status=$(curl --silent --show-error -o "$word_capability_file" -w '%{http_code}' \
        "$base_url/v1/audio/capabilities?task=transcribe&model=auto&language=en&timestamp_granularities=word")
    if [ "$word_capability_status" = 200 ]; then
        if ! validate_capabilities "$word_capability_file" transcribe \
            || ! jq -e '.timing_granularities | index("word") != null' "$word_capability_file" >/dev/null; then
            echo "speech-service contract v1 failed: the word-timing route did not honor its hard requirement at $base_url" >&2
            return 1
        fi
        cp -- "$word_capability_file" "$capability_file"
    elif [[ "$word_capability_status" =~ ^4 ]]; then
        check_error_code "$word_capability_file" word_timestamps_unavailable
    else
        echo "speech-service contract v1 failed: word-timing capability negotiation returned HTTP $word_capability_status at $base_url" >&2
        return 1
    fi

    status=$(curl --silent --show-error -o "$error_file" -w '%{http_code}' \
        "$base_url/v1/audio/capabilities?task=transcribe&model=auto&language=en&schema_version=10.0")
    [[ "$status" == 400 ]]
    check_error_code "$error_file" unsupported_extension_schema_version

    request_id="subtitle-aligner-contract-$$-$endpoint_index"
    granularities=(segment)
    if jq -e '.timing_granularities | index("word") != null' "$capability_file" >/dev/null; then
        granularities+=(word)
    fi
    if jq -e '.vad_intervals' "$capability_file" >/dev/null; then
        granularities+=(vad)
    fi
    required_timing=$(IFS=,; echo "${granularities[*]}")
    curl --fail-with-body --silent --show-error \
        "$base_url/v1/audio/capabilities?task=transcribe&model=auto&language=en&timestamp_granularities=$required_timing" \
        -o "$resolved_capability_file"
    if ! validate_capabilities "$resolved_capability_file" transcribe \
        || ! jq -e --argjson requested "$(jq -cn --args '$ARGS.positional' "${granularities[@]}")" \
            '. as $route
            | $requested | all(. as $granularity
                | if $granularity == "vad"
                    then $route.vad_intervals
                    else $route.timing_granularities | index($granularity) != null
                  end)' \
            "$resolved_capability_file" >/dev/null; then
        echo "speech-service contract v1 failed: resolved transcribe capabilities do not satisfy the request at $base_url" >&2
        return 1
    fi
    cp -- "$resolved_capability_file" "$capability_file"
    curl_args=(
        --fail-with-body --silent --show-error
        "$base_url/v1/audio/transcriptions"
        -F "file=@$work_dir/probe.wav;type=audio/wav"
        -F 'model=auto'
        -F 'language=en'
        -F 'response_format=verbose_json'
        -F 'temperature=0.0'
        -F "request_id=$request_id"
        -o "$response_file"
    )
    for granularity in "${granularities[@]}"; do
        curl_args+=( -F "timestamp_granularities[]=$granularity" )
    done
    curl "${curl_args[@]}"
    requested_json=$(jq -cn --args '$ARGS.positional' "${granularities[@]}")
    validate_response "$response_file" "$request_id" transcribe "$requested_json" "$capability_file"
    if [ "${SPEECH_CONTRACT_EXPECT_SPEECH:-0}" = 1 ]; then
        jq -e --argjson requested "$requested_json" '
            (.segments | type == "array" and length > 0)
            and (($requested | index("vad") == null) or (.speech_segments | length > 0))
            and (($requested | index("word") == null)
                or ([(.words // [])[], (.segments[] | (.words // [])[])] | length > 0))
        ' "$response_file" >/dev/null
    fi

    if ! jq -e '.timing_granularities | index("word") != null' "$capability_file" >/dev/null; then
        expect_capability_error \
            "$base_url" "$work_dir/probe.wav" 'timestamp_granularities[]' word \
            word_timestamps_unavailable "$error_file"
    fi
    if ! jq -e '.vad_intervals' "$capability_file" >/dev/null; then
        expect_capability_error \
            "$base_url" "$work_dir/probe.wav" 'timestamp_granularities[]' vad \
            vad_intervals_unavailable "$error_file"
    fi

    if jq -e '.supported_tasks | index("translate") != null' "$capability_file" >/dev/null; then
        translation_capability_file="$work_dir/translation-capabilities.json"
        resolved_translation_capability_file="$work_dir/resolved-translation-capabilities.json"
        translation_response_file="$work_dir/translation-response.json"
        curl --fail-with-body --silent --show-error \
            "$base_url/v1/audio/capabilities?task=translate&model=auto&language=ja" \
            -o "$translation_capability_file"
        if ! validate_capabilities "$translation_capability_file" translate; then
            echo "speech-service contract v1 failed: translation capabilities are incomplete at $base_url" >&2
            return 1
        fi
        translation_request_id="$request_id-translate"
        translation_args=(
            --fail-with-body --silent --show-error
            "$base_url/v1/audio/translations"
            -F "file=@$work_dir/probe.wav;type=audio/wav"
            -F 'model=auto'
            -F 'language=ja'
            -F 'response_format=verbose_json'
            -F 'temperature=0.0'
            -F "request_id=$translation_request_id"
            -o "$translation_response_file"
        )
        translation_granularities=(segment)
        if jq -e '.timing_granularities | index("word") != null' "$translation_capability_file" >/dev/null; then
            translation_granularities+=(word)
        fi
        if jq -e '.vad_intervals' "$translation_capability_file" >/dev/null; then
            translation_granularities+=(vad)
        fi
        required_timing=$(IFS=,; echo "${translation_granularities[*]}")
        curl --fail-with-body --silent --show-error \
            "$base_url/v1/audio/capabilities?task=translate&model=auto&language=ja&timestamp_granularities=$required_timing&dual_output=true" \
            -o "$resolved_translation_capability_file"
        if ! validate_capabilities "$resolved_translation_capability_file" translate \
            || ! jq -e --argjson requested "$(jq -cn --args '$ARGS.positional' "${translation_granularities[@]}")" '
                . as $route
                | ($requested | all(. as $granularity
                    | if $granularity == "vad"
                        then $route.vad_intervals
                        else $route.timing_granularities | index($granularity) != null
                      end))
                and $route.optional_features.dual_output
            ' "$resolved_translation_capability_file" >/dev/null; then
            echo "speech-service contract v1 failed: resolved translation capabilities do not satisfy the request at $base_url" >&2
            return 1
        fi
        cp -- "$resolved_translation_capability_file" "$translation_capability_file"
        for granularity in "${translation_granularities[@]}"; do
            translation_args+=( -F "timestamp_granularities[]=$granularity" )
        done
        translation_requested=$(jq -cn --args '$ARGS.positional' "${translation_granularities[@]}")
        if jq -e '.optional_features.dual_output' "$translation_capability_file" >/dev/null; then
            translation_args+=( -F 'dual_output=true' )
        fi
        curl "${translation_args[@]}"
        validate_response \
            "$translation_response_file" "$translation_request_id" translate "$translation_requested" \
            "$translation_capability_file"
        if [ "${SPEECH_CONTRACT_EXPECT_SPEECH:-0}" = 1 ]; then
            jq -e --argjson requested "$translation_requested" '
                (.segments | type == "array" and length > 0)
                and (($requested | index("vad") == null) or (.speech_segments | length > 0))
                and (($requested | index("word") == null)
                    or ([(.words // [])[], (.segments[] | (.words // [])[])] | length > 0))
            ' "$translation_response_file" >/dev/null
        fi
        if jq -e '.optional_features.dual_output' "$translation_capability_file" >/dev/null; then
            expected_decodes=$(jq '.optional_features.dual_output_decode_count' "$translation_capability_file")
            jq -e --argjson expected_decodes "$expected_decodes" '
                (.source_segments | type == "array")
                and (.translation_segments | type == "array")
                and .x_whisper_server.decode_count == $expected_decodes
                and ([.source_segments[], .translation_segments[]] | all(
                    . as $segment
                    | (.segment_id | type == "string" and length > 0)
                    and (.start | type == "number" and . >= 0)
                    and ($segment.end | type == "number" and . >= $segment.start)))
                and ((.source_segments | map(.segment_id) | length)
                    == (.source_segments | map(.segment_id) | unique | length))
                and ((.translation_segments | map(.segment_id) | length)
                    == (.translation_segments | map(.segment_id) | unique | length))
                and ((.source_segments | map(.segment_id) | sort)
                    == (.translation_segments | map(.segment_id) | sort))
                and ((env.SPEECH_CONTRACT_EXPECT_SPEECH // "0") != "1"
                    or ((.source_segments | length) > 0 and (.translation_segments | length) > 0))
            ' "$translation_response_file" >/dev/null
        fi
    else
        status=$(curl --silent --show-error -o "$error_file" -w '%{http_code}' \
            "$base_url/v1/audio/translations" \
            -F "file=@$work_dir/probe.wav;type=audio/wav" \
            -F 'model=auto' \
            -F 'language=auto' \
            -F 'response_format=verbose_json')
        [[ "$status" =~ ^4 ]]
        check_error_code "$error_file" model_task_mismatch
    fi

    if jq -e '.optional_features.progress' "$capability_file" >/dev/null; then
        state_file="$work_dir/request-state.json"
        curl --fail-with-body --silent --show-error \
            "$base_url/v1/audio/requests/$request_id" -o "$state_file"
        validate_request_state "$state_file" "$request_id"

        if jq -e '.optional_features.cancellation' "$capability_file" >/dev/null; then
            cancel_file="$work_dir/cancel.json"
            cancel_again_file="$work_dir/cancel-again.json"
            cancel_alias_file="$work_dir/cancel-alias.json"
            curl --fail-with-body --silent --show-error -X DELETE \
                "$base_url/v1/audio/requests/$request_id" -o "$cancel_file"
            curl --fail-with-body --silent --show-error -X DELETE \
                "$base_url/v1/audio/requests/$request_id" -o "$cancel_again_file"
            curl --fail-with-body --silent --show-error -X POST \
                --data '' \
                "$base_url/v1/audio/cancellations/$request_id" -o "$cancel_alias_file"
            validate_request_state "$cancel_file" "$request_id"
            validate_request_state "$cancel_again_file" "$request_id"
            validate_request_state "$cancel_alias_file" "$request_id"
            terminal_state=$(jq -r '.state' "$cancel_file")
            jq -e --arg state "$terminal_state" '.state == $state' \
                "$cancel_again_file" "$cancel_alias_file" >/dev/null
        fi
    fi

    status=$(curl --silent --show-error -o "$error_file" -w '%{http_code}' \
        -X DELETE "$base_url/v1/audio/requests/subtitle-aligner-contract-missing")
    [[ "$status" == 404 ]]
    check_error_code "$error_file" request_not_found

    status=$(curl --silent --show-error -o "$error_file" -w '%{http_code}' \
        "$base_url/v1/audio/capabilities?task=transcribe&model=auto&language=not-a-language")
    [[ "$status" == 400 ]]
    check_error_code "$error_file" invalid_language

    echo "speech-service contract v1 passed: $base_url"
}

endpoint_index=0
for base_url in "$@"; do
    check_base_url "$base_url" "$endpoint_index"
    endpoint_index=$((endpoint_index + 1))
done
