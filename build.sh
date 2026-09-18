#!/bin/sh
set -eu

project_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
configuration=Release
version=0.4.0.11
server=${1:-${WHISPER_SERVER:-}}
assembly="$project_dir/Jellyfin.Plugin.SubtitleAligner/bin/$configuration/net10.0/Jellyfin.Plugin.SubtitleAligner.dll"
artifact_dir="$project_dir/build/Subtitle Aligner_$version"
archive_name="SubtitleAligner-$version.zip"
archive="$project_dir/build/$archive_name"
expected_server_sha=$(awk '{print $1}' "$project_dir/native/engine-linux-x64.sha256")

if [ -z "$server" ]; then
    echo "usage: $0 /path/to/whisper-server" >&2
    exit 2
fi

if [ ! -x "$server" ]; then
    echo "whisper-server is missing or not executable: $server" >&2
    exit 2
fi

server_sha=$(shasum -a 256 "$server" | awk '{print $1}')
if [ "$server_sha" != "$expected_server_sha" ]; then
    echo "whisper-server does not match the pinned Linux x86-64 release build." >&2
    exit 2
fi

dotnet build "$project_dir/Jellyfin.Plugin.SubtitleAligner/Jellyfin.Plugin.SubtitleAligner.csproj" -c "$configuration"

rm -rf "$artifact_dir"
mkdir -p "$artifact_dir"
cp "$assembly" "$artifact_dir/"
cp "$server" "$artifact_dir/whisper-server"
chmod 755 "$artifact_dir/whisper-server"
cp "$project_dir/meta.json" "$artifact_dir/"
cp "$project_dir/artwork/subtitle-aligner.png" "$artifact_dir/logo.png"
cp "$project_dir/THIRD-PARTY-NOTICES.md" "$artifact_dir/"
cp "$project_dir/LICENSE-whisper.cpp" "$artifact_dir/"
cp "$project_dir/LICENSE-musl" "$artifact_dir/"
cp "$project_dir/LICENSE" "$artifact_dir/LICENSE-plugin"

rm -f "$archive"
(cd "$artifact_dir" && zip -q "$archive" \
    Jellyfin.Plugin.SubtitleAligner.dll \
    whisper-server \
    logo.png \
    meta.json \
    THIRD-PARTY-NOTICES.md \
    LICENSE-whisper.cpp \
    LICENSE-musl \
    LICENSE-plugin)
(cd "$project_dir/build" && shasum -a 256 "$archive_name" > "$archive_name.sha256")
printf '%s\n' "$archive"
