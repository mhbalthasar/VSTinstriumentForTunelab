#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
out="${1:-$root/TuneLab Test Linux.lv2}"

mkdir -p "$out"

gcc -shared -fPIC -O2 "$root/test_lv2.c" \
    -o "$out/test_lv2.so" -lm -lX11

sed 's/test_lv2\.dll/test_lv2.so/' \
    "$root/TuneLab Test.lv2/manifest.ttl" > "$out/manifest.ttl"

echo "LV2 Linux fixture: $out"