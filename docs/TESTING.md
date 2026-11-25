# Testing plan

## 0. Implemented harness (current)

`TestHarness/TuneLab.VstInstruments.Tests` is a console program that drives the
real helper and a real CLAP plugin. It does not need TuneLab:

```powershell
dotnet build .\TestHarness\TuneLab.VstInstruments.Tests.csproj -c Debug
dotnet .\TestHarness\bin\Debug\net8.0\TuneLab.VstInstruments.Tests.dll
```

The harness, extension and package manifest target SDK **1.2** only. The fake
host implements the 1.2 host-side additions (`PartId`, `InvalidatePart`,
`InvalidateNotes`, note `Id`).

- Updated mainline checkout `Y:\DISK\Codes\TuneLabs\TuneLab2`
  (`upstream/master` 6cff736, 2026-11-03, SDK 1.2): harness build and run.
- SDK-only drop `..\..\TuneLab2` (SDK 1.1): extension build verified; the
  harness would need the older fakes.

Build the harness against the updated checkout with:

```powershell
dotnet build .\TestHarness\TuneLab.VstInstruments.Tests.csproj -c Debug `
  -p:TuneLabSdkRoot=Y:\DISK\Codes\TuneLabs\TuneLab2
```

It exercises:

1. helper handshake and advertised formats;
2. PE header probing (`TDR Nova.dll` → `win-x86`, Dragonfly → `win-x64`,
   Surge XT CLAP → `win-x64`);
3. CLAP scan of Surge XT;
4. direct load + reset + 3-note chord + panic + unload (2 s at 48 kHz);
5. plugin parameter automation (CLAP `clap_event_param_value`);
6. plugin state round-trip (`GetState` → `LoadPlugin.state` → reload);
7. VST3 scan/load/chord/parameters/state/editor with Surge XT;
8. LV2 x64/x86 scan/load/chord/control/state/worker/options/Windows UI with
   synthetic fixtures, synthetic Gtk3 UI tests on Linux x64 and arm64 through
   the vendored Suil host, plus a real LSP Sampler Mono X11 UI test when
   `TUNELAB_TEST_LV2` is set (Cocoa UI remains build-only here);
9. the catalog loader (CLAP folder, cache, instrument-only filter, and
   configured-list authority: empty lists scan nothing);
10. `VstInstrumentSession` end-to-end (fake TuneLab context -> mono segment),
    including the immutable snapshot prefix and data-thread state write-back;
11. UI config probe: settings `Scan now` / `Scan range...` buttons, part
    `Open Editor` / `Reload Plugin` / `Add Automation` / `Clear Automation` /
    `Capture State` / `Clear State`, and the `vsti_state_<instrumentId>` field;
    saved per-format scan paths and folders added with `+` stay visible in the
    re-declared form;
12. event-pipe connection, host-driven state suppression, automatic capture,
    state store round-trip, reflection write-back and parameter filter;
13. automation declaration: selected parameters become `param:<id>` lanes;
14. VST2 editor open/close (Dragonfly Hall Reverb);
15. VST2 effect filtering on win-x64 and a 32-bit VST2 scan on win-x86.

Last full run (win-x64, 2026-11-25): **all tests passed**. The Linux x64 run
also passed, including the real LSP Sampler Mono X11 UI probe and the Gtk3
synthetic UI probe. The native Linux arm64 job runs the same Gtk3 probe.

```
bridge formats: vst2, clap, lv2, vst3
binary probe: x86 / x64 headers detected
scan: Surge XT | Surge Synth Team | id=org.surge-synth-team.surge-xt | instrument=True
bridge event channel: connected
loaded: Surge XT | latency=0 tail=96000 params=775 out=2 editor=True
rendered: peak=0.5709 rms=0.0858 frames=96000
parameter automation: M1: - 0 -> 0.75
editor: ok=True reason=                              (Surge XT CLAP)
bridge state events: host-driven changes suppressed
VST3 effect correctly filtered
VST3 loaded: Surge XT | Surge Synth Team | params=2855 out=2 editor=True
VST3 rendered: peak=0.0716 rms=0.0014 frames=96000
VST3 state: 80508 bytes
VST3 editor: ok=True
LV2 loaded: TuneLab Test LV2 | params=1 out=2 editor=True
LV2 rendered: peak=0.2000 rms=0.0991 frames=48000
LV2 parameters: 1
LV2 state: 32 bytes
LV2 editor: ok=True
LV2 real loaded: LSP Sampler Mono | LSP LV2 | params=14 out=1 editor=True
LV2 real rendered: peak=0.0000 rms=0.0000 frames=48000   (no sample loaded)
LV2 real parameters: 14
LV2 real state: 1656 bytes
LV2 real editor: ok=True
LV2 loaded: TuneLab Test LV2 32 | params=1 out=2 editor=True
LV2 rendered: peak=0.2000 rms=0.0991 frames=48000
LV2 parameters: 1
LV2 state: 32 bytes
LV2 editor: ok=True
LV2 catalog: 32-bit bundle routed to win-x86
plugin state round-trip: 58603 bytes
hard kill + restart: ok
catalog: 15 entry/entries, 4 format group(s)
UI config: settings and part buttons present
scan path form: saved and + added folders stay visible
empty scan roots: empty catalog (no default fallback)
state store round-trip: ok
part state write-back: ok
parameter selection filter: ok
session segment: offset=21600 samples=146400 peak=0.6575
automatic state capture: ok
state field clear: ok
automation tracks: 5 parameter lane(s)
VST2 effect correctly filtered: Dragonfly Reverb (Effect)
VST2 editor: ok=True reason=
32-bit VST2 correctly not loaded by the 64-bit helper: 0x8007000B
x86 bridge scanned 32-bit VST2: TDR Nova (Mastering)
```

The CLAP editor line is a real success: Surge XT’s CLAP GUI opens in the
host’s native child window. VST2 editor hosting is also verified.

The segment offset 21 600 = 24 000 (0.5 s) − 2 400 (50 ms pre-roll), and
146 400 samples = 3.05 s: 0.45 s pre-roll + 1 s note + 2 s Surge tail − 0.4 s
overlap of the pre-roll with the piece start. This confirms latency/tail math.

## 1. Fake plugins first

Real plugins are nondeterministic and platform-specific. The core must be
testable with fakes:

- **Fake CLAP instrument**: a tiny C library exporting `clap_entry`, one
  instrument descriptor, one note input port and one stereo output port. It
  renders a sine wave at the note pitch and writes a marker to a file so the
  test can assert the events it received.
- **Fake VST2 instrument**: an `AEffect` with `effFlagsIsSynth`, one stereo
  output, `effProcessEvents` recording events, `processReplacing` writing a
  sine.
- **Fake VST3/LV2/AU**: same contract, once the native shims exist.
- **Crash/hang variants**: a fake that aborts in `load`, `process`, or spins
  forever. These test isolation and timeouts without a real plugin.

The fake plugins should be buildable with CMake and checked into
`TestHarness/plugins/` (or downloaded by a script). They are not shipped.

## 2. Unit tests (no native plugin)

- Protocol serialization: every message round-trips; unknown fields ignored;
  frame size limit.
- MIDI event list: note-on/off matching with overlapping same-pitch notes;
  sustain; all-notes-off; sample offsets.
- Chunking: note groups, overlap, gap, re-segmentation after edits.
- Latency/tail math: segment offsets, pre-roll trimming, negative offsets.
- Mono downmix: all four modes.
- State blob: gzip + base64 round-trip; size limit; version mismatch.
- Catalog: id stability, instrument filter, unavailable entries.
- Client: helper selection by RID, timeout/kill, restart, residue cleanup.

## 3. Integration tests (real helper, fake plugin)

- Start the helper, handshake, load fake CLAP/VST2, render a 3-note chord,
  assert non-silence and the expected event list.
- Kill the helper during render; assert the host survives, the slot fails, and
  a restart works.
- Kill the host; assert the helper exits (job object / liveness pipe).
- Check no shared-memory files/sockets remain.

## 4. Real-plugin probes

Local Windows corpus and current status:

- `Y:\Insts\VSTEffects\CLAP\01_Bundles\Surge XT.clap` — instrument, **tested**.
- `Y:\Insts\VSTEffects\CLAP\01_Bundles\Cardinal.clap` — scanned.
- `Y:\Insts\VSTEffects\VST3\01_Bundles\Surge XT.vst3`,
  `CardinalSynth.vst3` - VST3 instruments, hosted through the vendored
  OwnVST3 native host; Surge XT is the current render/editor probe.
- VST2 corpus is effect-only: `DragonflyHallReverb-vst.dll` (x64 effect),
  `TDR Nova.dll` (x86 effect). Used for filtering and x86 scanning, not for a
  synth render.
- Linux: `/usr/lib64/lv2/lsp-plugins.lv2` (`LSP Sampler Mono`) - real LV2
  instrument with X11 UI, worker/state interfaces and time/transport support.
  Synthetic `TuneLab Gtk3.lv2` exercises the native Suil host (build with
  `build-suil-fixtures.sh`).
  The sampler has no sample loaded, so the probe accepts silence and checks
  load, parameters, state and UI open/close.

Implemented probe sequence:

1. Scan; Surge XT appears, effects do not.
2. Load; assert `isInstrument`, output channels, latency/tail.
3. Render a C-major chord (C4/E4/G4) for 1 s at 48 kHz plus a tail.
4. Assert peak > threshold and every sample finite.
5. Unload; the helper stays alive for the next slot.

Still to do:

- Send note-off, sustain, pitch-bend and CC1 and measure the resulting change.
- Save state, reload, render again; compare peak/spectrum.
- Crash/hang variants with a fake plugin.
- A real VST2/VST3 synth render.

## 5. TuneLab end-to-end

- Install the `.tlx` into a test data directory (`TUNELAB_DATA_DIR`).
- Create a MIDI part, select the plugin, render, export audio.
- Compare a short phrase rendered twice: identical bytes (determinism).
- Edit one note; assert only its piece is re-rendered.
- Close TuneLab; assert no helper process remains.

## 6. Matrix

Legend: ✅ executed on this machine; ⚠ built/published but not executed;
❌ not supported or not implemented; — not applicable.

| Test | win-x64 | win-x86 | win-arm64 | linux-x64 | linux-arm64 | osx-x64 | osx-arm64 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| build + self-contained publish | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| binary header probe | ✅ | — | — | ⚠ | ⚠ | ⚠ | ⚠ |
| real Surge XT CLAP render | ✅ | ❌ 32-bit CLAP rejected | ⚠ | ⚠ | ⚠ | ⚠ | ⚠ |
| real VST2 32-bit scan | — | ✅ | ⚠ | ❌ | ❌ | ❌ | ❌ |
| real VST2 64-bit effect filter | ✅ | — | ⚠ | ⚠ | ⚠ | ⚠ | ⚠ |
| real VST2 synth render | ⚠ no local synth | ⚠ | ⚠ | ⚠ | ⚠ | ⚠ | ⚠ |
| real VST3 Surge XT render/editor | ✅ | ❌ no native | ⚠ | ⚠ | ⚠ | ⚠ | ⚠ |
| LV2 synthetic instrument | ✅ | ✅ x86 host | ⚠ | ⚠ | ⚠ | ⚠ | ⚠ |
| AU native host | — | — | — | — | — | ⚠ untested | ⚠ untested |
| fake CLAP / VST2 / crash-hang | ❌ not written yet | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

## 7. Performance budgets

- Scan: ≤ 2 s for 100 cached entries; ≤ 100 ms per new plugin (metadata only).
- Load: ≤ 5 s for a typical synth; sample libraries may be much slower and are
  reported as “loading”.
- Render: real-time factor ≥ 5× for a 3-note chord on a modern CPU (offline).
- Helper memory: ≤ 200 MB for a typical synth; sample libraries dominate.

## 8. Failure injection

- Helper killed (SIGKILL / TerminateProcess) during load, render, state, scan.
- Pipe closed; shared memory deleted; helper returns a malformed frame.
- Plugin returns NaN/Inf; plugin writes beyond the output buffer (canary).
- Plugin reports a latency after the first render; re-render once.
- Plugin state > 12 MB; expect a clear error, not a truncated blob.
- Two extensions (epmixer + this one) installed together; no name collision.
