#!/bin/sh
# Build the vendored Suil library and the native Gtk3/Qt5 LV2 UI hosts.
#
# Requirements (Debian/Ubuntu):
#   build-essential meson ninja-build pkg-config lv2-dev
#   libgtk2.0-dev libgtk-3-dev qtbase5-dev libqt5x11extras5-dev
#
# The resulting files are copied into ./linux-<arch>/ so the bridge can ship
# them beside the helper executable.  This script intentionally builds only
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
  -Dgtk2=enabled \
  -Dgtk3=enabled \
  -Dqt5=enabled \
  -Dqt6=disabled \
  -Ddocs=disabled \
  -Dtests=disabled
meson compile -C "$build"
meson install -C "$build"

cp "$install/lib64/libsuil-0.so.0.10.26" "$out/libsuil-0.so.0"
mkdir -p "$out/suil-0"
for module in libsuil_x11.so libsuil_x11_in_gtk2.so libsuil_x11_in_gtk3.so libsuil_x11_in_qt5.so; do
  cp "$install/lib64/suil-0/$module" "$out/suil-0/$module"
done

cc -O2 -fPIC -shared -std=c11 \
  -I"$root/include" -I"$root" \
  $(pkg-config --cflags lv2 gtk+-3.0) \
  "$root/gtk3_host.c" \
  -o "$out/libtlvsti_suil_gtk3.so" \
  $(pkg-config --libs lv2 gtk+-3.0) \
  -L"$install/lib64" -lsuil-0 \
  -Wl,-rpath,'$ORIGIN'

if pkg-config --exists gtk+-2.0; then
  cc -O2 -fPIC -shared -std=c11 \
    -I"$root/include" -I"$root" \
    $(pkg-config --cflags lv2 gtk+-2.0) \
    "$root/gtk2_host.c" \
    -o "$out/libtlvsti_suil_gtk2.so" \
    $(pkg-config --libs lv2 gtk+-2.0) \
    -L"$install/lib64" -lsuil-0 \
    -Wl,-rpath,'$ORIGIN'
fi

c++ -O2 -fPIC -shared -std=c++17 \
  -I"$root/include" -I"$root" \
  $(pkg-config --cflags lv2 Qt5Widgets) \
  "$root/qt5_host.cpp" \
  -o "$out/libtlvsti_suil_qt5.so" \
  $(pkg-config --libs lv2 Qt5Widgets) \
  -L"$install/lib64" -lsuil-0 \
  -Wl,-rpath,'$ORIGIN'

cp "$root/COPYING" "$out/COPYING.suil"
cp "$root/LICENSES/ISC.txt" "$out/ISC.txt"

echo "Suil host files are in $out"
ls -l "$out"