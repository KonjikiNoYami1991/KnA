#!/usr/bin/env bash
# Builds a standalone Tesseract OCR for Linux (x64 / arm64, native): zlib, libpng and Leptonica compiled from source
# as static libraries and linked into one `tesseract` binary (only glibc is needed at run time), so the KnA packages
# read picture subtitles without any system package.
#
# Usage: build/build-tesseract.sh <output-dir>
# Needs: git, cmake (3.18+), make, a C/C++ compiler. Models (tessdata) are added by the release workflow.
set -euo pipefail
mkdir -p "${1:?output dir}"
OUT="$(cd "$1" && pwd)"
TESS_REF="${TESS_REF:-5.5.4}"
LEPT_REF="${LEPT_REF:-1.87.0}"
ZLIB_REF="${ZLIB_REF:-v1.3.2}"
PNG_REF="${PNG_REF:-v1.6.59}"
JOBS="$(nproc 2>/dev/null || echo 4)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PREFIX="$WORK/prefix"
cd "$WORK"
git clone -q --depth 1 --branch "$ZLIB_REF" https://github.com/madler/zlib.git
git clone -q --depth 1 --branch "$PNG_REF" https://github.com/pnggroup/libpng.git
git clone -q --depth 1 --branch "$LEPT_REF" https://github.com/DanBloomberg/leptonica.git
git clone -q --depth 1 --branch "$TESS_REF" https://github.com/tesseract-ocr/tesseract.git

common=(-DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$PREFIX" -DCMAKE_PREFIX_PATH="$PREFIX" -DBUILD_SHARED_LIBS=OFF
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DCMAKE_POLICY_VERSION_MINIMUM=3.5)
build() { cmake -S "$1" -B "$1/b" "${common[@]}" "${@:2}" >/dev/null && cmake --build "$1/b" -j "$JOBS" >/dev/null && cmake --install "$1/b" >/dev/null; }

build zlib -DZLIB_BUILD_EXAMPLES=OFF
rm -f "$PREFIX"/lib/libz.so*                          # link the static zlib only
build libpng -DPNG_SHARED=OFF -DPNG_TESTS=OFF -DPNG_TOOLS=OFF -DZLIB_ROOT="$PREFIX"
# Leptonica with PNG only (all KnA hands to Tesseract), no other image libraries
build leptonica -DSW_BUILD=OFF -DBUILD_PROG=OFF -DENABLE_PNG=ON -DENABLE_ZLIB=ON -DENABLE_GIF=OFF -DENABLE_JPEG=OFF \
  -DENABLE_TIFF=OFF -DENABLE_WEBP=OFF -DENABLE_OPENJPEG=OFF -DZLIB_ROOT="$PREFIX"
build tesseract -DSW_BUILD=OFF -DBUILD_TRAINING_TOOLS=OFF -DBUILD_TESTS=OFF -DDISABLE_ARCHIVE=ON -DDISABLE_CURL=ON \
  -DGRAPHICS_DISABLED=ON -DOPENMP_BUILD=OFF -DDISABLE_TIFF=ON -DLeptonica_DIR="$PREFIX/lib/cmake/leptonica" \
  "-DCMAKE_EXE_LINKER_FLAGS=-static-libgcc -static-libstdc++"

cp "$PREFIX/bin/tesseract" "$OUT/tesseract"
strip "$OUT/tesseract" || true
cp tesseract/LICENSE "$OUT/LICENSE-tesseract.txt"
printf '%s\n' "Tesseract $TESS_REF (https://github.com/tesseract-ocr/tesseract, Apache-2.0) with Leptonica $LEPT_REF," \
  "libpng $PNG_REF and zlib $ZLIB_REF, built statically by KnA's release workflow (build/build-tesseract.sh)." > "$OUT/SOURCE.txt"
"$OUT/tesseract" --version | head -2
