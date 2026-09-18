# Third-party notices

The bundled inference executable is built from `ggml-org/whisper.cpp` v1.9.4,
commit `927cfce34f31707e17f2bff35c349632fb9e2c3a`, under the MIT license included
as `LICENSE-whisper.cpp`.

The Linux x86-64 executable is statically linked against musl libc by Zig.
musl's copyright and license notices are included as `LICENSE-musl`.

The plugin can download `ggml-tiny-q8_0.bin`, `ggml-base-q8_0.bin`, and
`ggml-small-q8_0.bin` directly from the MIT-licensed
`ggerganov/whisper.cpp` model repository at revision
`5359861c739e955e79d9a303bcbc70fb988958b1`. They are quantized conversions of
OpenAI's MIT-licensed multilingual Whisper tiny, base, and small models. The
tiny model's expected SHA-256 is
`c2085835d3f50733e2ff6e4b41ae8a2b8d8110461e18821b09a15c40c42d1cca`.
The base model's expected SHA-256 is
`c577b9a86e7e048a0b7eada054f4dd79a56bbfa911fbdacf900ac5b567cbb7d9`.
The small model's expected SHA-256 is
`49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f`.

For speech-boundary evidence, the plugin downloads `ggml-silero-v6.2.0.bin`
from the MIT-licensed `ggml-org/whisper-vad` repository at revision
`9ffd54a1e1ee413ddf265af9913beaf518d1639b`. The pinned file is 885,098 bytes
with SHA-256
`2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987`.
Silero VAD is Copyright (c) 2020-present Silero Team and licensed under the MIT
License.

Sources and licenses:

- https://github.com/ggml-org/whisper.cpp/tree/v1.9.4
- https://github.com/openai/whisper
- https://huggingface.co/ggerganov/whisper.cpp/tree/5359861c739e955e79d9a303bcbc70fb988958b1
- https://huggingface.co/ggml-org/whisper-vad/tree/9ffd54a1e1ee413ddf265af9913beaf518d1639b
- https://github.com/snakers4/silero-vad/blob/master/LICENSE

OpenAI Whisper is Copyright (c) 2022 OpenAI and licensed under the MIT License.
The plugin does not redistribute its model weights; the administrator initiates
the direct download from the pinned upstream repository.

The optional throughput benchmark temporarily downloads whisper.cpp v1.9.4's `samples/jfk.wav`.
The pinned file is 352,078 bytes with SHA-256
`59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e`.
It is an excerpt from President John F. Kennedy's 1961 inaugural address, a
public-domain work of the United States federal government. The sample is used
only for the administrator-requested local benchmark and is deleted afterward.

Sample provenance:

- https://github.com/ggml-org/whisper.cpp/blob/v1.9.4/samples/jfk.wav
- https://commons.wikimedia.org/wiki/File:JFK_inaugural_address.ogg
- https://www.archives.gov/milestone-documents/president-john-f-kennedys-inaugural-address

The request-time, in-memory jellyfin-web script-injection approach was informed
by `n00bcodr/Jellyfin-JavaScript-Injector`, which is licensed under GPL-3.0.
Subtitle Aligner contains its own smaller implementation and, like that project,
does not modify jellyfin-web files on disk.

- https://github.com/n00bcodr/Jellyfin-JavaScript-Injector
