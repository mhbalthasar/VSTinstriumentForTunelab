# TuneLab VST Instrument Host

A TuneLab **instrument** extension that makes external instrument plugins
selectable in TuneLab's sound-source menu and renders their output offline into
TuneLab audio segments.

Target formats: **CLAP**, **VST2.4**, **VST3**, **AU** (macOS) and **LV2**.
Target platforms: Windows / Linux / macOS on x64, arm64 and (where the platform
still allows it) x86.

> This is the mirror image of the experimental `epmixer` insert host:
> epmixer lists **effects and no instruments**; this extension lists
> **instruments and no effects**. The safe out-of-process bridge mechanism is
> reused, but every shared native component and every process-global name is
> renamed (`tlhost` → `tlvsti`, `TuneLab.VstBridge` →
> `TuneLab.VstInstrumentBridge`) so both extensions can be installed in the same
> TuneLab process.

## Status

First working milestone (0.1.2, SDK 1.2):

- **CLAP instruments** — scan, load, note/CC/pitch-bend events, parameter
  automation, reset/panic, latency/tail, state and parameter protocol, offline
  block rendering. Verified end-to-end with Surge XT CLAP on win-x64, including
  its native editor in a dedicated child window.
- **VST2.4 instruments** — managed AEffect host, `effProcessEvents`,
  synth-only filtering, chunks/parameters, tail/latency, and a working Windows
  editor host. Verified by scanning a 32-bit VST2 in the win-x86 helper,
  filtering a 64-bit VST2 effect in win-x64, and opening/closing the Dragonfly
  VST2 editor.
- **Settings UI (SDK 1.2)** — “Scan now” and “Scan range...” buttons. The range
  dialog is a TuneLab-style `PromptInputs` form with per-format path lists;
  paths persist in the extension’s own `scan-paths.json`.
  Standard per-format folders (Windows, macOS and Linux) are seeded once as
  ordinary editable rows, including 32-bit VST2/VST3 locations on Windows.
  After that the configured lists are authoritative: an empty list scans
  nothing and never falls back to the usual system plugin folders. Pressing
  `+` keeps a blank row visible so the folder picker can be used immediately.
- **Part panel (SDK 1.2)** — “Open Editor”, “Reload Plugin”, “Add Automation”,
  “Clear Automation”, a multiline **State** field keyed
  `vsti_state_<instrumentId>`, “Capture State” and “Clear State”. Add
  Automation loads the parameter list and opens a TuneLab-style selection form
  with a name/ID filter and a checkbox per continuous parameter; selected
  parameters become automation tracks (keyed `param:<id>`) and the renderer
  sends their values per block. The bridge polls the plugin state hash while
  the host is idle and sends a `StateChanged` event on the event pipe; the
  session then captures and writes the blob automatically. “Capture State” is
  the manual fallback, and “Clear State” removes both the automatic blob and
  the part field. The write uses the host’s
  `MidiPart.Properties.SetValue` path (reflection bridge) when available.
- **VST3 instruments** — vendored `OwnAudioVST3` native host + managed adapter.
  Scan/load, MIDI, audio, parameters, 80 KB state round-trip, and the native
  editor window. Verified with Surge XT VST3 on win-x64.
- **AU (macOS)** — implemented through the same OwnVST3 native host using the
  AudioUnit identifier from its scanner; `.component` paths are used for
  catalog/stamps. The package is published but not executed on this machine.
- **LV2 instruments** — dependency-free managed host for atom MIDI input,
  audio output, control ports, `state:StateInterface`, `LV2_Worker`,
  options/buf-size, time/transport atoms and native LV2 UI hosts for
  `ui:WindowsUI`, `ui:X11UI`, `ui:CocoaUI` and `ui:Gtk3UI`, including the
  `ui:idleInterface` feature. Gtk3 uses
  the vendored Suil 0.10.26 runtime and a native toolkit window (never an
  Avalonia window). Both x64 and x86 LV2 bundles are scanned and routed to
  the matching helper. Verified with x64/x86 synthetic LV2 synths and with
  the real **LSP Sampler Mono** on Linux: scan, load, atom MIDI, render,
  14 parameters, 1656-byte state, worker/state/time features and the X11 UI.
  Synthetic Gtk3 UIs also pass on Linux x64 and, natively, on Linux arm64.
  If the Gtk3 shim is missing or `TUNELAB_VSTI_DISABLE_SUIL=1` is set, the
  host falls back to X11 when the plugin offers it, then to the parameter
  panel; it never risks a non-X11 widget in an X11 host. Windows UI is tested;
  Cocoa is build-only until run on macOS.
- **Instrument classification** — CLI/format metadata is authoritative
  (`instrument` feature, `effFlagsIsSynth`, VST3/AU category, LV2
  `InstrumentPlugin`/designation). If a format provides no instrument marker,
  a scanned plugin is an instrument only when it has **no audio input and at
  least one audio output**; effects with an input bus are filtered out.
- **Helpers** — published self-contained for win-x64, win-x86, win-arm64,
  linux-x64, linux-arm64, osx-x64 and osx-arm64. win-x64, win-x86 and
  linux-x64 and linux-arm64 were executed; the other RIDs are build-only.
- **Not yet** — MPE; per-note pitch; macOS Cocoa UI smoke testing
  (Windows and Linux X11 UIs are tested); AU macOS smoke test; VST3/AU 32-bit
  hosting (LV2 x86 is supported).
- **Synthesis threading** - the render worker only reads the immutable
  `InstrumentSynthesisSnapshot`. `CreateAudioSegment`, state write-back and
  declaration invalidation are marshalled back to the data thread; status
  queries never touch the live `IInstrumentSynthesisContext`. This avoids the
  SDK 1.2 live-view thread guard.
- **Host-shape note** — automatic State write-back uses reflection into the
  host’s `InstrumentSynthesisContext.Part → MidiPart.Properties →
  DataPropertyObject.SetValue` path because SDK 1.2 only exposes a read-only
  view. State-change notifications travel on a separate `<pipe>.events` channel,
  never as a reply on the command pipe. If a future host renames those members,
  the extension falls back to the per-instrument state cache plus the base64
  dialog; it never blocks. The old generic `state` property from earlier builds
  is intentionally no longer read; re-capture once into the namespaced field.

The manifest targets `sdk-version: 1.2` because the settings/part buttons use
`ButtonConfig`, `IButtonClickContext` and `InvalidatePart`. A released 1.1
host will reject the package; a 1.2 host (current mainline / installed
experimental build) loads it.

See:

- [`docs/GAP-ANALYSIS.md`](docs/GAP-ANALYSIS.md) — what TuneLab, the epmixer
  bridge and each plugin format do and do not provide. **Read this first.**
- [`docs/DESIGN.md`](docs/DESIGN.md) — rendering model, isolation model,
  catalog/state/parameter model, phasing.
- [`docs/PROTOCOL.md`](docs/PROTOCOL.md) — host ↔ bridge wire protocol,
  including the note-event channel that epmixer does not have.
- [`docs/FORMATS.md`](docs/FORMATS.md) — per-format host requirements and risk.
- [`docs/PLATFORMS.md`](docs/PLATFORMS.md) — RID/native matrix and packaging.
- [`docs/TESTING.md`](docs/TESTING.md) — fake plugins, real-plugin probes and
  the current test harness.
- [`docs/CI.md`](docs/CI.md) — GitHub Actions matrix, synthetic LV2 fixtures
  and the Linux/Xvfb runbook.

## Build

Requirements: .NET 8 SDK and a TuneLab SDK checkout. The default is
`..\..\TuneLab2` (from this folder: `Y:\TuneLab2`); override with
`TUNELAB_SDK_ROOT` or `-p:TuneLabSdkRoot=...`.

```powershell
dotnet build .\TuneLab.VstInstruments.csproj -c Release
```

The extension references `TuneLab.SDK.dll` / `TuneLab.Foundation.dll` with
`Private=false`; they are host-provided and must not be shipped.

The build was verified against both the SDK-only drop at `Y:\TuneLab2`
(SDK 1.1) and the updated mainline checkout at
`Y:\DISK\Codes\TuneLabs\TuneLab2` (`upstream/master` 6cff736, 2026-11-03,
SDK 1.2; `LastReleasedSdkVersion` is still 1.1). To use the latter:

```powershell
dotnet build .\TuneLab.VstInstruments.csproj -c Release `
  -p:TuneLabSdkRoot=Y:\DISK\Codes\TuneLabs\TuneLab2
```

The package manifest is now `sdk-version: 1.2`: the settings/part buttons use
`ButtonConfig` / `IButtonClickContext`, and the part panel uses
`InvalidatePart`. It loads on the current 1.2 mainline / installed experimental
host; a released 1.1 host rejects it with the SDK-gate message. Keep a separate
1.1 build if compatibility with a released 1.1 host is required.

The helper process is a separate project:

```powershell
dotnet build .\Bridge\Process\TuneLab.VstInstruments.Bridge.csproj -c Release
```

Publish the self-contained helpers for the target RIDs:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-bridges.ps1 -Configuration Release
```

They are staged under `artifacts\bridge\<rid>\` (the source folder is `Bridge\`,
so the output deliberately does not use the name `bridge` inside the repo).

## Test harness

```powershell
dotnet build .\TestHarness\TuneLab.VstInstruments.Tests.csproj -c Debug
dotnet .\TestHarness\bin\Debug\net8.0\TuneLab.VstInstruments.Tests.dll
```

The harness starts the real helper, scans Surge XT CLAP, renders a C-major
chord directly and through `VstInstrumentSession`, builds the catalog, and
checks VST2 effect filtering on both win-x64 and win-x86. See
[`docs/TESTING.md`](docs/TESTING.md) for the current results.

## Package / install

```powershell
powershell -ExecutionPolicy Bypass -File .\pack.ps1
powershell -ExecutionPolicy Bypass -File .\pack.ps1 -Install
```

`pack.ps1` builds the extension, publishes/stages the helpers under
`artifacts\bridge\<rid>\`, copies them into `bridge\<rid>\` inside the package,
and writes `dist\VSTinstriumentForTunelab.tlx`. The helper is resolved beside the
installed extension assembly (`bridge\<rid>\...`), not beside TuneLab.exe.
With all seven self-contained
RIDs the package is roughly 244 MB (the VST3/AU native libraries add most of
the increase); see `docs/PLATFORMS.md` for size options.

## Non-goals

- This extension does not synthesize sound by itself. With no plugin installed
  it is silent by design.
- It does not host effect plugins. Use `epmixer` for effects.
- It does not replace the plugin's own GUI. Deep editing stays in the plugin;
  TuneLab exposes a small, stable part panel plus the plugin's opaque state.

## License

GNU GPL v3.0 (same as `WaveTablesForTunelab`). Third-party notices are in
`THIRD-PARTY-NOTICES.txt`.
