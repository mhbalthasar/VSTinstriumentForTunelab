# Format host notes

This document records what each format host must do, what can be reused from
epmixer, and what is still missing.

## CLAP

**Status:** implemented and tested on win-x64. Surge XT CLAP scans, loads,
renders a chord, reports latency/tail and exposes parameters over the protocol.
The helper implements note/MIDI events, note ports, output-only buffers,
reset/panic, state, parameter events, and native Windows GUI hosting on the
bridge main thread. The editor host creates a dedicated `WS_CHILD` container
for the plugin peer, not an Avalonia window. Surge XT’s CLAP GUI still rejects
`gui->set_parent` for that container, so its editor reports that reason;
plugins that accept the host child window should open. 32-bit CLAP is rejected;
CLAP only runs in a 64-bit helper.

**Source base:** `TunelabExpermental/ExperimentalPatch/VstBridgeProcess/VstBridge.Common/Clap*.cs`.

**Reuse:** `ClapLibrary` (ref-counted `dlopen`), `ClapNative` (C ABI structs),
`ClapPluginHost` (audio ports, parameter events, GUI, host callbacks),
`ClapScanner` (descriptor enumeration).

**Add:**

- `clap_event_note` / `clap_event_note_expression` / `clap_event_midi` /
  `clap_event_transport` structs and event list writing.
- `clap_plugin_note_ports` discovery: pick the first input note port whose
  dialect is `clap_note_dialect_clap` (fall back to MIDI dialect).
- Instrument-only load: descriptor features must contain `instrument` and not
  `audio-effect`; otherwise reject.
- Output-only buffers: `audio_inputs_count = 0` when the plugin has no input
  ports; allocate output buffers from the plugin’s output port count.
- `clap_plugin_latency` and `clap_plugin_tail` read at load.
- `start_processing` / `stop_processing` per render chunk; `reset` before a
  fresh instance.
- Transport: fill `clap_process.transport` with tempo, time signature and
  sample position; send `clap_event_transport` at the start of a block if the
  plugin supports it.
- Per-note id allocation: use the host `noteId` in `clap_event_note.note_id`.

**Risks:** plugins that require a GUI to initialize; plugins that only accept
MIDI dialect; plugins that emit output on multiple ports. Start with the first
stereo output port.

## VST2.4

**Status:** implemented in managed code. The win-x86 helper loads and describes
a 32-bit VST2; the win-x64 helper classifies a 64-bit VST2 effect and filters
it out. The Windows VST2 editor opens and closes successfully (verified with
Dragonfly Hall Reverb). No local VST2 **synth** exists, so the render path is
implemented but not yet exercised with a real instrument.

**Source base:** `PluginHost.cs` (AEffect ABI), `Vst2EditorWindow.cs`.

**Reuse:** AEffect layout by pointer size, `VSTPluginMain` via `calli`,
`effOpen`/`effSetSampleRate`/`effSetBlockSize`/`effMainsChanged`,
`processReplacing`, `getChunk`/`setChunk`, parameter read/write, editor.

**Add:**

- `effProcessEvents` (opcode 25) and the `VstEvents` / `VstMidiEvent` layouts.
  The event struct is pointer-size dependent; build it with explicit offsets.
- `effFlagsIsSynth` requirement; reject non-synths.
- `effCanDo` for `sendVstEvents`, `receiveVstEvents`, `receiveVstMidiEvent`;
  if the plugin does not accept events, fail with a clear reason.
- `audioMasterGetTime` returning a `VstTimeInfo` with sample position, tempo,
  time signature and transport flags.
- `effGetProgram`, `effSetProgram`, `effGetProgramName`,
  `effGetProgramNameIndexed` for preset selection (optional v1).
- Output-only: `numInputs == 0`; allocate `numOutputs` planes, zero them.
- `effStartProcess`/`effStopProcess` (opcodes 71/72) around a render chunk if
  the plugin advertises them; otherwise `effMainsChanged`.
- Bitness/arch: PE (Windows), ELF (Linux), Mach-O (macOS); macOS `.vst`
  bundles resolve to `Contents/MacOS/<binary>`.
- macOS/Linux: `NativeLibrary.Load` of a bundle path is not enough; resolve the
  inner binary and set the working directory for sibling resources.

**Risks:** no current VST2 SDK; ABI is reverse-engineered. Some plugins rely on
undocumented host callbacks. VST2 is deprecated; keep it optional.

## VST3

**Status:** implemented and tested on win-x64. Surge XT VST3 scans, loads,
renders a chord, exposes 2855 parameters, round-trips an 80 508-byte state,
and opens/closes its native editor. The helper advertises `vst3` only when the
native `ownvst3.tlvsti.{dll,so,dylib}` is present and the process is 64-bit.

**Source base:** vendored `OwnAudioVST3` managed wrapper (`OwnVST3*.cs`) and
`PluginDiscovery.cs` from the experimental insert host, plus the native
`ownvst3.tlvsti` binary per RID. `Vst3InstrumentHost.cs` is the instrument-side
adapter; `Vst3EditorWindow.cs` runs the editor on its own STA thread.

**Behavior:**

- Scan uses the native descriptor resolver where it works, then falls back to
  one isolated plugin instantiation. VST3's `IsInstrument` has known false
  negatives (Surge XT), so a read-only `Instrument|` binary marker probe is a
  second source.
- Load passes MIDI status/data/offset through the native `ProcessMidi` entry
  and audio through `ProcessAudio`; the wrapper owns a planar scratch buffer.
- Parameters are exposed normalized to `0…1` (the wrapper's setter is
  normalized); state is the native component/controller blob.
- Editing is the native plugin window created on the bridge-side STA thread.
- VST3 is 64-bit only; no win-x86 native. Effects are rejected by the
  instrument-only load filter.

**Risks:** native host is MIT/Unlicense with VST3 SDK MIT sources; scanning
loads plugin code in the helper. Linux/macOS binaries are published but not
executed on this machine.

## AU (macOS)

**Status:** implemented through the same `OwnVST3` native host on macOS. The
helper advertises `au` only on macOS when the native library is present. The
loader uses the AU identifier from the native scanner (not a VST3 bundle path);
the catalog keeps the `.component` path for cache/stat purposes. It has not
been executed on this Windows machine.

**Behavior:** same MIDI/audio/parameter/state/editor path as VST3; the native
library selects AudioUnit when the identifier starts with `AudioUnit:`. AU
scanning uses `PluginScanner.ScanAsync(PluginFormat.AudioUnit)` and filters the
configured `.component` paths.

**Constraints:** AUv3 and code-signing/notarization still need a macOS CI pass.

## LV2

**Status:** implemented as a managed host for atom input/output, scalar control
ports, `state:StateInterface`, `LV2_Worker`, options/buf-size and time/transport,
plus LV2 UI hosts for `ui:WindowsUI`, `ui:X11UI`, `ui:CocoaUI` and `ui:Gtk3UI`
and the `ui:idleInterface` feature. Gtk3 is hosted by a vendored Suil 0.10.26
runtime and a native toolkit window; no
Avalonia window is involved. It was verified with x64 and x86 LV2 test synths
built in the harness (scan, load, MIDI note, audio render, control parameter,
state round-trip, required worker/options features, and Windows UI open/close),
with synthetic Gtk3 UIs on Linux x64 and arm64, and with the real LSP Sampler
Mono on Linux (14 parameters, 1656-byte state, worker/state/time features,
X11 UI open/close). Both LV2 helpers advertise `lv2`; Cocoa remains build-only
until run on macOS.

**Source base:** `Lv2Ttl.cs` (small Turtle reader), `Lv2Native.cs` (descriptor,
atom sequence, URID map/unmap features), `Lv2InstrumentHost.cs`.

**Behavior:**

- Bundle scanning reads `*.ttl` without executing plugin code.
- Load finds the `lv2_descriptor` whose URI matches the catalog plugin, calls
  `instantiate` with URID map/unmap features, connects ports, activates.
- MIDI becomes a `LV2_Atom_Sequence` of 3-byte `midi:MidiEvent` bodies;
  control inputs are normalized in the UI and mapped to the port's
  min/max range; audio output publishes the first two planes.
- `state:StateInterface` is bridged through store/retrieve callbacks and a
  compact binary property map, so the existing part `vsti_state_...` field
  and automatic state capture work for state-capable LV2 instruments.
- `LV2_Worker` is implemented as a host schedule feature and a serial worker
  thread; its responses are delivered through `work_response` in the next
  render context.
- Options/buf-size advertising supplies min/max/nominal/sequence lengths.
- Time/transport is written as a `time:Position` atom on ports that declare
  `atom:supports time:Position`.
- UI hosts: Windows uses `ui:WindowsUI` on an STA thread; Linux first tries
  the native Suil Gtk3 host and falls back
  to `ui:X11UI` with an X11 parent window; macOS uses `ui:CocoaUI` with an
  NSView/NSWindow and a dedicated AppKit thread. All hosts advertise
  `ui:idleInterface` and call the UI's idle callback from the UI thread.
  Windows, Linux X11 and synthetic Gtk3 are tested; Cocoa is build-only until
  run on macOS.
- A plugin requiring a feature the host does not advertise can still refuse to
  instantiate; the load error is reported honestly.

## Summary table

| Format | Host language | Native dependency | x86 | Status |
| --- | --- | --- | --- | --- |
| CLAP | C# | none | no (32-bit rejected) | **working** (win-x64 tested) |
| VST2 | C# | none | yes (win-x86 helper) | **working** (scan/filter tested; synth render untested) |
| VST3 | C# + OwnVST3 native | `ownvst3.tlvsti` per 64-bit RID | no | **working** (win-x64 tested with Surge XT) |
| LV2 | C# managed | none | x64, x86 | **working** (synthetic x64/x86 + real LSP on Linux + worker/options/idle; Windows/X11 UI tested, Cocoa build-only) |
| AU | C# + OwnVST3 native | same macOS native | no | **implemented, macOS untested** |
