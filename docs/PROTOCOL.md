# Host ↔ bridge protocol

The protocol is derived from epmixer’s `VstBridge` protocol. It is deliberately
small: JSON frames for control, a shared-memory ring for audio output.

## 1. Transport

- Named pipe (`NamedPipeServerStream` on Windows; Unix domain socket on
  Linux/macOS via .NET). The command pipe is bidirectional:
  `TuneLab.VstInstrumentBridge-<rid>-<pid>`.
- A second, one-way event pipe carries unsolicited bridge -> host events:
  `<command-pipe>.events`. It uses the same frame format. Keeping events off
  the command pipe guarantees a `StateChanged` frame can never be consumed as
  the reply to `GetState`/`ProcessRequest`.
- Each frame: 4-byte little-endian length + UTF-8 JSON. This build sends JSON
  only; the protocol keeps the option of `[JSON][0x00][binary]` for a future
  attachment, but audio never uses it.
- `MaxFrameBytes = 16 MiB`. A reply that would exceed it is replaced by an
  `Error` frame, never truncated.
- Protocol version: `1`. A version mismatch is rejected in `HelloAck`.

## 2. Shared memory

- One input ring and one output ring, named `<pipe>.in` / `<pipe>.out`.
- Windows: named `MemoryMappedFile`. Unix: files under
  `<temp>/tunelab-vstinstrument-bridge/`.
- Layout: 4 slots, each 16-byte header + 4 channels × 4096 frames × 4 bytes.
- For instruments the **input ring is not used for audio**. The host creates it
  so both sides share one layout, but the helper never reads it. The output
  ring carries the instrument’s stereo output; mono plugins are duplicated to
  L/R. The extension downmixes to TuneLab’s mono segment.
- The ring is SPSC. The producer publishes, the consumer reads and releases.
  A `Tag` field prevents a timed-out block from being mistaken for the next one.

## 3. Handshake

```json
// host → bridge
{ "kind": "Hello", "version": 1, "bits": 64,
  "rid": "win-x64", "arch": "x64", "formats": ["clap","vst2","vst3","lv2"] }

// bridge → host
{ "kind": "HelloAck", "ok": true, "version": 1,
  "rid": "win-x64", "arch": "x64",
  "formats": ["clap","vst2","vst3","lv2"],
  "native": { "vst3": "ownvst3.tlvsti.dll", "lv2": "managed", "au": null } }
```

`formats` is what this helper can actually host. It is derived from the
compiled hosts and the presence of the native shims. The host must not send a
format the helper did not declare.

## 4. Messages

### 4.1 Load

```json
{ "kind": "LoadPlugin", "slot": 1, "format": "clap",
  "pluginId": "com.surge-synth-team.surge-xt",
  "locator": "C:\\...\\Surge XT.clap",
  "sampleRate": 48000, "maxBlock": 1024,
  "state": "base64-or-null", "instrumentOnly": true }
```

```json
{ "kind": "LoadPluginResult", "slot": 1, "ok": true,
  "name": "Surge XT", "vendor": "Surge Synth Team",
  "isInstrument": true, "inputChannels": 0, "outputChannels": 2,
  "latencySamples": 0, "tailSamples": 0,
  "hasEditor": true, "needsOpenGl": false,
  "parameters": 312 }
```

Failure: `ok=false` + `reason` (shown to the user). Effects are rejected with
`reason="not an instrument"` when `instrumentOnly` is true.

### 4.2 Process

```json
{ "kind": "ProcessRequest", "slot": 1, "frames": 1024,
  "bypass": false, "tag": 42,
  "transport": { "playing": true, "tempo": 120.0,
                 "timeSigNum": 4, "timeSigDen": 4,
                 "samplePosition": 0, "ppqPosition": 0.0 },
  "events": [
    { "type": "noteOn",  "offset": 0,   "channel": 0, "note": 60, "velocity": 100, "noteId": 1 },
    { "type": "noteOff", "offset": 512, "channel": 0, "note": 60, "velocity": 0,   "noteId": 1 },
    { "type": "cc",      "offset": 0,   "channel": 0, "controller": 64, "value": 127 },
    { "type": "pitchBend","offset": 0,  "channel": 0, "value": 8192 },
    { "type": "programChange","offset": 0,"channel": 0, "program": 0 }
  ],
  "parameters": [
    { "id": 42, "value": 0.75 }
  ] }
```

- `offset` is in samples from the start of this block.
- `noteId` is allocated by the host and is unique per slot for the lifetime of
  the instance. It lets the helper match note-offs for overlapping same-pitch
  notes. Formats without note ids use `(channel, note, noteId)` internally.
- `bypass` means **silence** for an instrument, not passthrough.
- `parameters` carries plugin-parameter automation for this block: CLAP
  `clap_id`, VST2 parameter index. The helper applies them before the note
  events (CLAP `clap_event_param_value`, VST2 `setParameter`).
- The helper publishes the block to the output ring, then replies:

```json
{ "kind": "processResult", "slot": 1, "tag": 42,
  "ok": true, "frames": 1024, "channels": 2 }
```

Offline rendering has no real-time deadline, so an explicit acknowledgement is
simpler than a ring-only wait. On failure `ok=false` with a `reason`; the ring
still contains silence so the host can keep its block accounting uniform.
Control messages remain request/response.

`transport` is consumed by the VST2 host (`audioMasterGetTime` reads the sample
position, PPQ position and a fixed 120 BPM / 4/4). The CLAP host passes a null
transport in this build, which CLAP explicitly allows; a `clap_event_transport`
is a later addition.

### 4.3 Control

| Message | Direction | Purpose |
| --- | --- | --- |
| `UnloadPlugin` / `UnloadPluginResult` | both | destroy a slot |
| `GetState` / `StateChunk` | both | opaque state blob |
| `SetState` / `SetStateResult` | both | restore state |
| `StateChanged` | bridge → host (event pipe) | unsolicited: a slot’s state hash changed while the host was idle |
| `GetParameters` / `ParameterList` | both | parameter metadata + current values |
| `SetParameter` / `SetParameterResult` | both | set one parameter |
| `Panic` / `PanicResult` | both | all-notes-off, all-sound-off, reset controllers |
| `Reset` | host → bridge | full reset before a fresh render |
| `OpenEditor` / `OpenEditorResult` | both | open the plugin GUI |
| `CloseEditor` / `CloseEditorResult` | both | close it |
| `ScanPlugins` / `PluginList` | both | describe files |
| `Shutdown` / `ShutdownAck` | both | graceful exit |
| `Error` | bridge → host | recoverable protocol error |

`OpenEditor` is Windows-only in this build. The VST2 host creates the editor on
a dedicated STA thread; the CLAP host creates `gui` on the bridge main thread
and the bridge loop pumps its messages. A plugin that rejects
`gui->set_parent` in the native child window (Surge XT CLAP now opens)
a window.

### 4.4 State change events

The helper hashes each loaded plugin’s `GetState` blob while the host is idle
(500 ms interval). A changed hash emits one frame:

```json
{ "kind": "stateChanged", "slot": 1 }
```

The event carries only the slot; the host fetches the blob with `GetState`, so
the event pipe stays small even for a 58 KiB state. Host-driven work (process
blocks with notes/parameters, `SetParameter`, `SetState`, `Panic`, `Reset`)
marks a short quiet window and re-baselines the hash instead of emitting an
event. That prevents parameter automation and rendering from looking like a
user edit in the plugin editor. If the event pipe is unavailable, the command
protocol still works; automatic capture is simply skipped and the manual
“Capture State” action remains.

### 4.5 Scan

```json
{ "kind": "ScanPlugins", "format": "vst3", "deep": false,
  "instrumentOnly": true,
  "paths": ["C:\\Program Files\\Common Files\\VST3"] }
```

```json
{ "kind": "PluginList", "format": "vst3",
  "entries": [
    { "format": "VST3", "pluginId": "vst3:Surge XT.vst3/SurgeXT",
      "name": "Surge XT", "vendor": "Surge Synth Team",
      "category": "Instrument|Synth", "locator": "C:\\...\\Surge XT.vst3",
      "isInstrument": true, "isAvailable": true,
      "bits": 64, "arch": "x64", "rid": "win-x64" }
  ] }
```

The host enumerates files; the helper loads factories/descriptors. This is the
only safe place to execute plugin code during scanning.

## 5. Timeouts

| Path | Timeout | On timeout |
| --- | --- | --- |
| handshake | 15 s | kill helper |
| scan (batch of 4, or one file on retry) | 90 s | restart helper, retry per file, skip the offender |
| load + initialize | 60 s | kill helper, mark the slot failed |
| render block | `max(30 s, 8 × blockSeconds)` | kill helper, mark the piece failed |
| state get/set | 30 s | fail the operation |
| parameter get/set | 10 s | fail the operation |
| open/close editor | 10 s | VST2/VST3/AU host their native editor; CLAP uses a native child window and `set_parent` |
| unload | 10 s | kill helper |
| shutdown | 3 s | kill helper |

Instrument rendering is offline, so these are much larger than epmixer’s
real-time limits. A timed-out helper is always killed; it is never reused.

## 6. Error strings

The bridge returns user-facing English strings in `reason`. The extension may
translate known prefixes:

- `plugin file not found`
- `not an instrument`
- `cannot load the plugin library: ...` (includes bitness/architecture errors,
  e.g. Windows `0x8007000B`)
- `the plugin has no note input`
- `the plugin refused to initialize` / `the plugin refused to activate` /
  `the plugin refused to start processing`
- `the plugin threw during processing: ...`
- `the plugin reported a process error`
- `the bridge did not answer` / `the helper did not publish the block`
- `this bridge cannot host <format>`
- `the slot is not loaded`
- a frame-limit message when a reply (usually state) exceeds 16 MiB

## 7. Compatibility

- Add fields, never repurpose them. Unknown fields are ignored.
- Bump `version` only for a layout/meaning change (shared-memory layout,
  `ProcessRequest` semantics).
- The extension and bridge are shipped together; a mismatch is a bug, not a
  supported combination.
