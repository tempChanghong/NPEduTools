#!/usr/bin/env bash
# Run in MSYS2 UCRT64. Source archives and their hashes live in recording-sources.lock.json.
set -euo pipefail
build_root="$(cd "${1:?Pass the prepared build root}" && pwd)"
jobs="${2:-8}"
prefix="$build_root/prefix"
mkdir -p "$prefix" "$build_root/build-x264" "$build_root/build-ffmpeg"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
export SOURCE_DATE_EPOCH=1786518000
cd "$build_root/build-x264"
../src/x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55/configure \
    --prefix="$prefix" --host=x86_64-w64-mingw32 --enable-static --disable-cli \
    --disable-opencl --bit-depth=8 --extra-ldflags=-static
make -j"$jobs"
make install
cd "$build_root/build-ffmpeg"
../src/FFmpeg-bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa/configure \
    --prefix="$prefix" --target-os=mingw32 --arch=x86_64 \
    --enable-gpl --enable-version3 --enable-libx264 \
    --enable-static --disable-shared --disable-autodetect --disable-network \
    --disable-doc --disable-debug --disable-ffplay --disable-everything \
    --disable-pthreads --enable-w32threads --pkg-config-flags=--static \
    --extra-cflags="-I$prefix/include" --extra-ldflags="-L$prefix/lib -static" \
    --enable-indev=gdigrab,lavfi \
    --enable-protocol=file,pipe \
    --enable-demuxer=matroska,mov,concat,wav,pcm_s16le,pcm_f32le \
    --enable-muxer=matroska,mp4,null,wav,pcm_s16le,pcm_f32le \
    --enable-encoder=libx264,aac,pcm_s16le,pcm_f32le,rawvideo,wrapped_avframe \
    --enable-decoder=h264,aac,pcm_s16le,pcm_f32le,rawvideo,wrapped_avframe \
    --enable-parser=h264,aac \
    --enable-bsf=aac_adtstoasc,h264_mp4toannexb,extract_extradata \
    --enable-filter=scale,setsar,format,aformat,aresample,anull,null,concat,testsrc,testsrc2,sine,anullsrc,buffer,buffersink,abuffer,abuffersink,volume,amix
make -j"$jobs"
make install
pacman -Q > "$build_root/toolchain-packages.txt"
gcc --version > "$build_root/compiler-version.txt"
nasm -v > "$build_root/assembler-version.txt"
objdump -p "$prefix/bin/ffmpeg.exe" > "$build_root/ffmpeg-pe.txt"
objdump -p "$prefix/bin/ffprobe.exe" > "$build_root/ffprobe-pe.txt"
