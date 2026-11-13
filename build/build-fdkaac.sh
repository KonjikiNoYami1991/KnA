#!/usr/bin/env bash
# Builds a standalone, statically linked `fdkaac` encoder (nu774/fdkaac + Fraunhofer FDK AAC library).
#
# Why a separate program: the FDK AAC licence is not GPL-compatible, so an FFmpeg build with
# --enable-libfdk-aac --enable-gpl cannot be redistributed. A standalone fdkaac (Zlib frontend +
# FDK library) can be redistributed on its own; KnA runs it as a separate process
# (ffmpeg decodes → WAV pipe → fdkaac encodes), so nothing is linked with GPL code.
#
# Usage: build/build-fdkaac.sh <target> <output-dir>
#   target: linux-x64 | linux-arm64 | win-x64 | osx (universal, on macOS)
# Needs: git, cmake, a C/C++ compiler (mingw-w64 for win-x64, aarch64-linux-gnu-gcc for linux-arm64 on x64).
set -euo pipefail
TARGET="${1:?target}"
mkdir -p "${2:?output dir}"
OUT="$(cd "$2" && pwd)"
FDK_REF="${FDK_REF:-v2.0.3}"
FDKAAC_REF="${FDKAAC_REF:-v1.0.6}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"
git clone -q --depth 1 --branch "$FDK_REF" https://github.com/mstorsjo/fdk-aac.git
git clone -q --depth 1 --branch "$FDKAAC_REF" https://github.com/nu774/fdkaac.git

CMAKE_ARGS=(-DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DCMAKE_INSTALL_PREFIX="$WORK/prefix" -DCMAKE_PREFIX_PATH="$WORK/prefix")
EXE=fdkaac
case "$TARGET" in
  linux-x64)
    CMAKE_ARGS+=("-DCMAKE_EXE_LINKER_FLAGS=-static-libgcc -static-libstdc++") ;;
  linux-arm64)
    CMAKE_ARGS+=("-DCMAKE_EXE_LINKER_FLAGS=-static-libgcc -static-libstdc++")
    if [ "$(uname -m)" != "aarch64" ]; then
      CMAKE_ARGS+=(-DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64
                   -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++)
      STRIP=aarch64-linux-gnu-strip
    fi ;;
  win-x64)
    CMAKE_ARGS+=(-DCMAKE_SYSTEM_NAME=Windows -DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc -DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-g++
                 -DCMAKE_RC_COMPILER=x86_64-w64-mingw32-windres "-DCMAKE_EXE_LINKER_FLAGS=-static -static-libgcc -static-libstdc++")
    EXE=fdkaac.exe
    STRIP=x86_64-w64-mingw32-strip ;;
  osx)
    CMAKE_ARGS+=("-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64" -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0) ;;
  *) echo "unknown target $TARGET" >&2; exit 2 ;;
esac

cmake -S fdk-aac -B b1 "${CMAKE_ARGS[@]}" -DBUILD_PROGRAMS=OFF >/dev/null
cmake --build b1 --parallel >/dev/null
cmake --install b1 >/dev/null
cmake -S fdkaac -B b2 "${CMAKE_ARGS[@]}" >/dev/null
cmake --build b2 --parallel >/dev/null

cp "b2/$EXE" "$OUT/$EXE"
"${STRIP:-strip}" "$OUT/$EXE" 2>/dev/null || true
{
  echo "fdkaac $FDKAAC_REF (https://github.com/nu774/fdkaac) - Zlib licence"
  echo "Fraunhofer FDK AAC $FDK_REF (https://github.com/mstorsjo/fdk-aac) - licence below"
  echo "Note: AAC may be covered by patents in some countries; see the licence."
  echo
  cat fdk-aac/NOTICE
} > "$OUT/FDKAAC-LICENSE.txt"
echo "built $OUT/$EXE"
