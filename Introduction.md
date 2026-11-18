# VST Instrument Host

This extension turns external **instrument** plugins into TuneLab sound
sources. A plugin can be a synthesizer, a sampler, a drum machine or any other
plugin that declares itself as an instrument.

## What it hosts

| Format | Windows | Linux | macOS | Status |
| --- | --- | --- | --- | --- |
| CLAP | x64, arm64 | x64, arm64 | x64, arm64 | Working (64-bit only) |
| VST2.4 | x64, arm64, x86 | x64, arm64* | x64, arm64* | Working (managed ABI) |
| VST3 | x64, arm64 | x64, arm64 | x64, arm64 | Working via OwnVST3 native host (win-x64 tested) |
| LV2 | x64, x86 | x64 | x64 | Working: atom MIDI/audio/controls/state/worker/options/time; Windows/X11/Cocoa UI plus idle interface (Windows and Linux X11 tested; Cocoa build-only) |
| AU | — | — | x64, arm64 | Implemented via OwnVST3 on macOS; not smoke-tested here |

`*` VST2 on Linux/macOS is technically possible but is not part of the tested
tier yet.

Only plugins that identify themselves as instruments are listed. Effect
plugins are deliberately filtered out; use the mixer's effect host for those.

## How to use it

1. Open **Settings → Extensions → VST Instrument Host**.
2. Use **Scan range...** to add plugin folders for each format. It opens a
   TuneLab-style dialog with per-format path lists; the paths are saved in the
   extension's own `scan-paths.json`. Use **Scan now** to refresh the catalog.
3. Open a MIDI part's sound-source selector and choose
   **VST Instrument Host → <format> → <vendor> → <plugin>**.
4. Use the part panel for the stable controls (gain, transpose, MIDI channel,
   stereo mode, tail, pre-roll, sample rate). The same panel has:
   - **Open Editor** (VST2, VST3, and LV2 Windows/X11 UIs work; CLAP depends
     on the plugin accepting the host's window),
   - **Reload Plugin** (reload the slot on the next render),
   - **Add Automation** (opens a TuneLab-style selection form with a name/ID
     filter and a checkbox per continuous parameter) / **Clear Automation**
     (declare or remove the selected parameters as TuneLab automation tracks),
   - **State** (multiline base64 field keyed
     `vsti_state_<instrumentId>`), **Capture State** (writes the current state
     into that field and saves it by instrument ID) and **Clear State**
     (removes the part field and the automatic blob).

The plugin’s factory presets are not exposed yet. The bridge watches the
plugin’s state hash while the host is idle and sends a `StateChanged` event on
a separate event pipe; the session then captures the state and writes it into
the part’s namespaced field through the host’s writable data object, so it
travels with the project. The same blob is saved per instrument
(`states/<hash>.state`) and used when the field is empty. If the host’s internal
data shape is unavailable, the manual Capture State action falls back to
showing the base64 for copying, so no path is blocked. The old generic `state`
property is no longer read; projects from earlier builds should be captured
once into the namespaced field.

## Safety

Plugins are loaded in a separate helper process, never inside TuneLab. A
plugin that crashes, hangs or corrupts memory takes down only that helper
process; the project keeps its slot, the part is marked as failed, and TuneLab
stays alive. The helper is killed automatically when TuneLab exits.

## Rendering model

TuneLab is an offline, deterministic renderer. The extension groups notes into
pieces separated by silence. For each piece it resets the plugin, sends the
notes and controllers with sample-accurate timing, renders the requested block
plus a configurable pre-roll and release tail, and writes the result into a
TuneLab audio segment. Reset + all-notes-off makes repeated renders
deterministic for the usual instruments; a fresh-instance-per-piece option is
planned for plugins whose reset is weak.

TuneLab audio segments are mono. Stereo plugin output is downmixed according
to the **Stereo mode** setting (mono sum, −3 dB sum, left or right).

## Limitations

- CLAP is hosted in 64-bit helpers only. 32-bit CLAP plugins are rejected
  rather than risking two ABI layouts; 32-bit VST2 still works in the win-x86
  helper.
- The VST2 editor is hosted on Windows. The CLAP editor is attempted through
  the bridge's main-thread GUI pump; plugins that reject `gui->set_parent`
  (Surge XT CLAP is one) report that reason instead of opening a window.
- Plugin state is stored in the part’s multiline **State** field keyed
  `vsti_state_<instrumentId>` and is applied when the slot loads. The bridge
  watches the state hash while the host is idle and sends a `StateChanged` event
  on the event pipe; the session captures and writes the field automatically.
  The same blob is also saved by instrument ID (`states/<hash>.state`) for
  parts with an empty field. **Capture State** remains an explicit manual
  trigger; **Clear State** removes the field and the automatic copy. If the
  host’s internal data shape changes, the extension falls back to displaying
  base64 for manual pasting.
- Parameter automation is stored per instrument, not per part. “Add
  Automation” selects the plugin's continuous/automatable parameters; all
  parts using that instrument share the same lanes.
- Per-note pitch bend is not available through MIDI; only the part-level
  `pitchbend` automation is sent. MPE is a later item.
- Tempo is fixed at 120 BPM because the instrument SDK snapshot has no
  transport; tempo-synced arpeggiators are not tempo-accurate yet.
- A plugin whose state chunk exceeds the bridge frame limit (16 MiB) cannot be
  transferred; the operation reports the reason instead of truncating.
- The package requires SDK 1.2 (`ButtonConfig` / `InvalidatePart`); a released
  1.1 host rejects it.