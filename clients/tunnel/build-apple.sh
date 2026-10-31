#!/usr/bin/env bash
# Builds build/FlexarrTunnel.xcframework: static libraries (Go c-archive) for iOS,
# iOS simulator, tvOS and tvOS simulator, all arm64, with a header and module map
# (import FlexarrTunnel from Swift).
#
# gomobile doesn't know tvOS, so this drives `go build -buildmode=c-archive` by
# hand: GOOS=ios for every slice (Go's darwin/ios code paths), with clang pointed at
# the right SDK and -target. Go's linker takes the platform (iOS, tvOS, simulator)
# from the cgo objects, so each archive is stamped for the platform Xcode expects.
#
# Usage: ./build-apple.sh [slice ...]   slices: ios ios-sim tvos tvos-sim (default all)
set -euo pipefail
cd "$(dirname "$0")"

MIN_VERSION="${MIN_VERSION:-18.2}"
OUT=build
WORK="$OUT/apple"
# tsnet features the tunnel never uses are left out (tags.sh), logtail included.
TAGS="${TAGS:-$(./tags.sh)}"

slices=("$@")
[ ${#slices[@]} -eq 0 ] && slices=(ios ios-sim tvos tvos-sim)

build_slice() {
  local name=$1 sdk target
  case $name in
    ios)      sdk=iphoneos;         target="arm64-apple-ios$MIN_VERSION" ;;
    ios-sim)  sdk=iphonesimulator;  target="arm64-apple-ios$MIN_VERSION-simulator" ;;
    tvos)     sdk=appletvos;        target="arm64-apple-tvos$MIN_VERSION" ;;
    tvos-sim) sdk=appletvsimulator; target="arm64-apple-tvos$MIN_VERSION-simulator" ;;
    *) echo "unknown slice $name" >&2; exit 1 ;;
  esac
  local sysroot clang
  sysroot=$(xcrun --sdk "$sdk" --show-sdk-path)
  clang=$(xcrun --sdk "$sdk" --find clang)
  local dir="$WORK/$name"
  mkdir -p "$dir"
  echo "==> $name ($target)"
  local flags="-isysroot $sysroot -target $target"
  GOOS=ios GOARCH=arm64 CGO_ENABLED=1 \
    CC="$clang" CXX="$(xcrun --sdk "$sdk" --find clang++)" \
    CGO_CFLAGS="$flags -O2" CGO_LDFLAGS="$flags" \
    go build -trimpath -buildmode=c-archive -tags "ios,$TAGS" -ldflags="-s -w" \
      -o "$dir/libflexarrtunnel.a" ./capi
  # In a folder of its own: Xcode copies every xcframework's headers into one include/, where another package's
  # module.modulemap would collide with ours.
  mkdir -p "$dir/include/FlexarrTunnel"
  mv "$dir/libflexarrtunnel.h" "$dir/include/FlexarrTunnel/FlexarrTunnel.h"
}

# Module map for Swift (import FlexarrTunnel). It autolinks the system frameworks
# Go's runtime and crypto/x509 call, so apps need no extra linker settings.
write_modulemap() {
  cat > "$WORK/$1/include/FlexarrTunnel/module.modulemap" <<'MAP'
module FlexarrTunnel {
    header "FlexarrTunnel.h"
    link framework "Security"
    link framework "CoreFoundation"
    export *
}
MAP
}

for s in "${slices[@]}"; do
  # SKIP_GO=1 reuses archives already built (to change only the packaging).
  if [ -z "${SKIP_GO:-}" ] || [ ! -f "$WORK/$s/libflexarrtunnel.a" ]; then build_slice "$s"; fi
  write_modulemap "$s"
done

rm -rf "$OUT/FlexarrTunnel.xcframework"
args=()
for s in "${slices[@]}"; do
  args+=(-library "$WORK/$s/libflexarrtunnel.a" -headers "$WORK/$s/include")
done
xcodebuild -create-xcframework "${args[@]}" -output "$OUT/FlexarrTunnel.xcframework" >/dev/null
echo "==> $OUT/FlexarrTunnel.xcframework"
for s in "${slices[@]}"; do
  printf '  %-9s %s\n' "$s" "$(du -h "$WORK/$s/libflexarrtunnel.a" | cut -f1)"
done
