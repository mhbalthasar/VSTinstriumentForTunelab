# CI

The workflow is in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml).
The project is connected to
`github.com/mhbalthasar/VSTinstriumentForTunelab`; pushes to `main` run the
matrix and the packaging job. The Linux/x64 path has also been reproduced on
the local Linux debugger.

## Matrix

| Runner | RID | LV2 UI test |
| --- | --- | --- |
| `ubuntu-latest` | `linux-x64` | yes, under Xvfb (X11) |
| `windows-latest` | `win-x64`, plus `win-x86` helper | build-only (Windows UI is covered by the local harness) |
| `macos-15-intel` | `osx-x64`, plus `osx-arm64` build-only | build-only (Cocoa UI needs a GUI session) |

Each job:

1. checks out this extension;
2. checks out the public TuneLab SDK:
   `LiuYunPlayer/TuneLab` at `6cff736d63a403d06c51c2cf6db4474779e1cc57`, path `TuneLab2`;
3. installs .NET 8;
4. builds `TuneLab.SDK` (its project reference builds `TuneLab.Foundation`);
5. builds the extension with `TuneLabSdkRoot=$GITHUB_WORKSPACE/TuneLab2`;
6. publishes a self-contained bridge for the matrix RID; the Windows job also
   publishes `win-x86`, and the Intel macOS job cross-publishes `osx-arm64`
   (build-only, because GitHub-hosted Apple-silicon runners are not available
   to this repository);
7. builds the test harness;
8. compiles the synthetic LV2 bundle for that platform;
9. runs the harness.

After the matrix succeeds, the `package-tlx` job runs on `windows-latest`:

1. publishes all seven self-contained bridge RIDs with
   `publish-bridges.ps1 -Configuration Release`;
2. builds `TuneLab.SDK` in Release so the extension can resolve its references;
3. packs `dist/VSTinstriumentForTunelab.tlx` with `pack.ps1 -SkipBridges`;
4. uploads it as the `VSTinstriumentForTunelab-tlx` workflow artifact.

The Windows job uses MSVC (`ilammy/msvc-dev-cmd`) to compile the LV2
fixture. Linux needs `xvfb`, `libx11-dev` and `gcc`; `lsp-plugins-lv2` is
installed when available for the real LV2 UI test. macOS uses `clang` with
`-dynamiclib`.

## Real plugin tests are opt-in

CI does not ship proprietary or large local plugin corpora. The harness skips
real-plugin sections unless these files exist:

```text
TUNELAB_TEST_CLAP
TUNELAB_TEST_VST2
TUNELAB_TEST_VST2_32
TUNELAB_TEST_VST3
TUNELAB_TEST_VST3_EFFECT
TUNELAB_TEST_LV2
TUNELAB_TEST_LV2_NAME
```

On Linux CI, `lsp-plugins-lv2` is installed when available and the harness is
pointed at `LSP Sampler Mono`. That plugin exercises a real LV2 instrument with
an X11 UI, `ui:idleInterface`, worker/state interfaces, control parameters and
`time:Position` support. If the package is unavailable the section is skipped.

The synthetic LV2 tests always run when the fixture is built:

```text
TUNELAB_VSTI_LV2_FIXTURE
TUNELAB_VSTI_LV2_FIXTURE_32
```

The LV2 fixture covers scan, load, atom MIDI, audio, control, state, worker
requirements, options/buf-size requirements and (where the platform supports
it) UI open/close.

## Publishing this repository

The project is already committed on `main`. To connect it to GitHub, create an
empty repository (do not add a README) and push:

```powershell
$git = "C:\Users\balthasar\AppData\Local\GitHubDesktop\app-3.6.6\resources\app\git\cmd\git.exe"
& $git remote add origin https://github.com/<owner>/<repo>.git
& $git push -u origin main
```

Use GitHub Desktop's **Publish repository** command if you prefer the GUI.
Once the push lands, `.github/workflows/ci.yml` runs on `main`; the
`package-tlx` job uploads the `.tlx` artifact after all matrix jobs pass.

## GitHub secrets

Do not commit plugin paths or SSH credentials. Add plugin paths as repository
variables/secrets only if a private runner has the corpus. The Linux test box
credentials must never be written into the workflow; use an SSH key and a
self-hosted runner label if that machine is used.

## Linux debugger command sequence

The same sequence was used on the local Linux debugger:

```bash
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

dotnet build "$HOME/ci/TuneLab2/TuneLab.SDK/TuneLab.SDK.csproj" -c Release

dotnet build TuneLab.VstInstruments.csproj -c Release \
  -p:TuneLabSdkRoot="$HOME/ci/TuneLab2"

dotnet publish Bridge/Process/TuneLab.VstInstruments.Bridge.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:TuneLabSdkRoot="$HOME/ci/TuneLab2" \
  -o artifacts/bridge/linux-x64 --nologo -v:q

dotnet build TestHarness/TuneLab.VstInstruments.Tests.csproj \
  -c Release -p:TuneLabSdkRoot="$HOME/ci/TuneLab2"

TestHarness/LV2Fixture/build-linux.sh

export TUNELAB_VSTI_CI=1
export TUNELAB_VSTI_BRIDGE_DIR="$PWD/artifacts/bridge/linux-x64"
export TUNELAB_VSTI_LV2_FIXTURE="$PWD/TestHarness/LV2Fixture/TuneLab Test Linux.lv2"
export TUNELAB_TEST_LV2=/usr/lib64/lv2/lsp-plugins.lv2
export TUNELAB_TEST_LV2_NAME='LSP Sampler Mono'

Xvfb :99 -screen 0 1280x1024x24 >/tmp/tlvsti-xvfb.log 2>&1 &
XVFB_PID=$!
trap 'kill "$XVFB_PID" 2>/dev/null || true' EXIT
export DISPLAY=:99
sleep 1
dotnet TestHarness/bin/Release/net8.0/TuneLab.VstInstruments.Tests.dll
```

## Local Linux result

The exact Linux sequence above was run on the local Linux debugger
(`linux-x64`, Anolis 8.10, Xvfb):

```text
bridge formats: vst2, clap, lv2, vst3
LV2 loaded: TuneLab Test LV2 | params=1 out=2 editor=True
LV2 rendered: peak=0.2000 rms=0.0991 frames=48000
LV2 parameters: 1
LV2 state: 32 bytes
LV2 editor: ok=True
LV2 real loaded: LSP Sampler Mono | LSP LV2 | params=14 out=1 editor=True
LV2 real rendered: peak=0.0000 rms=0.0000 frames=48000
LV2 real parameters: 14
LV2 real state: 1656 bytes
LV2 real editor: ok=True
All tests passed.
```

`libX11-devel`, `gcc`, `Xvfb`, .NET 8 and `lsp-plugins-lv2` were installed on
that machine. LSP Sampler Mono is a real instrument with an X11 UI, worker,
state and time/transport ports; its sampler has no sample loaded, so the
real-plugin render is intentionally allowed to be silent. The real
CLAP/VST2/VST3 sections were skipped because that machine has no local plugin
corpus; GitHub Actions has the same default behavior.

## Optional Linux debugger as a self-hosted runner

The local Linux debugger can also be registered as a GitHub Actions runner.
Prepare it once:

```bash
sudo dnf install -y libX11-devel xvfb lsp-plugins-lv2
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet"
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc
echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.bashrc
```

Then download the runner bundle from the repository’s **Settings �?Actions �?
Runners �?New self-hosted runner** page, configure it with the
`linux-x64-debugger` label, and install it as a service:

```bash
./config.sh --url https://github.com/<owner>/<repo> --token <runner-token> \
  --labels linux-x64-debugger --work _work
sudo ./svc.sh install
sudo ./svc.sh start
```

A workflow job can opt into it with:

```yaml
runs-on: [self-hosted, linux-x64-debugger]
```

Do not put runner tokens or SSH passwords in the repository.

## Notes

- `TuneLab2` is checked out from the public upstream; the extension is not
  coupled to a private SDK build.
- `artifacts/` and test binaries are uploaded per RID for inspection.
- The `TuneLab2` repository itself has its own CI; this workflow only builds
  the SDK projects needed by the extension.
- macOS GitHub runners do not provide a normal interactive WindowServer, so
  Cocoa UI is compiled but not opened there. The local macOS test is still
  required before claiming Cocoa UI support.