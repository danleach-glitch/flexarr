#!/usr/bin/env bash
# Builds build/flexarr-tunnel.aar (arm64-v8a, armeabi-v7a, x86_64; minSdk 26) with
# gomobile bind. Kotlin sees it as io.github.flexarr.tunnel.Tunnel.
#
# Needs the Android SDK, an NDK and a JDK. gomobile and gobind are built from the
# versions pinned in go.mod (tool directive).
set -euo pipefail
cd "$(dirname "$0")"

export ANDROID_HOME="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
if [ -z "${ANDROID_NDK_HOME:-}" ]; then
  ANDROID_NDK_HOME="$(ls -d "$ANDROID_HOME"/ndk/* 2>/dev/null | sort -V | tail -1)"
  export ANDROID_NDK_HOME
fi
# gomobile runs javac; prefer Android Studio's JDK when JAVA_HOME isn't set.
if [ -z "${JAVA_HOME:-}" ] && [ -d "/Applications/Android Studio.app/Contents/jbr/Contents/Home" ]; then
  export JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home"
fi
mkdir -p build/bin
go build -o build/bin/ golang.org/x/mobile/cmd/gomobile golang.org/x/mobile/cmd/gobind
export PATH="$PWD/build/bin:$PATH"

gomobile bind \
  -target=android/arm64,android/arm,android/amd64 \
  -androidapi 26 \
  -javapkg io.github.flexarr \
  -tags "$(./tags.sh)" \
  -trimpath -ldflags="-s -w" \
  -o build/flexarr-tunnel.aar \
  .
echo "==> build/flexarr-tunnel.aar ($(du -h build/flexarr-tunnel.aar | cut -f1))"
unzip -l build/flexarr-tunnel.aar | awk '/jni\/.*\.so/ {printf "  %-40s %6.1f MB\n", $4, $1/1048576}'
