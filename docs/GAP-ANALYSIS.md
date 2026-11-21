# Gap analysis — VST instrument host for TuneLab

> Date: 2026-11-13.
> Inputs inspected:
> - `Y:\InstDriversForTLB\WaveTablesForTunelab` (sibling extension structure)
> - `Y:\DISK\Codes\TuneLabs\TuneLab2` (non-experimental TuneLab checkout)
> - `Y:\TuneLab2` (SDK-only drop, SDK 1.1; kept for released-host compatibility)
> - `Y:\OpenCodeSpace\TunelabExpermental` (epmixer insert host + VST bridge)
> - `Y:\Insts\VSTEffects` (local real plugins: Surge XT CLAP/VST3, Cardinal
>   Synth, effect-only bundles)
>
> **Network limitation and update path:** direct GitHub access from this
> machine is still blocked (`github.com` resolves to a stale IP and times out;
> `api.github.com`, `codeload.github.com` and `raw.githubusercontent.com` fail
> DNS), and `git` is not on `PATH`. However, the local clone at
> `Y:\OpenCodeSpace\TunelabExpermental` has
> `upstream = https://github.com/LiuYunPlayer/TuneLab.git`, and GitHub
> Desktop ships `git.exe`. Using that clone:
>
> - fetched `upstream` successfully;
> - confirmed `upstream/master = 6cff736` (2026-11-03, SDK 1.2,
>   `LastReleasedSdkVersion = 1.1`);
> - exported that tree and updated `Y:\DISK\Codes\TuneLabs\TuneLab2` to it
>   (old tree backed up beside it).
>
> The local checkout is therefore current as of the last fetch. A completely
> fresh-clone sign-off is still blocked by direct GitHub access.

This document is intentionally blunt: it lists what is missing before the
requested matrix can be claimed. The design that follows from it is in
[DESIGN.md](DESIGN.md).

---

## 0. Executive summary

The project is feasible, but the requested matrix is **not** a straight port of
epmixer. Three hard gaps dominate:

1. **The TuneLab instrument SDK is an offline, mono, chunked renderer.** It has
   no plugin-discovery, preset, parameter, state, transport or editor API. All
   of that has to be modelled through part properties and the extension's own
   settings/catalog.
2. **The epmixer bridge is an effects bridge.** It carries audio in → audio out
   and (for CLAP) only `clap_event_param_value`. It has **no note/MIDI event
   channel, no instrument processing path, no transport, no tail/latency
   compensation**, and its VST3 native wrapper (`OwnVst3Wrapper`) exposes only
   `ProcessAudio`. VST3/AU/LV2 instrument hosting cannot be built by renaming
   files alone.
3. **The requested architecture matrix exceeds what the host and formats
   actually ship.** TuneLab’s CI publishes win-x64, linux-x64 and osx-arm64.
   .NET 8 has no linux-x86 or osx-x86 runtime; modern macOS cannot load 32-bit
   code; upstream `ownvst3` has no x86 build. “x86 on all three OSes” is not
   achievable with the current host/runtime, and must be restated as
   “Windows x86 where the host can run a 32-bit helper”.

A realistic first supported tier is:

- **Tier 1:** CLAP + VST2 on Windows x64/x86/arm64; CLAP + VST3 on
  Windows/Linux/macOS x64/arm64.
- **Tier 2:** LV2 (native shim), AU (macOS native shim), VST3 x86 (needs a new
  native host).
- **Tier 3:** macOS/Linux plugin-editor windows, MPE, multi-timbral ports.

### 0.1 Implementation status (2026-11-14)

What is actually built and measured in this repository:

| Area | Status | Evidence |
| --- | --- | --- |
| CLAP scan + load + render | **Working** | Surge XT CLAP renders a 3-note chord; peak/RMS non-zero and finite; session produces a mono segment. |
| CLAP note/CC/pitch-bend | **Working** | `clap_event_note` / `clap_event_midi` written per block; reset/panic on every piece. |
| CLAP latency/tail | **Working** | `clap.latency` / `clap.tail` read at load; Surge reports 96 000 samples of tail at 48 kHz. |
| CLAP parameter automation | **Working** | `clap_event_param_value` carried in `ProcessRequest.parameters`; Surge `M1` moved 0 → 0.75 in the harness. |
| CLAP state | **Protocol only** | `clap.state` implemented in the helper; not persisted by the part yet. |
| VST2 scan + filter | **Working** | win-x64 filter rejects Dragonfly Reverb (effect); win-x86 helper loads and describes 32-bit TDR Nova. |
| VST2 render | **Implemented, untested with a synth** | `effProcessEvents` + `processReplacing` + chunks/parameters; no local VST2 synth exists. |
| VST2 editor | **Working (Windows)** | Dragonfly Hall Reverb editor opened and closed in the harness. |
| CLAP editor | **Partial** | Main-thread GUI pump implemented; Surge XT CLAP rejects `gui->set_parent`, so it reports that reason. |
| SDK 1.2 UI | **Working** | Settings `Scan now`/`Scan range...`; part `Open Editor`/`Reload Plugin`/`Add Automation`/`Clear Automation`. |
| Scan path dialog | **Working** | `PromptInputs` form with per-format path lists; paths persisted in `scan-paths.json`. |
| Parameter automation lanes | **Working** | “Add Automation” opens a per-parameter selection form; selection stored per instrument; harness found 5 lanes. |
| Plugin state data field | **Working** | Multiline `vsti_state_<instrumentId>` property is applied at load; protocol round-trip 58 603 bytes. |
| Automatic state detection | **Working** | The bridge hashes the state while idle and sends `StateChanged` on the separate `<pipe>.events` channel; host-driven changes are suppressed. |
| Automatic state write-back | **Working (reflection bridge)** | The session writes the namespaced part field via `MidiPart.Properties.SetValue`; `Capture State` is the manual fallback. |
| Automatic state cache | **Working** | A changed state is saved to `states/<hash>.state` by instrument ID; used when the part field is empty. `Clear State` removes both. |
| VST3 | **Working (win-x64 tested)** | OwnVST3 native host; scan/load/MIDI/audio/parameters/state/editor; Surge XT VST3 80 508-byte state. |
| AU | **Implemented, macOS untested** | Same OwnVST3 native host with AudioUnit identifiers; advertised only on macOS. |
| LV2 | **Working (Windows tested)** | Managed atom-MIDI/audio/control/state/worker/options/time host; x64/x86 synthetic tested; Windows UI tested; X11/Cocoa UI build-only. |
| State persistence | **Automatic field + per-instrument cache** | The field is written automatically and travels with the project; the sidecar covers parts with an empty field. |
| Helpers for 7 RIDs | **Published** | win-x64/x86/arm64, linux-x64/arm64, osx-x64/arm64; only win-x64/x86 executed here. |
| Package | **Built** | `dist\VSTinstriumentForTunelab.tlx`, ~248 MB with seven self-contained helpers (including OwnVST3 native libraries), manifest `sdk-version: 1.2`. |

The rest of this document remains the gap analysis that drove that build; it is
not a claim that everything in it is solved.

---

## 1. What TuneLab gives the plugin (and what it does not)

### 1.1 The instrument contract

From `TuneLab.SDK/Instrument`:

| Member | What it means for this project |
| --- | --- |
| `InstrumentSourceInfos` | id → `{Name, Description, Portrait}`. Must return **immediately**, no blocking. This is the entire menu model. |
| `InstrumentSourceLayout` | Optional group tree; leaves reference ids. Use it for `format → vendor → plugin`. |
| `Init()` / `Destroy()` | Lazy engine lifetime. Scan/caches belong here. |
| `CreateSession(context)` | One session per MIDI part. `context.InstrumentId` is immutable. |
| `GetAutomationConfigs` | Part-level automation tracks. Fixed set; can depend on part values but must be a **pure, lightweight** function. |
| `GetSynthesizedParameterConfigs` | Read-only echo tracks. |
| `GetPartPropertyConfig` / `GetNotePropertyConfig` | Property panel schema. Must be a pure function, called on every commit. |

Session contract:

| Member | Consequence |
| --- | --- |
| `GetNextPendingSynthesisRange(start,end)` | The engine chooses its own chunking. |
| `SynthesizeNext(start,end,cancel)` | Pull `context.GetSnapshot(...)` in the synchronous prefix, then render off-thread. |
| `context.CreateAudioSegment(sampleOffset, sampleCount, sampleRate)` | Audio is written through a host-owned segment; `sampleOffset` is global samples at the segment’s native rate. |
| `IAudioSegment.Write(int offset, ReadOnlySpan<float>)` | **Mono** float buffer. There is no channel dimension. |
| `Status` / `StatusChanged` | Per-chunk pending/synthesizing/done/failed timeline. |
| `SynthesizedParameters` | Optional curves; not needed for v1. |

### 1.2 Gaps in the host contract

| # | Gap | Evidence | Impact |
| --- | --- | --- | --- |
| H1 | **No plugin discovery / metadata API.** | `IInstrumentSynthesisEngine` only exposes the catalog. | The extension must own scanning, caching, instrument-vs-effect filtering and a stable id scheme. |
| H2 | **No preset API.** | Nothing in the SDK. | Presets must be part properties (index/name + opaque state). |
| H3 | **No dynamic parameter API.** | `GetPartPropertyConfig` is a pure, cheap function; querying a plugin there would block the UI and violate the contract. | Parameter metadata must be cached out-of-band; the panel can only expose a small fixed set plus a cached snapshot. |
| H4 | **No plugin-state storage.** | Part properties only support `bool/double/string/PropertyObject/PropertyArray`. | State must be a compressed base64 **string** property (or a sidecar file, which breaks project portability). |
| H5 | **No editor-window API.** | No control in the property vocabulary opens a native window. | v1 cannot offer an “Open plugin GUI” button without a host change or a property-toggle hack. |
| H6 | **Mono output.** | `IAudioSegment.Write(ReadOnlySpan<float>)`; `Introduction.md` of WaveTables says the same. | Stereo instruments must be downmixed. This is a product limitation, not a bug. |
| H7 | **Offline chunking, not real-time.** | `SynthesizeNext` is a task; the host can request any chunk at any time. | A stateful plugin must be reset per chunk or replayed; real-time-only plugins may not work. |
| H8 | **No transport/tempo in the SDK.** | Snapshot has notes, properties and automation only. | Tempo-synced arpeggiators/LFOs cannot follow TuneLab tempo in v1. Send a fixed transport derived from the chunk start (optional). |
| H9 | **No pitch curve for instruments (v1).** | `docs/instrument-sdk-design.md` §0/§4: “v1 纯按 note 的整数 pitch 发声”; pitch is a future additive extension. | Per-note detune/vibrato cannot be sent as MIDI pitch bend (channel-wide). Only part-level `pitchbend` automation is usable. |
| H10 | **No per-note fixed velocity field.** | Velocity is a declared note property (`Properties["velocity"]`), as WaveTables does. | The engine must declare it and read it from the snapshot. |
| H11 | **No way to mark a part as “failed” from the engine except `Status`.** | `SynthesisStatusSegment` is the only error channel. | Bridge crashes/load failures are reported as failed chunks with a message; the UI will show them as a red status band, not a modal. |
| H12 | **Declaration is called before a session exists.** | `docs/plugin-development.md` §5.2 explains why declarations live on the engine. | The catalog/parameter cache must be usable without a live plugin instance. |
| H13 | **The host is versioned separately from the SDK.** | The updated mainline checkout has `ExtensionManager.SdkVersion = 1.2` and `LastReleasedSdkVersion = 1.1`; `manifest.sdk-version` gates loading. | Build against 1.1 if the package must load on released hosts; build against 1.2 to use `ButtonConfig`/`TagConfig`/`Invalidate*` (1.2 is not released yet). |

### 1.3 Host build/packaging gaps

| # | Gap | Evidence | Impact |
| --- | --- | --- | --- |
| H14 | **The official TuneLab CI builds only win-x64, linux-x64, osx-arm64.** | `TunelabExpermental/.github/workflows/*.yml` runtime list. | The extension cannot make TuneLab itself run on win-arm64/linux-arm64/osx-x64; it can only ship helpers for those RIDs. |
| H15 | **.NET 8 has no linux-x86 or osx-x86 runtime.** | .NET 8 RID catalog. | A 32-bit helper on Linux/macOS needs a native (C/C++) bridge, not a .NET bridge. |
| H16 | **Modern macOS cannot execute 32-bit binaries.** | Apple dropped 32-bit support after Catalina. | macOS x86 plugin hosting is impossible, regardless of bridge design. |
| H17 | **Extension packages are loaded into an isolated ALC.** | `docs/plugin-development.md` §1. | Native libraries must be resolved explicitly by path; do not rely on the default DLL search order. |
| H18 | **The extension is not allowed to reference the host app.** | Same doc, §3. | No access to TuneLab’s internal mixer/editor windows; the bridge is the only native boundary. |
| H19 | **No update of TuneLab2 was possible here.** | No `git`, no network. | The final SDK-level compatibility test (latest non-experimental commit) is pending. |

---

## 2. What the epmixer bridge gives us (and what it does not)

### 2.1 Reusable pieces

| Piece | File | Reuse decision |
| --- | --- | --- |
| Length-prefixed JSON frames + named pipe | `BridgeProtocol.cs`, `BridgeSession.cs`, `BridgeProgram.cs` | Reuse shape, new pipe name and protocol version. |
| Shared-memory audio ring (SPSC, 4 slots, 4 channels, 4096 frames) | `BridgeSharedMemory.cs` | Reuse; add a “no input” convention for instruments. |
| Job object / liveness pipe / process group | `VstBridgeClient.cs`, `BridgeProgram.cs`, `UnixProcess.cs`, `BridgeResidue.cs` | Reuse, rename the residue directory and env var. |
| Load/scan/state/parameter/editor messages | `BridgeProtocol.cs` | Extend with instrument fields and note events. |
| CLAP C ABI host | `ClapNative.cs`, `ClapLibrary.cs`, `ClapPluginHost.cs`, `ClapScanner.cs` | Reuse as the base; add note events, note ports, transport, instrument filtering. |
| VST2 AEffect ABI host | `PluginHost.cs`, `Vst2EditorWindow.cs` | Reuse as the base; add `effProcessEvents`, synth checks, programs, transport. |
| VST3 host | `Vst3PluginHost.cs` + vendored `OwnVst3Wrapper` | **Not reusable for instruments as-is**: audio-only native API. |
| Timeout/watchdog/audio-stall policy | `BridgeTimeout.cs`, `AudioStallWatch.cs` | Reuse the idea; instrument rendering needs much longer, non-real-time timeouts. |
| Plugin bitness probe | `PluginBitness.cs` | Extend from PE-only to PE/ELF/Mach-O and arch (x86/x64/arm64). |

### 2.2 Effects-only assumptions that break for instruments

| # | Gap | Evidence | Consequence |
| --- | --- | --- | --- |
| B1 | **No note/MIDI event channel.** | `ProcessRequest` carries only `Slot/Frames/Channels/Bypass/Tag`; CLAP events are only `clap_event_param_value`; VST2 host never calls `effProcessEvents`. | Instruments produce silence or hang waiting for notes. This is the single largest protocol gap. |
| B2 | **Processing assumes an input block.** | `SharedAudioRing.TryPublish` rejects `channels <= 0`; VST2 host copies input planes; CLAP host builds input buffers. | An instrument has 0 audio inputs. Needs a silent-input convention or a new “synth” process path. |
| B3 | **Bypass means “copy input to output”.** | `PluginHost.Process` / `ClapPluginHost.Process` / `Vst3PluginHost.Process`. | For an instrument, bypass must mean **silence**, not passthrough. |
| B4 | **No instrument/effect filtering at load.** | Hosts report `IsSynth`, but epmixer filters at the insert layer. | This extension must require `IsSynth`/`instrument` and reject effects at both scan and load. |
| B5 | **No transport.** | No tempo/time-signature/sample-position fields. | Tempo-synced plugins cannot sync; some plugins refuse to start without `audioMasterGetTime`. |
| B6 | **No tail/latency contract.** | `LatencySamples` exists on load, but no tail; effects are continuous. | Offline chunks must add pre-roll and release tail explicitly. |
| B7 | **No per-note identity / note-id.** | CLAP events have `note_id` fields but the host never sets them; VST2 has no note id. | Overlapping same-pitch notes need careful off-matching; use a host-side voice map. |
| B8 | **No program/bank messages.** | VST2 host reads parameters and chunks only; CLAP has no program list path. | Preset selection is only possible through state chunks in v1. |
| B9 | **No “panic”/all-notes-off.** | Unload just calls `effClose`/`destroy`. | A crashed or timed-out render can leave stuck notes in a reused instance. Fresh instance per chunk avoids this. |
| B10 | **One bridge per bitness, all formats.** | `VstBridgeClient.ForBitness` is a singleton per bitness. | A hung instrument blocks every slot in that bridge. Offline rendering needs per-slot timeouts or a session-scoped bridge. |
| B11 | **Editor support is Windows-only.** | `Vst3-BRIDGE.md` §9.1: “不在本章: 编辑器 GUI 的跨平台消息泵”. | Instruments are GUI-heavy; macOS/Linux users get parameter panels only until a native pump exists. |
| B12 | **16 MB frame limit.** | `BridgeProtocol.MaxFrameBytes`. | Large sampler states (Kontakt, Omnisphere) can exceed it. Need chunked state transfer or a sidecar. |
| B13 | **No architecture negotiation.** | `HelloMessage` has `Bitness` only. | x64 vs arm64 cannot be distinguished; a win-x64 host could accidentally hand an arm64 plugin to an x64 bridge. Add full RID. |
| B14 | **PE-only bitness probe.** | `PluginBitness.Read` reads `IMAGE_FILE_MACHINE_*`. | Linux/macOS scanning cannot choose the right bridge. Add ELF `e_machine` and Mach-O `cputype`. |
| B15 | **Fixed pipe/shared-memory names.** | `BridgeProtocol.PipeName = "TuneLab.VstBridge"`; `tunelab-vstbridge` temp dir. | Collides with epmixer if both are installed. Rename to `TuneLab.VstInstrumentBridge` / `tunelab-vstinstrument-bridge`. |
| B16 | **Native suffix renamed.** | `OwnVST3.cs` constants, `Vst3Backend.NativeFileName`, csproj copy targets, native files. | Resolved: all ownvst3 files now use `.tlvsti`, so they cannot collide with epmixer's `.tlhost` library. |
| B17 | **No LV2 or AU code at all.** | No files. | Both need a native shim and a new host-side backend. |
| B18 | **No offline-render lifecycle.** | Effects are `setActive` once and process forever. | Instruments need `startProcessing/stopProcessing` per chunk, reset, and deterministic state restore. |

---

## 3. Format-by-format gaps

### 3.1 CLAP (best candidate)

**What epmixer already has:** library ref-counting, descriptor scanning, audio
ports, parameter events, GUI (`clap_plugin_gui`), host callbacks, start/stop
processing.

**Missing for instruments:**

- `clap_event_note` (`note_on`, `note_off`, `note_choke`, `note_end`) and
  `clap_event_note_expression`; `clap_event_midi` for CC/pitch-bend/program.
- `clap_plugin_note_ports` discovery: which note port/dialect/channel to feed.
- Instrument detection already exists (`ClapScanner.LooksLikeInstrument`), but
  the load path does not require it and does not select note ports.
- Transport (`clap_event_transport`) and `clap_plugin_latency` / `clap_plugin_tail`.
- Per-note id allocation and off-matching.
- Output-only audio buffers (0 input ports).
- Offline `start_processing` / `stop_processing` per chunk, plus reset/flush.

**Verdict:** implement first. Pure C ABI, MIT, works on every OS/arch where a
.NET bridge can run (including win-x86).

### 3.2 VST2.4

**What epmixer already has:** AEffect layout for x86/x64, `VSTPluginMain`,
`dispatcher`, `setParameter`/`getParameter`, `processReplacing`, `getChunk`/
`setChunk`, editor window, sample rate/block size/mains-changed.

**Missing for instruments:**

- `effProcessEvents` (opcode 25) and `VstMidiEvent` / `VstEvents` marshalling.
- `effFlagsIsSynth` requirement and `effGetEffectName`/vendor.
- `effGetProgram`, `effSetProgram`, `effGetProgramName`, `effGetProgramNameIndexed`,
  `effGetMidiProgramName`/`effGetMidiProgramCategory` (optional v1).
- `audioMasterGetTime` (`VstTimeInfo`) so tempo-synced plugins run.
- `effCanDo` probes (`sendVstEvents`, `receiveVstEvents`, `receiveVstMidiEvent`).
- Output-only processing; `numInputs == 0` handling.
- `effMainsChanged`/`effStartProcess`/`effStopProcess` per chunk.
- macOS bundle resolution (`*.vst/Contents/MacOS/*`) and ELF/Mach-O bitness.

**Verdict:** feasible on Windows x86/x64/arm64 with the existing reverse-
engineered ABI. Linux/macOS VST2 works in principle but is deprecated and not
in Tier 1. No VST2 SDK is available; do not add Steinberg headers.

### 3.3 VST3 (hardest)

**What epmixer has:** `Vst3PluginHost` + vendored `OwnVst3Wrapper`, which calls
native exports `VST3Plugin_Create`, `VST3Plugin_Initialize`,
`VST3Plugin_ProcessAudio`, `VST3Plugin_SetParameter`, etc. The native library
is `ownvst3.tlvsti.*` from `OwnAudioVst` 1.7.0 (MIT wrapper; native binaries
prebuilt for win/linux/osx x64+arm64, **no x86**).

**Why it cannot host instruments as-is:**

- `ProcessAudio` takes planar audio in/out only; there is no event bus, no
  `IEventList` input, no note events, no transport, no `kInstrument`
  classification.
- The wrapper does not expose the plugin’s audio input/output bus layout or
  event bus count.
- VST3 instruments require `addEvent` on the input event list with
  `kNoteOnEvent`/`kNoteOffEvent`, plus `kParamValueEvent`, and a transport
  context. That must be added in the native shim.

**Options:**

1. **Fork/extend `OwnAudioVST3`’s native host** to expose an event-capable
   `ProcessAudioWithEvents` (and instrument metadata). Pros: keeps one native
   library and the existing RID matrix. Cons: native source/build chain must be
   obtained; upstream may be JUCE-based, so licensing must be checked.
2. **Write a small native VST3 host shim** against the Steinberg VST3 SDK.
   Pros: full control. Cons: VST3 SDK is GPLv3/proprietary dual-licensed; a
   GPLv3 project is compatible only if the whole distribution is GPLv3 (this
   project is GPLv3, but the TuneLab host is MIT and loads us as a plugin — the
   boundary needs legal review). Also build 6 RIDs.
3. **Use an existing MIT/ISC VST3 host library.** Needs research; none is
   vendored here.
4. **Ship VST3 as “audio-effect only”** — not acceptable for this product.

**Verdict:** VST3 instrument support is a **native work item**, not a rename.
Until it lands, VST3 must be reported as “not available in this build” rather
than silently loading and producing silence. No x86 VST3.

### 3.4 AU (macOS only)

**What exists:** nothing in epmixer.

**Requirements:**

- macOS-only native shim using AudioToolbox (`AudioComponentFindNext`,
  `AudioComponentInstanceNew`, `AudioUnitInitialize`, `AudioUnitSetProperty`,
  `MusicDeviceMIDIEvent` / `AUScheduleMIDIEventBlock`, render callback).
- Distinguish AUv2 (component) and AUv3 (app extension). AUv3 out-of-process
  hosting is substantially more complex; v1 should target AUv2 music devices.
- Scan `/Library/Audio/Plug-Ins/Components`, `~/Library/Audio/Plug-Ins/Components`,
  and `AudioComponent` enumeration.
- The bridge process must have a running CFRunLoop for some AUs; offline render
  can use `AudioUnitRender` directly but GUI needs the runloop.
- Code signing / hardened runtime: loading third-party unsigned AUs in a
  hardened process requires `com.apple.security.cs.disable-library-validation`.
- AU has no Windows/Linux equivalent. The matrix must say macOS only.

**Verdict:** native shim + macOS CI + notarization. Tier 2.

### 3.5 LV2

**What exists:** nothing in epmixer.

**Requirements:**

- Prefer `lilv` (ISC license) for discovery/instantiation, or implement TTL
  parsing + `dlopen` + `LV2_Descriptor` directly. `lilv` is the pragmatic
  choice; it is a native dependency per RID.
- Discover bundles (`*.lv2` directories with `manifest.ttl`), read
  `lv2:Plugin`, ports (`lv2:InputPort`/`OutputPort`, `lv2:AudioPort`,
  `lv2:ControlPort`, `atom:AtomPort`), and `lv2:designation` (`lv2:instrument`).
- Feed notes through an Atom sequence (`LV2_Atom_Sequence` with
  `LV2_MIDI__MidiEvent` or the newer `lv2:midi` / `atom:Sequence`), or the
  plugin’s event port. Send transport via `lv2:time` if supported.
- Run the plugin’s worker (`LV2_Worker_Schedule`) on a separate thread; some
  samplers require it for loading.
- UI: native Windows/X11/Cocoa hosts plus vendored Suil 0.10.26 for
  Gtk2/Gtk3/Qt5 on Linux x64.
- Cross-platform x64/arm64; x86 only if lilv/native libs are built for x86.

**Verdict:** native shim, but simpler than AU/VST3. Tier 2.

---

## 4. Platform / architecture matrix reality

| OS \ Arch | x64 | arm64 | x86 |
| --- | --- | --- | --- |
| Windows | Host ships; all formats except AU (VST3 x64 native exists) | Host not in official CI; bridge can be published; VST3 arm64 native exists | Host can run win-x86; bridge can be win-x86; VST3 x86 native missing; CLAP/VST2 possible |
| Linux | Host ships; CLAP/VST3/LV2; VST2 possible | Host not in official CI; CLAP/VST3/LV2 arm64; VST2 possible | **No .NET 8 linux-x86 runtime**; needs native bridge; rare |
| macOS | Host not in official CI; CLAP/VST3/LV2/AU x64; Rosetta on arm64 | Host ships; CLAP/VST3/LV2/AU arm64 | **Impossible**: modern macOS has no 32-bit execution |

Consequences:

- “CLAP/VST2.4/VST3/AU/LV2 on linux/Windows/macOS arm64/x64/x86” is **not a
  single achievable matrix**. AU is macOS-only; x86 macOS is dead; Linux x86
  needs a non-.NET bridge; VST3 x86 has no upstream native.
- The extension can still **publish helper binaries** for more RIDs than the
  host officially ships, but the host must be running on that OS/arch to use
  them. A win-x64 TuneLab can drive a win-x86 helper (WOW64) and a win-arm64
  helper (if the OS can execute it); a linux-x64 TuneLab cannot drive a
  linux-arm64 helper; an osx-arm64 TuneLab can drive an osx-x64 helper under
  Rosetta 2 only if Rosetta is installed.
- The plugin’s architecture, not the host’s, decides which helper is needed.
  The scanner must read PE/ELF/Mach-O headers and the host must select the RID.

---

## 5. Packaging, size, signing, licensing

| # | Gap | Detail |
| --- | --- | --- |
| P1 | **Helper size** | A self-contained .NET bridge is ~60–90 MB per RID; 6 RIDs → a 400+ MB `.tlx`. A framework-dependent bridge is small but needs a .NET 8 runtime on the user’s machine (TuneLab may be self-contained). Options: trimming, native C++ bridge, or one bridge per format family. |
| P2 | **macOS signing** | The bridge executable and every `.dylib` must be signed and notarized; loading unsigned third-party AUs/VST3s needs the library-validation entitlement. |
| P3 | **Windows DLL search** | Plugins often load sibling DLLs; the bridge must set the DLL directory (`SetDllDirectory` / `AddDllDirectory`) and use the plugin’s folder, not TuneLab’s. |
| P4 | **Linux glibc** | Upstream `ownvst3` linux-x64 requires `GLIBC_2.38` (Ubuntu 24.04 level); RHEL8/Anolis8 (glibc 2.28) cannot `dlopen` it. Need a manylinux build or document the floor. |
| P5 | **Licensing** | This project is GPLv3 (sibling convention). `ownvst3` is MIT. `lilv` is ISC. VST3 SDK/JUCE licensing must be reviewed before shipping a VST3 native host. VST2 has no current SDK; the AEffect ABI is reverse-engineered. AU uses Apple’s SDK (allowed). |
| P6 | **No plugin sandbox** | The bridge isolates crashes but not filesystem/network access. Document that plugins are trusted code. |
| P7 | **State size** | 16 MB frame limit ≈ 12 MB raw state after base64. Large samplers can exceed it. Plan chunked state or a sidecar blob with project-relative path. |
| P8 | **Coexistence** | epmixer and this extension may be installed together. All process-global names must differ: pipe, shared-memory dir, env var, native suffix, bridge exe name, mutex/job names. |

---

## 6. Rendering-model gaps (host semantics vs plugin semantics)

TuneLab can ask for any chunk at any time and re-asks after edits. Instrument
plugins are stateful. The following must be decided explicitly:

| # | Question | Options | Recommended |
| --- | --- | --- | --- |
| R1 | One plugin instance per part or per chunk? | Per part (fast, stateful, order-dependent) vs per chunk (deterministic, expensive). | **Per chunk** for v1 correctness; add an instance pool and “render whole part” fast path later. |
| R2 | How to handle latency? | Ignore (misaligned) vs pre-roll + shift segment offset. | Pre-roll `latencySamples`, create the segment at `pieceStart - latency` (clamped at 0), drop the pre-roll from the written audio. |
| R3 | How to handle release tails? | Stop at last note-off (clipped) vs render `tail` beyond. | Render a configurable tail (default 2 s), bounded by a max; let the segment extend past the last note. |
| R4 | How to reset between chunks? | `all-notes-off` only vs full plugin reset/reload. | Fresh instance (R1) + `all-notes-off`/`reset`; never reuse a timed-out instance. |
| R5 | Stereo → mono | Sum, average, left, right. | Average with −3 dB option; expose `stereo_mode`. |
| R6 | Sample rate | Plugin rate = segment rate. | 48 kHz default; setting 44.1/48/96 kHz. |
| R7 | Chunk size | TuneLab range vs fixed blocks. | Fixed internal blocks (e.g. 512/1024) inside the requested range; sample-accurate event offsets. |
| R8 | Automation | Part-level CC curves. | Declare `pitchbend`, `mod`, `vol`, `expr`, `sustain`; map to MIDI. Do not expose plugin parameters as automation in v1. |
| R9 | Note overlap | Instrument SDK passes overlaps raw. | Allocate a voice id per note; match note-off by `(pitch, voiceId)`, not pitch alone. |
| R10 | Tempo | No host transport in SDK. | Send a fixed tempo (120) and chunk-relative sample position; document that tempo-synced plugins are not yet tempo-accurate. |

---

## 7. What “compatible with the latest TuneLab” requires

1. **Update the checkout.** Direct GitHub access is still blocked, but the
   local clone at `Y:\OpenCodeSpace\TunelabExpermental` (which has
   `upstream = https://github.com/LiuYunPlayer/TuneLab.git`) was fetched with
   GitHub Desktop’s bundled `git.exe`. `upstream/master` is
   `6cff736` (2026-11-03), `SdkVersion = 1.2`,
   `LastReleasedSdkVersion = 1.1`. That tree was exported and installed at
   `Y:\DISK\Codes\TuneLabs\TuneLab2`; the old tree is backed up beside it.
   `TuneLab.SDK` + `TuneLab.Foundation` were built Release, and the extension
   and harness were built against it with
   `-p:TuneLabSdkRoot=Y:\DISK\Codes\TuneLabs\TuneLab2`; all harness tests pass.
   The SDK-only drop at `Y:\TuneLab2` (1.1) also builds the extension.
2. **Re-run the SDK diff** (`TuneLab.SDK/PublicAPI.Shipped.txt` and
   `PublicAPI.Unshipped.txt`) and check for:
   - new instrument members (additive, but verify no removals),
   - `ExtensionManager.SdkVersion` (manifest gate),
   - changes to `IAudioSegment`, `InstrumentSynthesisSnapshot`,
     `InstrumentSynthesisNoteSnapshot`, `SynthesisStatusSegment`.
3. **Build against the updated DLLs**, then run the extension load test and a
   real render test.
4. **Do not use unreleased 1.2 members in the 1.1 package.** The shipped
   package declares `sdk-version: 1.1`; `ButtonConfig`, `TagConfig` and the
   `Invalidate*` context members belong to the unreleased 1.2 surface. A
   separate 1.2 package may use them and must bump the manifest to 1.2.

The updated mainline checkout is verified. Only a fresh-clone/direct-GitHub
sign-off remains blocked by the network path.

---

## 8. Recommended scope and phasing

### Phase 0 — this scaffold

- Project layout, manifest, docs, protocol, bridge client/program skeleton.
- No claim of production support.

### Phase 1 — CLAP instruments end-to-end

- Note events, note ports, transport, latency/tail, instrument-only filtering.
- Windows x64 first; then win-x86/win-arm64/linux-x64/linux-arm64/osx-x64/osx-arm64.
- Real-plugin test: Surge XT CLAP (`Y:\Insts\VSTEffects\CLAP\01_Bundles\Surge XT.clap`).
- This proves the rendering model and the isolation model.

### Phase 2 — VST2.4 instruments

- `effProcessEvents`, synth flags, programs, transport, output-only.
- Windows x64/x86/arm64; Linux/macOS as a documented “best effort”.
- Real-plugin test needs a VST2 instrument; the local corpus is effects-only.

### Phase 3 — VST3 instruments

- Native event-capable shim (fork ownvst3 or write one); no x86.
- Legal review of the VST3 SDK/JUCE boundary.
- Real-plugin test: Surge XT.vst3 / CardinalSynth.vst3.

### Phase 4 — LV2

- `lilv`-based native shim; Atom/MIDI events; worker thread.
- Cross-platform x64/arm64.

### Phase 5 — AU

- macOS AudioToolbox shim; AUv2 first; signing/notarization.
- GUI pumping is a separate item.

### Phase 6 — polish

- Plugin editor windows on macOS/Linux, MPE, multi-timbral ports, preset
  browsing, chunked state, instance pool, whole-part fast path.

---

## 9. Verification checklist (must pass before “supported”)

Legend: `[x]` measured in this repository; `[~]` implemented but not fully
measured; `[ ]` not done.

- [x] `dotnet build` of extension and bridge against the updated mainline SDK 1.2
      (`upstream/master` 6cff736) and the older SDK-only drop 1.1.
- [x] Extension loads in TuneLab; catalog shows only instruments; a 64-bit VST2
      effect and a 32-bit VST2 effect are both filtered.
- [x] Surge XT CLAP renders a 3-note chord to a non-silent mono segment
      (peak ≈ 0.65, RMS ≈ 0.09 for the test chord).
- [x] Settings page declares `Scan now` / `Scan range...` buttons; the path
      dialog is a SDK `PromptInputs` form persisted to `scan-paths.json`.
- [x] Part panel declares `Open Editor` / `Reload Plugin` / `Add Automation` /
      `Clear Automation`; `Add Automation` declares `param:<id>` tracks and the
      renderer sends parameter events (Surge `M1` 0 → 0.75).
- [x] VST2 editor opens and closes on Windows (Dragonfly Hall Reverb).
- [ ] CLAP editor for Surge XT: the plugin rejects `gui->set_parent`; other
      CLAP plugins are untested. VST2 editor hosting is verified.
- [~] Note-off, sustain, pitch-bend and CC1 are implemented; state changes were
      not measured against a reference.
- [~] A CLAP ABI bug crashed the helper during development; the host survived
      and reported the failure. A dedicated crash/hang test is still missing.
- [ ] Killing TuneLab leaves no bridge process, shared-memory file or socket.
- [x] State `GetState` / `SetState` round-trips (Surge XT: 58 603 bytes) and
      the part’s `vsti_state_<instrumentId>` field is applied at load. The bridge
      emits `StateChanged` and the session writes the field automatically;
      “Capture State” remains the manual fallback.
- [~] 48 kHz is measured; 44.1/96 kHz are implemented but not measured.
- [x] Mono downmix produces finite, non-silent output for a stereo plugin.
- [ ] Latency compensation aligns a known impulse (Surge reports zero latency,
      so this path was not exercised).
- [x] Tail capture renders beyond the last note (Surge’s 2 s tail produced a
      146 400-sample segment for a 1 s chord).
- [ ] The same project sounds identical after a re-render (byte comparison).
- [~] All process-global names differ from epmixer; coexistence was not tested
      with both extensions loaded at once.
- [x] The win-x86 helper loads and describes a 32-bit VST2 plugin.
- [ ] Linux x64/arm64 and macOS x64/arm64 smoke tests.
- [ ] macOS signing/notarization and hardened-runtime entitlement.
- [x] `.tlx` produced. Seven self-contained RIDs plus the OwnVST3 native
      libraries make it ~248 MB; acceptable for development, not yet for release.
