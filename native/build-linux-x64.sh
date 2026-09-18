#!/bin/sh
set -eu

root=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)
source_dir=${WHISPER_SOURCE_DIR:-"$root/artifacts/whisper.cpp-v1.9.4"}
build_dir=${WHISPER_BUILD_DIR:-"$root/artifacts/whisper-linux-x64"}
output=${WHISPER_OUTPUT:-"$root/artifacts/whisper-server"}
contract_patch="$root/native/whisper-server-contract-v1.patch"
tag=v1.9.4
commit=927cfce34f31707e17f2bff35c349632fb9e2c3a

zig_bin=$(command -v zig || true)
if [ -z "$zig_bin" ]; then
    echo "Zig 0.16.x is required." >&2
    exit 2
fi

case $("$zig_bin" version) in
    0.16.*) ;;
    *) echo "Zig 0.16.x is required for the pinned release build." >&2; exit 2 ;;
esac

if [ ! -e "$source_dir/.git" ]; then
    git clone --depth 1 --branch "$tag" https://github.com/ggml-org/whisper.cpp.git "$source_dir"
fi

actual_commit=$(git -C "$source_dir" rev-parse HEAD)
if [ "$actual_commit" != "$commit" ]; then
    echo "whisper.cpp source is not the pinned v1.9.4 commit." >&2
    exit 2
fi

mkdir -p "$build_dir/toolchain" "$(dirname -- "$output")"

# The bundled service is an auditable patch over the pinned upstream source.
# Accept either a clean checkout or the exact already-applied patch so the
# command remains repeatable without accepting unrelated source changes.
source_status=$(git -C "$source_dir" status --porcelain)
if [ -z "$source_status" ]; then
    git -C "$source_dir" apply --unidiff-zero --check "$contract_patch"
    git -C "$source_dir" apply --unidiff-zero "$contract_patch"
elif [ "$source_status" = " M examples/server/server.cpp
 M src/whisper.cpp
 M tests/test-vad-full.cpp" ]; then
    actual_patch=$(mktemp "${TMPDIR:-/tmp}/subtitle-aligner-whisper-patch.XXXXXX")
    trap 'rm -f "$actual_patch"' EXIT HUP INT TERM
    git -C "$source_dir" diff --no-ext-diff --binary --unified=0 -- \
        examples/server/server.cpp src/whisper.cpp tests/test-vad-full.cpp >"$actual_patch"
    if ! cmp -s "$contract_patch" "$actual_patch"; then
        echo "whisper.cpp source contains changes other than the pinned contract patch." >&2
        exit 2
    fi
else
    echo "whisper.cpp source must be clean or contain only the pinned contract patch." >&2
    exit 2
fi

cat >"$build_dir/toolchain/zig-cc" <<EOF
#!/bin/sh
exec "$zig_bin" cc -target x86_64-linux-musl "\$@"
EOF
cat >"$build_dir/toolchain/zig-cxx" <<EOF
#!/bin/sh
exec "$zig_bin" c++ -target x86_64-linux-musl "\$@"
EOF
cat >"$build_dir/toolchain/zig-ar" <<EOF
#!/bin/sh
exec "$zig_bin" ar "\$@"
EOF
cat >"$build_dir/toolchain/zig-ranlib" <<EOF
#!/bin/sh
exec "$zig_bin" ranlib "\$@"
EOF
chmod 0755 "$build_dir/toolchain/zig-cc" "$build_dir/toolchain/zig-cxx" "$build_dir/toolchain/zig-ar" "$build_dir/toolchain/zig-ranlib"

reproducible_flags="-ffile-prefix-map=$source_dir=/src/whisper.cpp -fdebug-prefix-map=$source_dir=/src/whisper.cpp -fmacro-prefix-map=$source_dir=/src/whisper.cpp -ffile-prefix-map=$build_dir=/build/whisper.cpp -fdebug-prefix-map=$build_dir=/build/whisper.cpp -fmacro-prefix-map=$build_dir=/build/whisper.cpp"

cat >"$build_dir/toolchain/zig-toolchain.cmake" <<EOF
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR x86_64)
set(CMAKE_C_COMPILER "$build_dir/toolchain/zig-cc")
set(CMAKE_CXX_COMPILER "$build_dir/toolchain/zig-cxx")
set(CMAKE_AR "$build_dir/toolchain/zig-ar")
set(CMAKE_RANLIB "$build_dir/toolchain/zig-ranlib")
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
set(CMAKE_C_FLAGS_INIT "$reproducible_flags")
set(CMAKE_CXX_FLAGS_INIT "$reproducible_flags")
set(CMAKE_EXE_LINKER_FLAGS_INIT "-static -s")
EOF

cmake -S "$source_dir" -B "$build_dir/build" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$build_dir/toolchain/zig-toolchain.cmake" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_C_FLAGS="$reproducible_flags" \
    -DCMAKE_CXX_FLAGS="$reproducible_flags" \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DBUILD_SHARED_LIBS=OFF \
    -DWHISPER_BUILD_TESTS=OFF \
    -DWHISPER_BUILD_EXAMPLES=ON \
    -DWHISPER_BUILD_SERVER=ON \
    -DWHISPER_CURL=OFF \
    -DWHISPER_COMMON_FFMPEG=OFF \
    -DGGML_NATIVE=OFF \
    -DGGML_SSE42=ON \
    -DGGML_AVX=OFF \
    -DGGML_AVX2=OFF \
    -DGGML_AVX_VNNI=OFF \
    -DGGML_BMI2=OFF \
    -DGGML_FMA=OFF \
    -DGGML_F16C=OFF \
    -DGGML_AVX512=OFF \
    -DGGML_BLAS=OFF \
    -DGGML_OPENMP=OFF \
    -DGGML_CUDA=OFF \
    -DGGML_VULKAN=OFF \
    -DCMAKE_EXE_LINKER_FLAGS="-static -s"
cmake --build "$build_dir/build" --target whisper-server --parallel
cp "$build_dir/build/bin/whisper-server" "$output"
chmod 0755 "$output"
actual_sha=$(shasum -a 256 "$output" | awk '{print $1}')
expected_sha=$(awk '{print $1}' "$root/native/engine-linux-x64.sha256")
if [ "$actual_sha" != "$expected_sha" ]; then
    echo "release engine SHA-256 mismatch: $actual_sha" >&2
    exit 1
fi
printf '%s  %s\n' "$actual_sha" "$output"
