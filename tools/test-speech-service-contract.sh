#!/usr/bin/env bash
set -euo pipefail

project_dir=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)
work_dir=$(mktemp -d "${TMPDIR:-/tmp}/subtitle-aligner-contract-test.XXXXXX")
full_pid=''
constrained_pid=''
broken_pid=''
regressing_pid=''

cleanup() {
    if [ -n "$full_pid" ]; then
        kill "$full_pid" 2>/dev/null || true
        wait "$full_pid" 2>/dev/null || true
    fi
    if [ -n "$constrained_pid" ]; then
        kill "$constrained_pid" 2>/dev/null || true
        wait "$constrained_pid" 2>/dev/null || true
    fi
    if [ -n "$broken_pid" ]; then
        kill "$broken_pid" 2>/dev/null || true
        wait "$broken_pid" 2>/dev/null || true
    fi
    if [ -n "$regressing_pid" ]; then
        kill "$regressing_pid" 2>/dev/null || true
        wait "$regressing_pid" 2>/dev/null || true
    fi
    rm -rf "$work_dir"
}
trap cleanup EXIT HUP INT TERM

# The synthetic services inspect the request contract, not the audio content.
# Generate a valid probe with the Python standard library so this test does not
# depend on ffmpeg being installed on the CI runner.
python3 - "$work_dir/probe.wav" <<'PY'
import sys
import wave

with wave.open(sys.argv[1], "wb") as probe:
    probe.setnchannels(1)
    probe.setsampwidth(2)
    probe.setframerate(16_000)
    probe.writeframes(b"\0\0" * 16_000)
PY
export SPEECH_CONTRACT_PROBE_FILE="$work_dir/probe.wav"

python3 "$project_dir/tools/synthetic-speech-service.py" \
    --profile full --port-file "$work_dir/full.port" &
full_pid=$!
python3 "$project_dir/tools/synthetic-speech-service.py" \
    --profile constrained --port-file "$work_dir/constrained.port" &
constrained_pid=$!
python3 "$project_dir/tools/synthetic-speech-service.py" \
    --profile broken --port-file "$work_dir/broken.port" &
broken_pid=$!
python3 "$project_dir/tools/synthetic-speech-service.py" \
    --profile regressing --port-file "$work_dir/regressing.port" &
regressing_pid=$!

for _ in $(seq 1 100); do
    if [ -s "$work_dir/full.port" ] \
        && [ -s "$work_dir/constrained.port" ] \
        && [ -s "$work_dir/broken.port" ] \
        && [ -s "$work_dir/regressing.port" ]; then
        break
    fi
    sleep 0.05
done

if [ ! -s "$work_dir/full.port" ] \
    || [ ! -s "$work_dir/constrained.port" ] \
    || [ ! -s "$work_dir/broken.port" ] \
    || [ ! -s "$work_dir/regressing.port" ]; then
    echo "synthetic speech services did not start" >&2
    exit 1
fi

full_url="http://127.0.0.1:$(cat "$work_dir/full.port")"
constrained_url="http://127.0.0.1:$(cat "$work_dir/constrained.port")"
bash "$project_dir/tools/check-speech-service-contract.sh" "$full_url" "$constrained_url"

broken_url="http://127.0.0.1:$(cat "$work_dir/broken.port")"
if bash "$project_dir/tools/check-speech-service-contract.sh" "$broken_url" \
    >"$work_dir/broken.stdout" 2>"$work_dir/broken.stderr"; then
    echo "the conformance checker accepted a silent timing downgrade" >&2
    exit 1
fi

regressing_url="http://127.0.0.1:$(cat "$work_dir/regressing.port")"
if bash "$project_dir/tools/check-speech-service-contract.sh" "$regressing_url" \
    >"$work_dir/regressing.stdout" 2>"$work_dir/regressing.stderr"; then
    echo "the conformance checker accepted regressing progress" >&2
    exit 1
fi
