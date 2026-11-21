# Design — TuneLab VST Instrument Host

> Read [GAP-ANALYSIS.md](GAP-ANALYSIS.md) first. This document describes the
> architecture that closes (or deliberately defers) those gaps.

## 1. Product model

One TuneLab **instrument extension** (`com.tunelab.vstinstruments`) that owns:

1. a **catalog** of external instrument plugins, and
2. a **renderer** that turns a TuneLab note group into a mono audio segment by
   driving a real plugin in a helper process.

Each plugin (or each plugin descriptor inside a CLAP/LV2 bundle) is one
`InstrumentSourceInfo` id. The id is stable across machines and projects:

```
<format>:<plugin-identity>
  clap:com.surge-synth-team.surge-xt
  vst3:<bundle-name>
  vst2:<file-name>/<unique-id>
  lv2:<plugin-uri>
  au:<AudioUnit-identifier>
```

The menu layout is:

```
VST Instrument Host
├─ CLAP
│  └─ Surge Synth Team
│     └─ Surge XT
├─ VST3
│  └─ ...
├─ VST2
├─ LV2
└─ AU
```

Only plugins whose metadata says “instrument” are listed. The filter is
format-specific and intentionally conservative (see FORMATS.md).

## 2. Process architecture

```
TuneLab (managed, host ALC)
┌──────────────────────────────────────────────────────────────┐
│ VstInstrumentEngine                                          │
│  ├─ PluginCatalog (cache, layout)                            │
│  ├─ VstInstrumentSession (one per MIDI part)                 │
│  │   ├─ snapshot → note/CC event list                        │
│  │   └─ render blocks → IAudioSegment (mono)                 │
│  └─ VstInstrumentBridgeClient (per RID)                      │
│       │  named pipe: JSON control + note events              │
│       │  shared memory: audio output ring                    │
└───────┼──────────────────────────────────────────────────────┘
        │
        ▼
Helper process (one per RID, multiple slots)
┌──────────────────────────────────────────────────────────────┐
│ TuneLab.VstInstruments.Bridge                                │
│  ├─ BridgeSession (message loop)                             │
│  ├─ ClapInstrumentHost   (managed, C ABI)                    │
│  ├─ Vst2InstrumentHost   (managed, AEffect ABI)              │
│  ├─ Vst3InstrumentHost   (OwnVST3 native host)               │
│  ├─ Lv2InstrumentHost    (managed atom/audio/control/state,  │
│  │                       worker/options/time, Win/X11/Cocoa UI)│
│  └─ AU uses Vst3InstrumentHost (OwnVST3 AudioUnit, macOS)    │
└──────────────────────────────────────────────────────────────┘
```

Why a helper process:

- a crashing plugin cannot take down TuneLab;
- a hung plugin can be killed without freezing the UI;
- 32-bit plugins can run in a 32-bit helper while TuneLab is 64-bit;
- a plugin’s native dependencies cannot pollute TuneLab’s ALC/DLL search path.

One helper per **RID** (not per plugin) is the default, matching epmixer. The
client keeps a slot per session. A future “session-scoped helper” mode can be
added for hard isolation of one expensive instrument.

## 3. Naming and coexistence

All process-global names are distinct from epmixer:

| Thing | epmixer | this project |
| --- | --- | --- |
| Pipe | `TuneLab.VstBridge` | `TuneLab.VstInstrumentBridge` |
| Shared-memory temp dir | `tunelab-vstbridge` | `tunelab-vstinstrument-bridge` |
| Native suffix | `.tlhost` | `.tlvsti` |
| VST3 native | `ownvst3.tlhost.*` | `ownvst3.tlvsti.*` |
| Bridge exe | `TuneLab.ExperimentalPatch.VstBridge.*` | `TuneLab.VstInstruments.Bridge.*` |
| Bridge dir env | `TUNELAB_BRIDGE_DIR` | `TUNELAB_VSTI_BRIDGE_DIR` |
| Residue marker | `tunelab-vstbridge` | `tunelab-vstinstrument-bridge` |

The native suffix change is not cosmetic: Windows resolves DLLs by file name,
and two extensions loading different `ownvst3.dll` builds in one process would
otherwise fight over the first one loaded. The same rule applies to the VST3,
AU and LV2 shims.

## 4. Rendering model

### 4.1 Chunking

TuneLab calls `GetNextPendingSynthesisRange` / `SynthesizeNext`. The session
groups notes into **pieces** separated by silence, exactly like
`WaveTableSession`:

- notes sorted by start; a new piece starts when `note.StartTime > groupMaxEnd`;
- overlapping notes stay in one piece (chords);
- editing any note invalidates its piece and forces re-segmentation.

The engine does **not** try to render the whole part in one piece. That would
make every edit re-render minutes of audio and hide the plugin’s state from the
scheduler.

### 4.2 Determinism: reset per piece

A plugin instance is stateful. To make a piece re-render reproducibly after an
edit elsewhere:

1. keep one **slot per session** (not one per piece);
2. call `reset` / all-notes-off / all-sound-off before each piece;
3. render the piece from `pieceStart - preRoll - latency` to
   `pieceEnd + tail + latency`;
4. re-send the stored state when the sample rate or block size changes.

This is what the current implementation does. Reusing the slot avoids paying a
sampler’s load cost for every piece. Reset is CLAP’s specified way to return to
a deterministic initial state; VST2 has no reset opcode, so the host toggles
`effMainsChanged` and sends all-notes-off / all-sound-off / reset-controllers.

A stricter mode — a fresh instance per piece — remains a planned option for
plugins whose reset is weak. It is a setting, not a different protocol: the
session can unload and reload the slot between pieces. A per-`(pluginId,
stateHash)` instance pool can sit behind the same interface.

### 4.3 Timing

- `sampleRate` is chosen by the extension setting (44.1/48/96 kHz), not by the
  host’s audio device. The segment is created with that rate.
- The requested range is expanded by:
  - `preRollMs` (default 50 ms) so envelopes/attack are not clipped;
  - `latencySamples` reported by the plugin (compensation);
  - `tailMs` (default 2000 ms, max 30 s) for release/reverb, or the plugin’s
    reported tail if that is longer (also capped at 30 s).
- Events are sample-accurate: `sampleOffset = round((eventTime - renderStart) * rate)`.
- `renderStart` is `max(0, pieceStart - preRoll - latency)`. The buffer includes
  the pre-roll, and the segment’s global `sampleOffset` is
  `round(renderStart * rate)`. Including the pre-roll is intentional: it
  preserves the attack and lets a long latency align with the note start.
- Rendering uses fixed internal blocks (512 by default) and a ten-minute
  per-piece cap; the buffer is not trimmed to the note boundaries because the
  pre-roll and tail are part of the audible result.

### 4.4 Latency

Plugin latency shifts the audible result later. The renderer includes it in the
pre-roll window:

1. `renderStart = max(0, pieceStart - preRoll - latency)`;
2. the segment starts at `round(renderStart * rate)`;
3. the first note event lands at `round((pieceStart - renderStart) * rate)`,
   which is at least `latencySamples` into the buffer.

Latency is read at load and cached for the slot. If a plugin changes latency
after activation (CLAP can request a restart), the helper restarts the plugin
and the next render re-reads it. A change that invalidates the current piece
marks it dirty; it is not silently left misaligned.

### 4.5 Mono downmix

TuneLab segments are mono. The helper publishes stereo (or N-channel) output.
The session downmixes:

| Setting | Formula |
| --- | --- |
| `mono_sum` (default) | `(L + R) * 0.5` |
| `mono_sum_minus3db` | `(L + R) * 0.353553` |
| `left` | `L` |
| `right` | `R` |

For plugins with more than 2 outputs, only the first two are used unless a
future “output port” setting says otherwise.

### 4.6 Status and failures

Each piece has a status:

- `Pending` → dirty, not rendered;
- `Synthesizing` → render in progress, with progress;
- `Synthesized` → segment committed;
- `Failed` → the bridge rejected, crashed, timed out, or the plugin produced
  invalid audio. `Message` carries the reason.

A failed piece is not retried automatically in a loop. The user can reload the
plugin (part property) or edit the part to invalidate it.

## 5. Catalog

### 5.1 Scan roots

Settings (per format, `IExtensionSettings`):

- CLAP: `clap_paths` list;
- VST3: `vst3_paths` list;
- VST2: `vst2_paths` list;
- LV2: `lv2_paths` list;
- AU: `au_paths` list (macOS only).

Defaults are the platform’s standard folders:

- Windows: `%COMMONPROGRAMFILES%\CLAP`, `%COMMONPROGRAMFILES%\VST3`,
  `%PROGRAMFILES%\VSTPlugins`, `%COMMONPROGRAMFILES%\VST2`;
- Linux: `/usr/lib/clap`, `/usr/lib/vst3`, `/usr/lib/lv2`,
  `~/.clap`, `~/.vst3`, `~/.lv2`;
- macOS: `/Library/Audio/Plug-Ins/CLAP`, `/Library/Audio/Plug-Ins/VST3`,
  `/Library/Audio/Plug-Ins/VST`, `/Library/Audio/Plug-Ins/Components`,
  `/Library/Audio/Plug-Ins/LV2`, plus the `~/Library/...` equivalents.

Scanning is **metadata-only first**: enumerate files/directories, then ask the
helper to describe each candidate. The helper loads each plugin’s factory (not
an instance) and reads its descriptor. This can crash, which is why it runs in
the helper.

The result is cached in one file,
`%LOCALAPPDATA%\TuneLab\VstInstruments\catalog.json` (or the platform
equivalent), with the plugin path, mtime, size, id, name, vendor, category,
architecture and availability. A file whose mtime/size changed is re-described;
entries whose file disappeared are dropped. The cache is keyed by locator +
mtime + size, so a normal start re-describes only new or changed plugins.

Scan folders are edited from the settings **Scan range...** button. The host
renders a TuneLab-style input form (`PromptInputs`) with one path list per
format; the extension stores the result in `scan-paths.json`. The host settings
page no longer duplicates the path lists.

### 5.2 Instrument filter

Only entries with `isInstrument == true` are published. The rules:

- CLAP: features contain `instrument` and not `audio-effect`;
- VST2: `AEffect.flags & effFlagsIsSynth`;
- VST3: `kInstrument` / `kInstrumentSynth` class category and no `kFx`;
- LV2: `lv2:designation` = `lv2:instrument` or a `lv2:Plugin` class that
  denotes an instrument;
- AU: component type `aumu` (music device) or `augn` (generator).

Entries that fail to describe are shown as unavailable (so the user sees why),
but are not selectable.

### 5.3 Identity

- CLAP descriptor id (stable, reverse-DNS) — best.
- VST3 bundle path + class name (path is not stable across machines; store the
  bundle file name + class name, resolve by scan).
- VST2 unique id (4 chars) + file name.
- LV2 plugin URI (stable).
- AU subtype + manufacturer (stable).

The catalog maps a stable id to the current locator. A project stores the
stable id, not an absolute path. If the plugin moved, the catalog resolves it;
if not found, the part shows “plugin not found”.

## 6. Part model (what the user sees)

`GetPartPropertyConfig` returns a small, stable panel:

| Key | Type | Purpose |
| --- | --- | --- |
| `gain_db` | Slider | Output gain, −24…+12 dB |
| `transpose` | Slider | Semitones, −24…+24 |
| `midi_channel` | Slider | 1…16 |
| `stereo_mode` | ComboBox | mono sum / −3 dB sum / left / right |
| `tail_ms` | Slider | Release tail, 0…30000 ms |
| `pre_roll_ms` | Slider | Pre-roll, 0…500 ms |
| `sample_rate` | ComboBox | 44100 / 48000 / 96000 |
| `polyphony` | Slider | 8…256; advisory in v1 (plugins own their voice allocation) |
| `open_editor` | Button | Open the plugin editor (VST2/VST3/CLAP native editors) |
| `reload_plugin` | Button | Invalidate all pieces and reload the helper slot on the next render |
| `add_automation` | Button | Load parameters and open a selection form; selected continuous parameters become `param:<id>` automation lanes |
| `clear_automation` | Button | Remove those parameter lanes for this instrument |
| `vsti_state_<instrumentId>` | TextBox (multiline) | Base64 plugin state; applied when the slot loads. The namespaced key avoids collisions with other extensions |
| `capture_state` | Button | Manual trigger: capture the plugin state, save it by instrument ID, and write the part field |
| `clear_state` | Button | Remove the part field and the automatic per-instrument state blob |

The panel is a pure function of these values + `InstrumentId`. It never loads
the plugin. Plugin-specific parameters are exposed as automation tracks, not
as part properties.

Deliberately absent in v1:

- `preset`: the SDK has no preset API; the plugin’s editor is the place to
  choose a preset. The `vsti_state_<instrumentId>` field is the portable
  fallback.

Note properties:

| Key | Type | Purpose |
| --- | --- | --- |
| `velocity` | Slider 1…127 | MIDI velocity |
| `detune` | Slider −100…+100 cents | Reserved; not sent in v1 (no per-note pitch in MIDI) |

## 7. Automation

The engine declares the same five part automation tracks as WaveTables so MIDI
imports land on real tracks:

| Track | Range | MIDI mapping |
| --- | --- | --- |
| `pitchbend` | −24…+24 semitones | 14-bit pitch bend, ±2 semitone range (configurable later) |
| `mod` | 0…127 | CC1 |
| `vol` | 0…127 | CC7 |
| `expr` | 0…127 | CC11 |
| `sustain` | 0…127 | CC64 (≥64 = on) |

Automation is evaluated at block boundaries (and at note events) from the
frozen snapshot. It is not per-sample; that is sufficient for MIDI CC.

**Plugin parameter lanes.** “Add Automation” loads the instrument’s parameter
metadata (through a temporary helper slot), filters continuous/automatable
parameters and stores the selection per instrument in `automation.json`. Each
selected parameter is declared as an automation track with id `param:<id>`.
The session evaluates those lanes at each block boundary and sends the values
in the `ProcessRequest.parameters` array; the helper writes CLAP
`clap_event_param_value` events or calls VST2 `setParameter` before the block’s
notes. VST2 parameter values are normalized 0…1; CLAP values use the plugin’s
declared range.

## 8. State and presets

**Current status:** the bridge implements CLAP `clap.state` save/load, VST2
chunks (or program + normalized parameters when the plugin has no chunk
support), and the `GetState` / `SetState` messages. The part panel exposes a
multiline field keyed `vsti_state_<instrumentId>` (not the generic `state`, so
it cannot collide with another extension). The session reads it on the data
thread before offload and passes it in `LoadPlugin`; the helper applies it after
plugin initialization and before activation.

The bridge also polls the plugin state hash (500 ms) while the host is idle and
sends an unsolicited `StateChanged` frame on `<pipe>.events`. The session
coalesces those notifications, reads `GetState`, writes the blob into the
namespaced part field through the host’s writable
`MidiPart.Properties.SetValue` (reflection bridge; SDK 1.2 itself exposes only
the read-only view), and saves it by instrument ID
(`states/<hash>.state`). Host-driven operations (process blocks, parameter
writes, set-state, reset) mark a quiet window and re-baseline the hash, so
automation and rendering never look like a user edit. “Capture State” is the
manual fallback; if the host data shape is unavailable, it shows the base64 for
pasting. “Clear State” removes both the part field and the automatic blob.

The state automation model:

- `LoadPlugin` carries the stored `vsti_state_<instrumentId>` blob; the helper
  applies it after plugin initialization and before activation.
- The plugin’s own editor is the place where the user changes the sound; the
  bridge’s state hash watcher emits `StateChanged`, and the session stores the
  current blob automatically.
- Preset enumeration is format-specific:
  - CLAP: `clap_plugin_preset_load` / preset discovery extension if present;
  - VST2: `effGetProgramNameIndexed` / chunks;
  - VST3: preset discovery / program list;
  - LV2: presets via `lv2:presets`;
  - AU: factory presets via `kAudioUnitProperty_FactoryPresets`.
- If a format cannot enumerate presets, the panel offers only “(current
  state)” and the state blob is authoritative.

State blob format:

```
base64(gzip(header + payload))
header: "TLVI" | u16 version | u16 format | u32 flags | utf8 pluginId
payload: format-specific chunk (VST2 setChunk, VST3 getState, CLAP state, ...)
```

The 16 MB bridge frame limit is checked before sending; if the compressed blob
is too large, the part reports “state too large” and keeps the previous state.

## 9. Isolation and lifecycle

- The client starts the helper for the plugin’s RID on first use.
- The helper exposes a second one-way `<pipe>.events` channel for
  `StateChanged`. If the host cannot connect it, the command protocol and
  rendering continue; only automatic state capture is skipped.
- Windows: job object with `KILL_ON_JOB_CLOSE`; Unix: liveness pipe + process
  group, as in epmixer.
- Load timeout: 60 s (sample libraries can be slow). Scan timeout: 120 s.
  Render timeout: per block, `max(30 s, blockSeconds * 4)`, configurable.
- On timeout: kill the helper, mark all slots in it failed, recreate on next
  use. A slot that times out twice is quarantined for the session.
- On helper crash: the pipe EOF is detected; all slots fail; the part status
  shows the reason.
- On extension unload: close editors, unload slots, kill helpers, delete
  shared-memory files/sockets.
- No orphan processes: the same liveness mechanism as epmixer.

## 10. Protocol overview

See [PROTOCOL.md](PROTOCOL.md). The important additions over epmixer:

- `Hello` carries `rid`, `arch`, `bits`, `formats`, `protocolVersion`.
- `LoadPlugin` carries `format`, `pluginId`, `locator`, `sampleRate`,
  `maxBlock`, `state`, and `instrumentOnly`.
- `ProcessRequest` carries `frames`, `events[]`, `transport`, `bypass`, `tag`.
  There is **no input audio** for instruments; the helper publishes output to
  the shared ring.
- `ProcessResult` is not sent on the audio path (same reasoning as epmixer:
  the shared ring is the result). Control messages use request/response.
- `Panic` (all-notes-off / all-sound-off) and `Reset` are explicit.
- `GetParameters` returns a cached parameter list; `SetParameter` is for the
  future parameter page.

## 11. Threading

- The extension’s live view (`IInstrumentSynthesisContext`) is data-thread
  only. `SynthesizeNext` captures the snapshot synchronously, then renders on a
  worker.
- The bridge client’s control channel is serialized by a lock. The audio/output
  ring is SPSC: worker → helper on input (none for instruments), helper →
  worker on output.
- The helper’s main thread handles messages and plugin calls. Plugin editors
  run on a separate STA/UI thread (Windows) or the platform’s main runloop
  (macOS/Linux, later).
- No managed object from the live view crosses into the worker; only the
  immutable snapshot and the event list.

## 12. Phasing

| Phase | Deliverable | Exit criteria |
| --- | --- | --- |
| 0 | scaffold + docs | **done** |
| 1 | CLAP instruments | **win-x64 done** (Surge XT chord); helpers published for the other RIDs but not executed here |
| 2 | VST2.4 | **host implemented**; x86 scan and x64 effect filtering verified; a VST2 synth render test is still missing |
| 3 | VST3 | native event shim; x64/arm64 |
| 4 | LV2 | lilv shim; x64/arm64 |
| 5 | AU | macOS AUv2; signing |
| 6 | editor GUI, MPE, presets, state page, pool | product polish |
