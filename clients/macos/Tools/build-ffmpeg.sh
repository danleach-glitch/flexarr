#!/bin/bash
# Builds the FFmpeg the Mac app bundles: ffmpeg and ffprobe, LGPL (no GPL or non-free parts, so no libx264: the app
# encodes with VideoToolbox), linked only against macOS's own libraries. Cached in clients/macos/build/ffmpeg.
# Usage: Tools/build-ffmpeg.sh [arm64|x86_64]   (with neither, builds both and joins them in <version>-universal)
set -euo pipefail

version=9.0.2
# The release tarball's SHA-256. Its signature was checked against FFmpeg's release signing key
# (FCF9 86EA 15E6 E293 A564 4F10 B432 2F04 D676 58D8) when this version was pinned.
sha256=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e
here="$(cd "$(dirname "$0")/.." && pwd)"

if [ -z "${1:-}" ]; then
  universal="$here/build/ffmpeg/$version-universal"
  "$0" arm64
  "$0" x86_64
  mkdir -p "$universal"
  for tool in ffmpeg ffprobe; do
    lipo -create "$here/build/ffmpeg/$version-arm64/$tool" "$here/build/ffmpeg/$version-x86_64/$tool" -output "$universal/$tool"
  done
  echo "FFmpeg $version (arm64 and x86_64) in $universal"
  exit 0
fi
arch="$1"
out="$here/build/ffmpeg/$version-$arch"

if [ -x "$out/ffmpeg" ] && [ -x "$out/ffprobe" ]; then
  echo "FFmpeg $version ($arch) already built in $out"
  exit 0
fi

work="$here/build/ffmpeg/work-$arch"
rm -rf "$work"
mkdir -p "$work"
tarball="$here/build/ffmpeg/ffmpeg-$version.tar.xz"
[ -f "$tarball" ] || curl -sfL "https://ffmpeg.org/releases/ffmpeg-$version.tar.xz" -o "$tarball"
if [ "$(shasum -a 256 "$tarball" | cut -d' ' -f1)" != "$sha256" ]; then
  echo "error: $tarball doesn't match the pinned SHA-256; not building it" >&2
  rm -f "$tarball"
  exit 1
fi
tar -xf "$tarball" -C "$work" --strip-components 1

cd "$work"
# --disable-autodetect: nothing from Homebrew or elsewhere on this Mac, only what's named here, all in macOS itself.
PKG_CONFIG_LIBDIR=/nonexistent ./configure \
  --arch="$arch" --target-os=darwin --cc="clang -arch $arch" \
  --extra-cflags="-mmacosx-version-min=14.0" --extra-ldflags="-mmacosx-version-min=14.0" \
  --disable-autodetect --disable-gpl --disable-nonfree \
  --enable-videotoolbox --enable-audiotoolbox --enable-securetransport \
  --enable-zlib --enable-bzlib --enable-iconv --extra-libs=-liconv \
  --enable-static --disable-shared \
  --disable-ffplay --disable-doc --disable-debug \
  $([ "$arch" = x86_64 ] && echo --disable-x86asm) \
  > configure.log || { tail -20 configure.log; exit 1; }
make -j"$(sysctl -n hw.ncpu)" ffmpeg ffprobe > make.log 2>&1 || { tail -30 make.log; exit 1; }

mkdir -p "$out"
cp ffmpeg ffprobe "$out/"
strip -x "$out/ffmpeg" "$out/ffprobe"
rm -rf "$work"
echo "Built FFmpeg $version ($arch) in $out"
