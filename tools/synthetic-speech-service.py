#!/usr/bin/env python3
"""Synthetic contract-v1 services for the shared conformance test."""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from email import policy
from email.parser import BytesParser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse


VALID_LANGUAGES = {"auto", "en", "ja"}


class ContractHandler(BaseHTTPRequestHandler):
    server_version = "SyntheticSpeechService/1.0"

    def log_message(self, format: str, *args: object) -> None:
        return

    @property
    def profile(self) -> str:
        return self.server.profile  # type: ignore[attr-defined]

    def send_json(self, status: int, value: object) -> None:
        body = json.dumps(value, separators=(",", ":")).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_error_code(self, status: int, code: str, message: str) -> None:
        self.send_json(
            status,
            {"error": {"code": code, "message": message, "details": {}}},
        )

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        if parsed.path.startswith("/v1/audio/requests/"):
            request_id = parsed.path.rsplit("/", 1)[-1]
            state = self.server.request_states.get(request_id)  # type: ignore[attr-defined]
            if state is None:
                self.send_error_code(404, "request_not_found", "The request ID does not exist.")
            else:
                self.send_request_state(request_id, state)
            return
        if parsed.path != "/v1/audio/capabilities":
            self.send_error_code(404, "request_not_found", "The requested resource does not exist.")
            return

        query = parse_qs(parsed.query)
        schema_version = query.get("schema_version", ["1.0"])[0]
        if schema_version != "1" and not schema_version.startswith("1."):
            self.send_error_code(
                400,
                "unsupported_extension_schema_version",
                "Only speech-service extension schema major version 1 is supported.",
            )
            return
        language = query.get("language", ["auto"])[0]
        task = query.get("task", ["transcribe"])[0]
        if language not in VALID_LANGUAGES:
            self.send_error_code(400, "invalid_language", "The language code is not supported.")
            return
        requested_timing = {
            value
            for field in query.get("timestamp_granularities", ["segment"])
            for value in field.split(",")
            if value
        }
        if self.profile == "constrained" and "word" in requested_timing:
            self.send_error_code(422, "word_timestamps_unavailable", "Word timestamps are unavailable.")
            return
        if self.profile == "constrained" and "vad" in requested_timing:
            self.send_error_code(422, "vad_intervals_unavailable", "VAD intervals are unavailable.")
            return
        supported_tasks = ["transcribe", "translate"] if self.profile != "constrained" else ["transcribe"]
        if task not in supported_tasks:
            self.send_error_code(422, "model_task_mismatch", "The resolved route does not support this task.")
            return

        full = self.profile != "constrained"
        self.send_json(
            200,
            {
                "object": "audio.route.capabilities",
                "schema_version": "1.0",
                "task": task,
                "requested_model": "auto",
                "selected_model": f"synthetic-{self.profile}",
                "backend": f"synthetic-{self.profile}",
                "build_identity": f"synthetic-{self.profile}-build",
                "model_files_cached": True,
                "supported_tasks": supported_tasks,
                "timing_granularities": ["segment", "word"] if full else ["segment"],
                "timing_bases": ["decoder_segment", "decoder_word", "vad"] if full else ["decoder_segment"],
                "vad_intervals": full,
                "supported_languages": ["en", "ja"],
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
                    "unsupported_extension_schema_version",
                ],
                "automatic_language_detection": True,
                "effective_language": None if language == "auto" else language,
                "response_formats": ["json", "verbose_json"],
                "confidence_evidence": {
                    "word_probability": full,
                    "decoder_token_probabilities": full,
                    "segment_average_log_probability": True,
                    "segment_no_speech_probability": True,
                    "detected_language_probability": True,
                },
                "optional_features": {
                    "dual_output": full,
                    "dual_output_decode_count": 2 if full else 1,
                    "progress": full,
                    "cancellation": full,
                },
                "limits": {
                    "maximum_upload_bytes": 10_000_000,
                    "maximum_audio_duration_seconds": 3600,
                },
                "limitations": [] if full else ["word_timestamps_unavailable", "vad_intervals_unavailable"],
                "configuration_can_change": False,
            },
        )

    def do_DELETE(self) -> None:
        if self.path.startswith("/v1/audio/requests/"):
            request_id = self.path.rsplit("/", 1)[-1]
            state = self.server.request_states.get(request_id)  # type: ignore[attr-defined]
            if state is None:
                self.send_error_code(404, "request_not_found", "The request ID does not exist.")
            else:
                self.send_request_state(request_id, state)
            return
        self.send_error_code(404, "request_not_found", "The requested resource does not exist.")

    def do_POST(self) -> None:
        if self.path.startswith("/v1/audio/cancellations/"):
            request_id = self.path.rsplit("/", 1)[-1]
            state = self.server.request_states.get(request_id)  # type: ignore[attr-defined]
            if state is None:
                self.send_error_code(404, "request_not_found", "The request ID does not exist.")
            else:
                self.send_request_state(request_id, state)
            return
        if self.path == "/v1/audio/translations" and self.profile == "constrained":
            self.send_error_code(422, "model_task_mismatch", "The resolved route does not support translation.")
            return
        if self.path not in {"/v1/audio/transcriptions", "/v1/audio/translations"}:
            self.send_error_code(404, "request_not_found", "The requested resource does not exist.")
            return

        fields = self.read_multipart_fields()
        schema_version = fields.get("schema_version", ["1.0"])[0]
        if schema_version != "1" and not schema_version.startswith("1."):
            self.send_error_code(
                400,
                "unsupported_extension_schema_version",
                "Only speech-service extension schema major version 1 is supported.",
            )
            return
        requested = fields.get("timestamp_granularities[]", ["segment"])
        if "word" in requested and self.profile == "constrained":
            self.send_error_code(422, "word_timestamps_unavailable", "Word timestamps are unavailable.")
            return
        if "vad" in requested and self.profile == "constrained":
            self.send_error_code(422, "vad_intervals_unavailable", "VAD intervals are unavailable.")
            return

        task = "translate" if self.path.endswith("translations") else "transcribe"
        request_id = fields.get("request_id", ["synthetic-request"])[0]
        requested_language = fields.get("language", ["auto"])[0]
        dual_output = fields.get("dual_output", ["false"])[0] == "true"
        words = [
            {
                "start": 0.1,
                "end": 0.6,
                "word": "Synthetic",
                "probability": 0.99,
                "x_whisper_server": {
                    "decoder_token_probabilities": [0.99],
                    "probability_basis": "synthetic_decoder_probability",
                },
            }
        ]
        segment = {
            "id": 0,
            "start": 0.1,
            "end": 0.6,
            "text": "Synthetic",
            "avg_logprob": -0.01,
            "no_speech_prob": 0.0,
        }
        if "word" in requested:
            segment["words"] = words
        actual = ["segment"] if self.profile == "broken" else requested
        timing_channels = {
            granularity: {
                "basis": "decoder_word"
                if granularity == "word"
                else "vad"
                if granularity == "vad"
                else "decoder_segment",
                "reliable": True,
                "reliability": "synthetic-test-evidence",
            }
            for granularity in actual
        }
        response: dict[str, object] = {
            "text": "Synthetic",
            "language": "en",
            "segments": [segment],
            "words": words if "word" in requested else [],
            "speech_segments": [{"start": 0.1, "end": 0.6, "confidence": 0.99}]
            if "vad" in requested
            else [],
            "x_whisper_server": {
                "schema_version": "1.0",
                "request_id": request_id,
                "task": task,
                "requested_model": "auto",
                "selected_model": f"synthetic-{self.profile}",
                "backend": f"synthetic-{self.profile}",
                "server_version": "1.0.0",
                "build_identity": f"synthetic-{self.profile}-build",
                "requested_language": requested_language,
                "detected_language": "en",
                "detected_language_probability": 0.99,
                "detected_language_probability_basis": "synthetic",
                "effective_language": "en",
                "timing": {
                    "requested": requested,
                    "actual": actual,
                    "basis": "decoder_word" if "word" in requested else "vad" if "vad" in requested else "decoder_segment",
                    "reliable": True,
                    "reliability": "synthetic-test-evidence",
                    "channels": timing_channels,
                },
                "audio_duration_seconds": 1.0,
                "queue_seconds": 0.0,
                "preparation_seconds": 0.01,
                "inference_seconds": 0.02,
                "cold_start_seconds": 0.0,
                "inference_includes_preparation": False,
                "realtime_factor": 0.02,
                "cache": {"type": "model_files", "hit": True},
                "decode_count": 2 if dual_output else 1,
                "warnings": [],
            },
        }
        if dual_output:
            response["source_segments"] = [{**segment, "segment_id": "synthetic-0"}]
            response["translation_segments"] = [{**segment, "segment_id": "synthetic-0"}]
        self.server.request_states[request_id] = "completed"  # type: ignore[attr-defined]
        if fields.get("stream", ["false"])[0] == "true":
            progress = {
                "schema_version": "1.0",
                "request_id": request_id,
                "phase": "inference",
                "processed_audio_seconds": 0.5,
                "total_audio_seconds": 1.0,
                "fraction_completed": 0.5,
                "eta_seconds": 0.01,
            }
            completed_progress = {
                **progress,
                "phase": "finalizing",
                "processed_audio_seconds": 0.25 if self.profile == "regressing" else 1.0,
                "fraction_completed": 0.25 if self.profile == "regressing" else 1.0,
                "eta_seconds": 0.0,
            }
            body = (
                "event: progress\n"
                f"data: {json.dumps(progress, separators=(',', ':'))}\n\n"
                "event: progress\n"
                f"data: {json.dumps(completed_progress, separators=(',', ':'))}\n\n"
                "event: result\n"
                f"data: {json.dumps(response, separators=(',', ':'))}\n\n"
            ).encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        self.send_json(200, response)

    def send_request_state(self, request_id: str, state: str) -> None:
        self.send_json(
            200,
            {
                "object": "audio.request",
                "schema_version": "1.0",
                "request_id": request_id,
                "state": state,
                "phase": state,
                "completed_work": 1 if state == "completed" else None,
                "total_work": 1 if state == "completed" else None,
                "fraction_completed": (
                    1.5 if self.profile == "regressing" else 1.0
                ) if state == "completed" else None,
                "eta_seconds": 0.0 if state == "completed" else None,
                "cancellation_requested": False,
            },
        )

    def read_multipart_fields(self) -> dict[str, list[str]]:
        content_length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(content_length)
        content_type = self.headers.get("Content-Type", "")
        message = BytesParser(policy=policy.default).parsebytes(
            f"Content-Type: {content_type}\r\nMIME-Version: 1.0\r\n\r\n".encode() + body
        )
        fields: defaultdict[str, list[str]] = defaultdict(list)
        for part in message.iter_parts():
            name = part.get_param("name", header="content-disposition")
            if not name or part.get_filename() is not None:
                continue
            fields[name].append(part.get_content().strip())
        return dict(fields)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--profile",
        choices=("full", "constrained", "broken", "regressing"),
        required=True,
    )
    parser.add_argument("--port-file", type=Path, required=True)
    args = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", 0), ContractHandler)
    server.profile = args.profile  # type: ignore[attr-defined]
    server.request_states = {}  # type: ignore[attr-defined]
    args.port_file.write_text(str(server.server_port), encoding="utf-8")
    server.serve_forever()


if __name__ == "__main__":
    main()
