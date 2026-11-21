#!/bin/sh
# Build the vendored Suil library and the native Gtk3 LV2 UI host.
#
# Requirements (Debian/Ubuntu):
#   build-essential meson ninja-build pkg-config lv2-dev libgtk-3-dev
#
# The resulting files are copied into ./linux-<arch>/ so the bridge can ship
# them beside the helper executable. This script intentionally builds only
# Linux modules; Windows and macOS use their native UI hosts.
set -eu

arch="${1:-x64}"
root="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
build="$root/build-$arch"
install="$root/install-$arch"
out="$root/linux-$arch"

rm -rf "$build" "$install" "$out"
mkdir -p "$out"

meson setup "$build" "$root" \
  --prefix="$install" \
  --buildtype=release \
  -Dx11=enabled \
  -Dgtk2=disabled \
  -Dgtk3=enabled \
  -Dqt5=disabled \
  -Dqt6=disabled \
  -Ddocs=disabled \
  -Dtests=disabled
meson compile -C "$build"
meson install -C "$build"

libsuil=$(find "$install" -name 'libsuil-0.so.0.10.26' -print -quit)
if [ -z "$libsuil" ]; then
  echo "could not find the installed libsuil" >&2
  exit 1
fi
libdir=$(dirname "$libsuil")
cp "$libsuil" "$out/libsuil-0.so.0"
mkdir -p "$out/suil-0"
cp "$libdir/suil-0/libsuil_x11.so" "$out/suil-0/libsuil_x11.so"
cp "$libdir/suil-0/libsuil_x11_in_gtk3.so" "$out/suil-0/libsuil_x11_in_gtk3.so"

cc -O2 -fPIC -shared -std=c11 \
  -I"$root/include" -I"$root" \
  $(pkg-config --cflags lv2 gtk+-3.0) \
  "$root/gtk3_host.c" \
  -o "$out/libtlvsti_suil_gtk3.so" \
  $(pkg-config --libs lv2 gtk+-3.0) \
  -L"$libdir" -lsuil-0 \
  -Wl,-rpath,'$ORIGIN'

cp "$root/COPYING" "$out/COPYING.suil"
cp "$root/LICENSES/ISC.txt" "$out/ISC.txt"

echo "Suil host files are in $out"
ls -l "$out"