#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
out="${1:-$root/TuneLab Test macOS.lv2}"

mkdir -p "$out"

clang -dynamiclib -O2 "$root/test_lv2.c" \
    -o "$out/test_lv2.dylib" -lm

sed 's/test_lv2\.dll/test_lv2.dylib/' \
    "$root/TuneLab Test.lv2/manifest.ttl" > "$out/manifest.ttl"

echo "LV2 macOS fixture: $out"