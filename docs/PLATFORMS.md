# Platforms, RIDs and native components

## 1. Reality check

The request lists Linux/Windows/macOS on arm64/x64/x86 for all five formats.
That full Cartesian product is not achievable with the current TuneLab host and
plugin ecosystems. This page separates “what the code can support” from “what
the host/runtime/format actually allows”.

| OS \ Arch | x64 | arm64 | x86 |
| --- | --- | --- | --- |
| Windows | ✅ host + all formats except AU | ⚠ host not in official CI; helper can be published; VST3 arm64 native exists | ⚠ win-x86 helper works under WOW64; no VST3 x86 native |
| Linux | ✅ host + CLAP/VST3/LV2; VST2 best effort | ⚠ host not in official CI; helper can be published | ❌ no .NET 8 linux-x86 runtime; native bridge required |
| macOS | ⚠ host not in official CI; CLAP/VST3/LV2/AU x64; Rosetta on arm64 | ✅ host + CLAP/VST3/LV2/AU | ❌ modern macOS cannot run 32-bit code |

Legend: ✅ supported by the official host build and the format; ⚠ technically
possible, needs extra publishing/validation; ❌ not possible.

## 2. Helper RIDs

The helper is published per RID. In the repository the staging folder is
`artifacts/bridge/` (the source tree already has a `Bridge/` folder, which is
case-insensitively the same path on Windows/macOS); inside the package it is
`bridge/`:

```
artifacts/bridge/                     (source staging)
  win-x64/TuneLab.VstInstruments.Bridge.exe
  win-x86/TuneLab.VstInstruments.Bridge.exe
  win-arm64/TuneLab.VstInstruments.Bridge.exe
  linux-x64/TuneLab.VstInstruments.Bridge
  linux-arm64/TuneLab.VstInstruments.Bridge
  osx-x64/TuneLab.VstInstruments.Bridge
  osx-arm64/TuneLab.VstInstruments.Bridge

<package>/bridge/<rid>/...            (installed layout)
```

All seven RIDs were published self-contained on this machine. win-x64,
win-x86 and linux-x64 were executed; the macOS/arm64 helpers are build
artifacts, not runtime-tested here.

The client picks the helper by the **plugin’s** RID, not the host’s. The
catalog reads the plugin’s binary header (`Catalog/PluginBinaryProbe.cs`):

- Windows: PE `Machine` (0x014C x86, 0x8664 x64, 0xAA64 arm64);
- Linux: ELF `e_machine` (EM_386, EM_X86_64, EM_AARCH64);
- macOS: Mach-O `cputype` (CPU_TYPE_X86, CPU_TYPE_X86_64, CPU_TYPE_ARM64) and
  the `Contents/MacOS` binary inside a bundle.

This is what lets a 64-bit TuneLab list a 32-bit VST2: the probe returns
`win-x86`, and the catalog starts the win-x86 helper for that file.

Cross-RID execution rules:

| Host | Helper | Works? |
| --- | --- | --- |
| win-x64 | win-x86 | yes (WOW64) |
| win-x64 | win-arm64 | only on Windows 11 arm64 with x64 emulation; not the reverse |
| win-arm64 | win-x64 | yes (x64 emulation) |
| win-arm64 | win-x86 | yes (x86 emulation) |
| linux-x64 | linux-arm64 | no |
| linux-arm64 | linux-x64 | no |
| osx-arm64 | osx-x64 | yes, if Rosetta 2 is installed |
| osx-x64 | osx-arm64 | no |
| any macOS | osx-x86 | no (32-bit unsupported) |

The client should verify the helper’s `HelloAck.rid` matches the requested RID.

## 3. Native components

| Component | File (per RID) | Used by | Source/license |
| --- | --- | --- | --- |
| VST3 host | `ownvst3.tlvsti.dll` / `libownvst3.tlvsti.so` / `libownvst3.tlvsti.dylib` | VST3 | `OwnAudioVst` 1.7.0 native, MIT wrapper; native license to confirm |
| AU host | same OwnVST3 native host | AU | Apple AudioToolbox through OwnVST3 |
| LV2 host | none (managed C ABI) | LV2 | dependency-free managed implementation |
| CLAP | none (managed C ABI) | CLAP | MIT headers/ABI |
| VST2 | none (managed AEffect ABI) | VST2 | reverse-engineered ABI |

The `.tlvsti` suffix is mandatory. It prevents Windows from resolving the same
file name to epmixer’s `.tlhost` library when both extensions are installed.

## 4. Packaging

`pack.ps1` stages:

- the extension DLL + manifest + docs;
- `bridge/<rid>/` for every helper that has been published;
- native libraries next to each helper, not in the extension root.

`pack.ps1` copies `artifacts/bridge/<rid>/` into `<package>/bridge/<rid>/`.
Each helper is published **self-contained** so it does not depend on a global
.NET 8 runtime (TuneLab itself may be self-contained). The resulting `.tlx`
with all seven RIDs is about **248 MB** (measured; includes the OwnVST3 native
libraries for the six 64-bit RIDs).

Size options, in order of preference:

1. **Trimmed self-contained helper** (`PublishTrimmed=true`, no reflection-heavy
   code): smaller per RID, but test after trimming.
2. **Framework-dependent helper** if the installation can expose a shared .NET
   8 runtime: a few hundred KB per RID, but a missing runtime becomes a support
   burden.
3. **Native C++ helper** for VST3/AU/LV2 (~1–3 MB per RID), managed helper only
   for CLAP/VST2. More code, much smaller package.
4. **Per-platform packages**: `VSTinstriumentForTunelab-win.tlx`,
   `-linux.tlx`, `-mac.tlx`, each with only that platform’s RIDs.

For development the single 248 MB package is acceptable; before release choose
(1) or (4).

## 5. macOS specifics

- Hardened runtime: loading third-party plugins requires
  `com.apple.security.cs.disable-library-validation` (and possibly
  `allow-unsigned-executable-memory` for some plugins).
- Sign the helper and every `.dylib`; notarize the `.tlx` contents or the app
  that installs them.
- AU components live in `/Library/Audio/Plug-Ins/Components`; VST3 in
  `/Library/Audio/Plug-Ins/VST3`; CLAP in `/Library/Audio/Plug-Ins/CLAP`.
- Rosetta 2 is required to run an osx-x64 helper on arm64. Detect it and show a
  clear message if absent.

## 6. Linux specifics

- `ownvst3` linux-x64 from upstream requires `GLIBC_2.38`. RHEL8/Anolis8
  (glibc 2.28) cannot load it. Build a manylinux-compatible shim or document
  the floor.
- Plugin paths: `/usr/lib/clap`, `/usr/lib/vst3`, `/usr/lib/lv2`, plus
  `~/.clap`, `~/.vst3`, `~/.lv2`.
- The helper uses Unix domain sockets and temp-file shared memory; clean up
  stale files on startup (same as epmixer’s `BridgeResidue`).
- No `linux-x86` .NET runtime; a 32-bit helper would have to be native.

## 7. Windows specifics

- x86 helper: published self-contained as `win-x86`; the x64 host starts it and
  assigns it to the job object. Verified by scanning a 32-bit VST2
  (`TDR Nova.dll`) from the x86 helper.
- arm64 helper: published as `win-arm64`; not executed here.
- CLAP is 64-bit only: a 32-bit CLAP file is rejected with a clear reason.
  VST2 is the only format that uses the x86 helper.
- **TODO:** set the plugin’s directory as the DLL search directory before
  loading (`SetDllDirectory` / `AddDllDirectory`) so sibling DLLs resolve.
  Currently `NativeLibrary.Load` uses the process search path; plugins with
  side-by-side DLLs may fail.
- Long paths: use `\\?\` or `LongPathsEnabled` for deep plugin folders.
- Editor windows: VST2, VST3 and LV2 are hosted on Windows; LV2 X11 is hosted
  on Linux; LV2 Cocoa is implemented but still needs a macOS GUI smoke test.

## 8. CI matrix proposal

| Job | Runner | Build | Test |
| --- | --- | --- | --- |
| win-x64 | windows-latest | extension + bridge + win-x86 | synthetic LV2; optional real CLAP/VST2/VST3 |
| linux-x64 | ubuntu-latest | extension + bridge | synthetic LV2 + real LSP Sampler Mono X11 UI |
| osx-x64 | macos-15-intel | extension + bridge | synthetic LV2, UI skipped |
| osx-arm64 | macos-15 | extension + bridge | synthetic LV2, UI skipped |

win-arm64/linux-arm64 are published locally but not part of the hosted CI
matrix yet.

macOS signing/notarization runs on the release job, not every PR.
