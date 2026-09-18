# Contributing

Subtitle Aligner is a personal project provided without support. Issues are disabled, and pull requests are accepted only from explicitly added repository collaborators. Other pull requests are closed automatically.

You may fork and adapt the project under GPL-3.0. Modified distributions should use a different project name and branding.

## Development setup

You need the .NET 10 SDK. The ordinary development build does not require a Whisper model or native server:

```sh
dotnet restore Jellyfin.Plugin.SubtitleAligner.slnx
dotnet build Jellyfin.Plugin.SubtitleAligner.slnx --configuration Release --no-restore
```

Before retaining a change, run:

```sh
dotnet format Jellyfin.Plugin.SubtitleAligner.slnx --verify-no-changes
dotnet build Jellyfin.Plugin.SubtitleAligner.slnx --configuration Release --no-restore
dotnet test Jellyfin.Plugin.SubtitleAligner.slnx --configuration Release --no-build
node --input-type=module --check < Jellyfin.Plugin.SubtitleAligner/Web/subtitlealigner.js
node --input-type=module --check < Jellyfin.Plugin.SubtitleAligner/Web/subtitlealigner-client.js
bash -n tools/check-speech-service-contract.sh
```

Changes to the speech API extension must also run the same live conformance
script against every supported implementation:

```sh
tools/check-speech-service-contract.sh http://external-test-server:port http://nas-test-server:port
```

See [the v1 contract](docs/speech-service-contract-v1.md). Do not add
server-identity exceptions to the conformance checks.

The ordinary CI workflow runs this deterministic suite. Live speech-service
conformance remains a release gate because CI does not have either inference
backend or its models.

To produce a complete Linux x86-64 archive, pass a statically linked `whisper-server` whose digest matches `native/engine-linux-x64.sha256`:

```sh
./build.sh /path/to/whisper-server
```

The pinned engine is built with Zig 0.16.x, CMake, and Ninja:

```sh
./native/build-linux-x64.sh
```

The build script accepts only the exact whisper.cpp v1.9.4 commit with no
changes or with the byte-identical `native/whisper-server-contract-v1.patch`
already applied. Rebuilds from different source and build directories must
produce the checksum in `native/engine-linux-x64.sha256`.

## Design principles

- Never modify a source media file or source subtitle.
- Prefer declining a correction over applying weak or ambiguous evidence.
- Keep automatic scans resumable and bounded.
- Keep the local inference service loopback-only and alive only while work is running.
- Treat external inference servers as trusted destinations for extracted audio.
- Keep the dashboard consistent with Jellyfin's native controls and permissions.
- Avoid adding dependencies or abstractions when a small, direct implementation is sufficient.

## Pull requests

For existing repository collaborators, a pull request should:

- explain the user-visible behavior and why it is needed;
- identify any Jellyfin ABI, platform, model, subtitle-format, or permission impact;
- update the README and changelog when behavior changes;
- include focused validation for parsing or alignment logic when practical;
- avoid logs, screenshots, sample names, paths, addresses, or configuration from a real server; and
- preserve all relevant third-party attribution.

Do not include copyrighted media or subtitles in the repository. Use short, synthetic fixtures if a focused test needs sample data.
