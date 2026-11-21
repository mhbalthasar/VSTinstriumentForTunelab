#!/usr/bin/env bash
# Build the synthetic Gtk3 LV2 bundle for the Suil UI host test.
#
# Requirements: gcc, pkg-config, libgtk-3-dev.
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
gtk_out="${1:-$root/TuneLab Gtk3.lv2}"

mkdir -p "$gtk_out"

gcc -shared -fPIC -O2 "$root/test_lv2.c" -o "$root/test_lv2.so" -lm -lX11

gcc -shared -fPIC -O2 "$root/gtk3_ui.c" \
    -o "$gtk_out/test_gtk3_ui.so" \
    $(pkg-config --cflags --libs gtk+-3.0)

cp "$root/test_lv2.so" "$gtk_out/test_lv2.so"

cat > "$gtk_out/manifest.ttl" <<'TTL'
@prefix lv2:  <http://lv2plug.in/ns/lv2core#> .
@prefix atom: <http://lv2plug.in/ns/ext/atom#> .
@prefix midi: <http://lv2plug.in/ns/ext/midi#> .
@prefix doap: <http://usefulinc.com/ns/doap#> .
@prefix ui:   <http://lv2plug.in/ns/extensions/ui#> .

<urn:tunelab:test-lv2>
    a lv2:Plugin, lv2:InstrumentPlugin ;
    doap:name "TuneLab Gtk3 Fixture" ;
    lv2:binary <test_lv2.so> ;
    ui:ui <urn:tunelab:test-gtk3-ui> ;
    lv2:port [
        a lv2:InputPort, atom:AtomPort ;
        lv2:index 0 ;
        lv2:symbol "midi" ;
        lv2:name "MIDI In" ;
        atom:bufferType atom:Sequence ;
        atom:supports midi:MidiEvent
    ], [
        a lv2:OutputPort, lv2:AudioPort ;
        lv2:index 1 ;
        lv2:symbol "outL" ;
        lv2:name "Out L"
    ], [
        a lv2:OutputPort, lv2:AudioPort ;
        lv2:index 2 ;
        lv2:symbol "outR" ;
        lv2:name "Out R"
    ], [
        a lv2:InputPort, lv2:ControlPort ;
        lv2:index 3 ;
        lv2:symbol "gain" ;
        lv2:name "Gain" ;
        lv2:minimum 0.0 ;
        lv2:maximum 1.0 ;
        lv2:default 1.0
    ] .

<urn:tunelab:test-gtk3-ui>
    a ui:Gtk3UI ;
    lv2:binary <test_gtk3_ui.so> .
TTL

echo "Gtk3 fixture: $gtk_out"