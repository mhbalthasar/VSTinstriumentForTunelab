using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TuneLab.Foundation;
using TuneLab.SDK;
using TuneLab.VstInstruments;
using TuneLab.VstInstruments.Bridge;
using TuneLab.VstInstruments.Catalog;

namespace TuneLab.VstInstruments.Tests;

internal static class Program
{
    static readonly string ClapSurge = EnvPath("TUNELAB_TEST_CLAP", @"Y:\Insts\VSTEffects\CLAP\01_Bundles\Surge XT.clap");
    static readonly string Vst2Effect = EnvPath("TUNELAB_TEST_VST2", @"Y:\Insts\VSTEffects\VST2\04_Reverb\DragonflyHallReverb-vst.dll");
    static readonly string Vst2Effect32 = EnvPath("TUNELAB_TEST_VST2_32", @"Y:\Insts\VSTEffects\VST2\03_EQ\TDR Nova.dll");
    static readonly string Vst3Surge = EnvPath("TUNELAB_TEST_VST3", @"Y:\Insts\VSTEffects\VST3\01_Bundles\Surge XT.vst3");
    static readonly string Vst3Effect = EnvPath("TUNELAB_TEST_VST3_EFFECT", @"Y:\Insts\VSTEffects\VST3\01_Bundles\Surge XT Effects.vst3");
    static readonly string Lv2Fixture = EnvPath("TUNELAB_VSTI_LV2_FIXTURE",
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestHarness", "LV2Fixture", "TuneLab Test.lv2"));
    static readonly string Lv2Fixture32 = EnvPath("TUNELAB_VSTI_LV2_FIXTURE_32",
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestHarness", "LV2Fixture", "TuneLab Test 32.lv2"));
    static readonly string Lv2Real = EnvPath("TUNELAB_TEST_LV2", "");
    static readonly string Lv2RealName = EnvPath("TUNELAB_TEST_LV2_NAME", "");

    static string EnvPath(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    

    static async Task<int> Main(string[] args)
    {
        ConfigureEnvironment();

        Console.WriteLine($"TuneLab VST instrument tests — host {BridgeIdentity.Rid}, {BridgeIdentity.Bits}-bit");

        try
        {
            var client = ProbeBridge();
            BinaryProbe();
            var surge = ScanClap(client);

            if (surge != null)
            {
                DirectClapRender(client, surge);
                var state = StateProbe(surge);
                await SessionRender(surge, state);
                AutomationProbe(surge);
            }

            RestartProbe(client);
            Vst3Probe(client);
            Lv2Probe(client, Lv2Fixture);
            Lv2RealProbe(client);
            Lv2Probe32();
            Lv2CatalogProbe();
            CatalogProbe();
            UiConfigProbe();
            StateStoreProbe();
            StateWriterProbe();
            ParameterFilterProbe();
            Vst2FilterProbe(client);

            Console.WriteLine("All tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex);
            return 1;
        }
        finally
        {
            VstInstrumentBridgeClient.ShutdownAll();
        }
    }

    static void ConfigureEnvironment()
    {
        // The apphost needs a runtime. The repository's local SDK has one; make
        // the helper inherit it so tests do not depend on a machine-wide install.
        const string localDotnet = @"Y:\devtools\dotnet";

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")) && Directory.Exists(localDotnet))
            Environment.SetEnvironmentVariable("DOTNET_ROOT", localDotnet);

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TUNELAB_VSTI_BRIDGE_DIR")))
        {
            string baseDirectory = AppContext.BaseDirectory;

            // Source staging layout: <repo>/artifacts/bridge/<rid>/...
            string artifacts = Path.GetFullPath(Path.Combine(baseDirectory,
                "..", "..", "..", "..", "artifacts", "bridge"));

            // Installed layout: <package>/bridge/<rid>/...
            string installed = Path.GetFullPath(Path.Combine(baseDirectory,
                "..", "..", "..", "..", "bridge"));

            if (Directory.Exists(Path.Combine(artifacts, "win-x64")))
            {
                Environment.SetEnvironmentVariable("TUNELAB_VSTI_BRIDGE_DIR", artifacts);
            }
            else if (Directory.Exists(Path.Combine(installed, "win-x64")))
            {
                Environment.SetEnvironmentVariable("TUNELAB_VSTI_BRIDGE_DIR", installed);
            }
            else
            {
                // Development fallback: a flat build output with one apphost.
                string debug = Path.GetFullPath(Path.Combine(baseDirectory,
                    "..", "..", "..", "..", "Bridge", "Process", "bin", "Debug"));

                if (File.Exists(Path.Combine(debug, "TuneLab.VstInstruments.Bridge.exe")))
                    Environment.SetEnvironmentVariable("TUNELAB_VSTI_BRIDGE_DIR", debug);
            }
        }
    }

    static VstInstrumentBridgeClient ProbeBridge()
    {
        var client = VstInstrumentBridgeClient.ForRid(BridgeIdentity.Rid);
        Check(client.EnsureStartedBlocking(), "bridge start failed: " + (client.LastBridgeError ?? "unknown"));
        Check(client.BridgeFormats != null && client.BridgeFormats.Length > 0, "the bridge declared no formats");

        Console.WriteLine($"  bridge formats: {string.Join(", ", client.BridgeFormats!)}");
        return client;
    }

    static void BinaryProbe()
    {
        if (!OperatingSystem.IsWindows()
            || !File.Exists(Vst2Effect32)
            || !File.Exists(Vst2Effect)
            || !File.Exists(ClapSurge))
        {
            Console.WriteLine("  binary probe: fixture unavailable, skipping");
            return;
        }

        Check(PluginBinaryProbe.DetectRid(Vst2Effect32, PluginFormats.Vst2) == "win-x86",
            "the 32-bit VST2 probe did not report win-x86");
        Check(PluginBinaryProbe.DetectRid(Vst2Effect, PluginFormats.Vst2) == "win-x64",
            "the 64-bit VST2 probe did not report win-x64");
        Check(PluginBinaryProbe.DetectRid(ClapSurge, PluginFormats.Clap) == "win-x64",
            "the CLAP probe did not report win-x64");

        Console.WriteLine("  binary probe: x86 / x64 headers detected");
    }

    static PluginInfo? ScanClap(VstInstrumentBridgeClient client)
    {
        if (!File.Exists(ClapSurge))
        {
            Console.WriteLine("  CLAP: no local Surge fixture, skipping real-plugin tests");
            return null;
        }

        var files = new List<string> { ClapSurge };

        Check(client.BridgeFormats!.Contains(PluginFormats.Clap), "the bridge does not host CLAP");

        var entries = client.ScanBlocking(PluginFormats.Clap, files, instrumentOnly: true,
            timeout: TimeSpan.FromSeconds(120));

        Check(entries != null, "CLAP scan failed: " + (client.LastBridgeError ?? "no reply"));

        foreach (var entry in entries!)
        {
            Console.WriteLine($"  scan: {entry.Name} | {entry.Vendor} | id={entry.PluginId} | " +
                $"instrument={entry.IsInstrument} | available={entry.IsAvailable} | {entry.UnavailableReason}");
        }

        var surge = entries!.FirstOrDefault(e =>
            e.IsInstrument && e.Name.Contains("Surge", StringComparison.OrdinalIgnoreCase));

        if (surge == null)
        {
            Console.WriteLine("  CLAP: Surge XT was not listed, skipping real-plugin tests");
            return null;
        }

        return surge;
    }

    static void Vst3Probe(VstInstrumentBridgeClient client)
    {
        if (!client.BridgeFormats!.Contains(PluginFormats.Vst3))
        {
            Console.WriteLine("  VST3: not advertised by this bridge");
            return;
        }

        if (!File.Exists(Vst3Surge) && !Directory.Exists(Vst3Surge))
        {
            Console.WriteLine("  VST3: no local Surge fixture, skipping real-plugin tests");
            return;
        }

        var infos = client.ScanBlocking(PluginFormats.Vst3, [Vst3Surge], instrumentOnly: true,
            timeout: TimeSpan.FromSeconds(120));

        Check(infos != null && infos.Count > 0,
            "VST3 scan returned no instrument: " + (client.LastBridgeError ?? "no reply"));

        if (Directory.Exists(Vst3Effect))
        {
            var effectInfos = client.ScanBlocking(PluginFormats.Vst3, [Vst3Effect], instrumentOnly: true,
                timeout: TimeSpan.FromSeconds(120));

            Check(effectInfos == null || effectInfos.All(p => !p.IsInstrument),
                "VST3 effect was not filtered out of the instrument scan");
            Console.WriteLine("  VST3 effect correctly filtered");
        }

        foreach (var candidate in infos!)
        {
            Console.WriteLine($"  VST3 scan entry: {candidate.Name} | {candidate.Vendor} | " +
                $"instrument={candidate.IsInstrument} available={candidate.IsAvailable} " +
                $"id={candidate.PluginId} locator={candidate.Locator} reason={candidate.UnavailableReason}");
        }

        var info = infos!.FirstOrDefault(p => p.IsInstrument && p.IsAvailable);
        Check(info != null, "VST3 scan returned no available instrument");
        Check(!string.IsNullOrEmpty(info!.Locator), "the VST3 descriptor has no locator");

        int slot = client.AllocateSlot();
        var load = client.LoadBlocking(slot, PluginFormats.Vst3, info.PluginId, info.Locator, 48000, 512);

        Check(load != null, "VST3 load did not answer: " + (client.LastBridgeError ?? "no reply"));
        Check(load!.Ok, "VST3 load failed: " + (load.Reason ?? "unknown"));
        Check(load.IsInstrument, "the loaded VST3 is not an instrument");
        Check(load.OutputChannels > 0, "the VST3 has no audio output");

        Console.WriteLine($"  VST3 loaded: {load.Name} | {load.Vendor} | latency={load.LatencySamples} " +
            $"params={load.ParameterCount} out={load.OutputChannels} editor={load.HasEditor}");

        Check(client.ResetBlocking(slot), "VST3 reset failed");

        var (peak, rms, frames) = RenderChord(client, slot, [60, 64, 67], 2.0, 48000, 512);

        Check(frames > 0, "VST3 rendered no frames");
        Check(float.IsFinite(peak), "VST3 output contains a non-finite sample");
        Check(peak > 0.001f, $"VST3 output is silent (peak {peak})");

        Console.WriteLine($"  VST3 rendered: peak={peak:0.0000} rms={rms:0.0000} frames={frames}");

        var parameters = client.GetParametersBlocking(slot);
        Check(parameters != null && parameters.Count > 0, "VST3 reported no parameters");

        if (parameters!.FirstOrDefault(p => p.IsAutomatable && p.Max > p.Min) is { } parameter)
        {
            double target = parameter.Min + (parameter.Max - parameter.Min) * 0.75;

            if (client.SetParameterBlocking(slot, parameter.Id, target))
                Console.WriteLine($"  VST3 parameter: {parameter.Name} -> {target:0.###}");
        }

        var state = client.GetStateBlocking(slot);
        Check(state != null && state.Length > 0, "VST3 GetState returned nothing");
        Console.WriteLine($"  VST3 state: {state!.Length} bytes");

        var editor = client.OpenEditorBlocking(slot, "Surge XT VST3");
        Console.WriteLine($"  VST3 editor: ok={editor.Ok} reason={editor.Reason}");

        if (editor.Ok)
        {
            Thread.Sleep(500);
            Check(client.CloseEditorBlocking(slot), "VST3 close editor failed");
        }

        Check(client.UnloadBlocking(slot), "VST3 unload failed");
    }

    static void Lv2Probe(VstInstrumentBridgeClient client, string bundle,
        bool requireAudio = true, bool requireState = true, string label = "LV2",
        string? nameFilter = null)
    {
        if (!client.BridgeFormats!.Contains(PluginFormats.Lv2))
        {
            Console.WriteLine($"  {label}: not advertised by this bridge");
            return;
        }

        if (!Directory.Exists(bundle))
        {
            Console.WriteLine($"  {label}: bundle not found, skipping");
            return;
        }

        var infos = client.ScanBlocking(PluginFormats.Lv2, [bundle], instrumentOnly: true,
            timeout: TimeSpan.FromSeconds(30));

        Check(infos != null && infos.Count > 0,
            $"{label} scan returned no instrument: " + (client.LastBridgeError ?? "no reply"));

        foreach (var candidate in infos!)
        {
            Console.WriteLine($"  {label} scan entry: {candidate.Name} | {candidate.Vendor} | " +
                $"instrument={candidate.IsInstrument} available={candidate.IsAvailable} " +
                $"id={candidate.PluginId} reason={candidate.UnavailableReason}");
        }

        var info = infos!.FirstOrDefault(p => p.IsInstrument && p.IsAvailable
            && (string.IsNullOrEmpty(nameFilter)
                || p.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)));

        Check(info != null, $"{label} scan returned no available instrument"
            + (string.IsNullOrEmpty(nameFilter) ? "" : $" matching '{nameFilter}'"));
        Check(!string.IsNullOrEmpty(info!.Locator), $"the {label} descriptor has no locator");

        int slot = client.AllocateSlot();
        var load = client.LoadBlocking(slot, PluginFormats.Lv2, info.PluginId, info.Locator, 48000, 512);

        Check(load != null, $"{label} load did not answer: " + (client.LastBridgeError ?? "no reply"));
        Check(load!.Ok, $"{label} load failed: " + (load.Reason ?? "unknown"));
        Check(load.IsInstrument, $"the loaded {label} is not an instrument");

        Console.WriteLine($"  {label} loaded: {load.Name} | {load.Vendor} | params={load.ParameterCount} " +
            $"out={load.OutputChannels} editor={load.HasEditor}");

        Check(client.ResetBlocking(slot), $"{label} reset failed");

        // Give a plugin that schedules worker jobs during instantiate/restore a
        // chance to finish before the first process block.
        Thread.Sleep(200);

        var (peak, rms, frames) = RenderChord(client, slot, [60, 64, 67], 1.0, 48000, 512);

        Check(frames > 0, $"{label} rendered no frames");
        Check(float.IsFinite(peak), $"{label} output contains a non-finite sample");

        if (requireAudio)
            Check(peak > 0.001f, $"{label} output is silent (peak {peak})");

        Console.WriteLine($"  {label} rendered: peak={peak:0.0000} rms={rms:0.0000} frames={frames}");

        var parameters = client.GetParametersBlocking(slot);
        Check(parameters != null && parameters.Count > 0, $"{label} reported no control parameters");
        Console.WriteLine($"  {label} parameters: {parameters!.Count}");

        if (parameters!.FirstOrDefault(p => p.IsAutomatable && p.Max > p.Min) is { } parameter)
        {
            double target = parameter.Min + (parameter.Max - parameter.Min) * 0.25;
            Check(client.SetParameterBlocking(slot, parameter.Id, target),
                $"{label} parameter update failed");
            Console.WriteLine($"  {label} parameter: {parameter.Name} -> {target:0.###}");
        }

        var state = client.GetStateBlocking(slot);

        if (requireState)
            Check(state != null && state.Length > 0, $"{label} state interface returned no state");

        if (state is { Length: > 0 })
        {
            Check(client.SetStateBlocking(slot, state), $"{label} restore state failed");
            Console.WriteLine($"  {label} state: {state.Length} bytes");
        }
        else
        {
            Console.WriteLine($"  {label} state: unavailable");
        }

        if (Environment.GetEnvironmentVariable("TUNELAB_VSTI_SKIP_UI") != "1")
        {
            if (load.HasEditor)
            {
                var editor = client.OpenEditorBlocking(slot, $"{label} Test UI");
                Console.WriteLine($"  {label} editor: ok={editor.Ok} reason={editor.Reason}");
                Check(editor.Ok, $"{label} editor failed: {editor.Reason}");

                Thread.Sleep(400);
                Check(client.CloseEditorBlocking(slot), $"{label} close editor failed");
            }
            else
            {
                Console.WriteLine($"  {label} editor: plugin has no UI");
            }
        }

        Check(client.UnloadBlocking(slot), $"{label} unload failed");
    }

    static void Lv2RealProbe(VstInstrumentBridgeClient client)
    {
        if (string.IsNullOrEmpty(Lv2Real))
        {
            Console.WriteLine("  LV2 real: no TUNELAB_TEST_LV2 bundle, skipping");
            return;
        }

        Lv2Probe(client, Lv2Real, requireAudio: false, requireState: false,
            label: "LV2 real", nameFilter: string.IsNullOrEmpty(Lv2RealName) ? null : Lv2RealName);
    }

    static void Lv2Probe32()
    {
        if (!OperatingSystem.IsWindows()
            || !File.Exists(Path.Combine(Lv2Fixture32, "test_lv2_32.dll")))
            return;

        var client = VstInstrumentBridgeClient.ForRid("win-x86");
        Check(client.EnsureStartedBlocking(), "x86 LV2 bridge start failed: " + (client.LastBridgeError ?? "unknown"));
        Check(client.BridgeFormats!.Contains(PluginFormats.Lv2), "the x86 bridge does not advertise LV2");

        Lv2Probe(client, Lv2Fixture32);
    }

    static void Lv2CatalogProbe()
    {
        if (!OperatingSystem.IsWindows()
            || !File.Exists(Path.Combine(Lv2Fixture32, "test_lv2_32.dll")))
            return;

        var settings = new VstInstrumentSettings { SampleRate = 48000, BlockSize = 512 };
        settings.Lv2Paths.Add(Path.GetDirectoryName(Lv2Fixture32)!);

        var catalog = PluginCatalogLoader.LoadOrScan(settings, new NullLogger(), forceScan: true);

        foreach (var e in catalog.ById.Values)
            Console.WriteLine($"  LV2 catalog entry: {e.Format} | {e.Name} | bits={e.Bits} | available={e.IsAvailable} | {e.UnavailableReason}");

        var entry = catalog.ById.Values.FirstOrDefault(e =>
            e.Format == PluginFormats.Lv2 && e.Name.Contains("32", StringComparison.OrdinalIgnoreCase));

        Check(entry != null, "the catalog did not route the 32-bit LV2 bundle to a helper");
        Check(entry!.Bits == 32, "the 32-bit LV2 catalog entry has the wrong bitness");
        Console.WriteLine("  LV2 catalog: 32-bit bundle routed to win-x86");
    }

    static void DirectClapRender(VstInstrumentBridgeClient client, PluginInfo surge)
    {
        int slot = client.AllocateSlot();
        int stateEvents = 0;
        client.StateChanged += _ => Interlocked.Increment(ref stateEvents);

        var load = client.LoadBlocking(slot, PluginFormats.Clap, surge.PluginId, surge.Locator, 48000, 512);

        Check(load != null, "load did not answer: " + (client.LastBridgeError ?? "no reply"));
        Check(load!.Ok, "load failed: " + (load.Reason ?? "unknown"));
        Check(load.IsInstrument, "the loaded plugin is not an instrument");

        for (int i = 0; i < 40 && !client.EventChannelConnected; i++)
            Thread.Sleep(50);

        Check(client.EventChannelConnected,
            "the bridge event channel did not connect: " + (client.LastEventError ?? "no error"));
        Console.WriteLine("  bridge event channel: connected");

        Console.WriteLine($"  loaded: {load.Name} | {load.Vendor} | latency={load.LatencySamples} " +
            $"tail={load.TailSamples} params={load.ParameterCount} out={load.OutputChannels} " +
            $"editor={load.HasEditor}");

        Check(client.ResetBlocking(slot), "reset failed");

        var (peak, rms, frames) = RenderChord(client, slot, [60, 64, 67], 2.0, 48000, 512);

        Check(frames > 0, "no frames were rendered");
        Check(float.IsFinite(peak), "the output contains a non-finite sample");
        Check(peak > 0.001f, $"the output is silent (peak {peak})");

        Console.WriteLine($"  rendered: peak={peak:0.0000} rms={rms:0.0000} frames={frames}");

        client.PanicBlocking(slot);

        var parameters = client.GetParametersBlocking(slot);
        var linear = parameters?.FirstOrDefault(p =>
            p.IsAutomatable && p.Steps == 0 && p.Max > p.Min && !string.IsNullOrWhiteSpace(p.Name));

        if (linear != null)
        {
            double target = linear.Min + (linear.Max - linear.Min) * 0.75;
            var outcome = client.ProcessBlocking(slot, 512, [], null, bypass: false, timeoutMs: 60000,
                parameters: [new ParameterEvent { Id = linear.Id, Value = target }]);

            Check(outcome.Ok, "parameter block failed: " + (outcome.Reason ?? "unknown"));

            var after = client.GetParametersBlocking(slot)?.FirstOrDefault(p => p.Id == linear.Id)?.Value ?? double.NaN;
            Console.WriteLine($"  parameter automation: {linear.Name} {linear.Value:0.###} -> {after:0.###}");
        }

        var editor = client.OpenEditorBlocking(slot, "Surge XT");
        Console.WriteLine($"  editor: ok={editor.Ok} reason={editor.Reason} bridge={client.LastBridgeError}");

        if (editor.Ok)
        {
            Thread.Sleep(300);
            Check(client.CloseEditorBlocking(slot), "close editor failed");
        }

        // Host-driven process/parameter/reset changes must not look like user
        // edits. The bridge suppresses them and re-baselines on the first idle
        // scan.
        Thread.Sleep(1200);
        Check(Volatile.Read(ref stateEvents) == 0,
            $"host-driven operations produced {stateEvents} state-changed event(s)");
        Console.WriteLine("  bridge state events: host-driven changes suppressed");

        Check(client.UnloadBlocking(slot), "unload failed");
    }

    static (float Peak, double Rms, int Frames) RenderChord(VstInstrumentBridgeClient client, int slot,
        IReadOnlyList<int> notes, double seconds, double sampleRate, int blockSize)
    {
        int totalFrames = (int)Math.Round(seconds * sampleRate);
        int noteOffFrame = (int)(sampleRate * 0.5);
        var onSent = new bool[notes.Count];
        var offSent = new bool[notes.Count];

        float peak = 0;
        double sum = 0;
        long count = 0;

        for (int offset = 0; offset < totalFrames; offset += blockSize)
        {
            int frames = Math.Min(blockSize, totalFrames - offset);
            var events = new List<MidiEvent>();

            for (int i = 0; i < notes.Count; i++)
            {
                if (!onSent[i])
                {
                    events.Add(new MidiEvent
                    {
                        Type = MidiEventKind.NoteOn,
                        Offset = 0,
                        Channel = 0,
                        Note = notes[i],
                        Velocity = 100,
                        NoteId = i + 1,
                    });
                    onSent[i] = true;
                }

                if (!offSent[i] && offset + frames > noteOffFrame)
                {
                    events.Add(new MidiEvent
                    {
                        Type = MidiEventKind.NoteOff,
                        Offset = Math.Max(0, noteOffFrame - offset),
                        Channel = 0,
                        Note = notes[i],
                        Velocity = 64,
                        NoteId = i + 1,
                    });
                    offSent[i] = true;
                }
            }

            var outcome = client.ProcessBlocking(slot, frames, [.. events], new TransportInfo
            {
                Playing = true,
                Tempo = 120,
                SamplePosition = offset,
                PpqPosition = offset / sampleRate * 2.0,
            }, bypass: false, timeoutMs: 60000);

            Check(outcome.Ok, "process failed: " + (outcome.Reason ?? "unknown") + " | "
                + (client.LastBridgeError ?? "no bridge error"));

            foreach (var plane in outcome.Planes)
            {
                foreach (float sample in plane)
                {
                    if (!float.IsFinite(sample))
                        return (float.NaN, double.NaN, totalFrames);

                    peak = Math.Max(peak, Math.Abs(sample));
                    sum += sample * sample;
                    count++;
                }
            }
        }

        return (peak, count == 0 ? 0 : Math.Sqrt(sum / count), totalFrames);
    }

    static string? StateProbe(PluginInfo surge)
    {
        var client = VstInstrumentBridgeClient.ForRid(BridgeIdentity.Rid);

        int slot = client.AllocateSlot();
        var load = client.LoadBlocking(slot, PluginFormats.Clap, surge.PluginId, surge.Locator, 48000, 512);
        Check(load?.Ok == true, "state probe load failed: " + (load?.Reason ?? "unknown"));

        var state = client.GetStateBlocking(slot);
        Check(state != null && state.Length > 0, "the plugin returned no state");
        string base64 = Convert.ToBase64String(state!);
        client.UnloadBlocking(slot);

        int reloadSlot = client.AllocateSlot();
        var reload = client.LoadBlocking(reloadSlot, PluginFormats.Clap, surge.PluginId, surge.Locator,
            48000, 512, state: base64);

        Check(reload?.Ok == true, "state reload failed: " + (reload?.Reason ?? "unknown"));
        client.UnloadBlocking(reloadSlot);

        Console.WriteLine($"  plugin state round-trip: {state!.Length} bytes");
        return base64;
    }

    static void RestartProbe(VstInstrumentBridgeClient client)
    {
        client.KillBridge();
        Check(!client.IsRunning, "the bridge did not stop after KillBridge");

        Check(client.EnsureStartedBlocking(), "the bridge did not restart: " + (client.LastBridgeError ?? "unknown"));

        var entries = client.ScanBlocking(PluginFormats.Clap, [ClapSurge], instrumentOnly: true,
            timeout: TimeSpan.FromSeconds(60));

        Check(entries != null && entries.Count > 0, "scan after restart failed");
        Console.WriteLine("  hard kill + restart: ok");
    }

    static void CatalogProbe()
    {
        if (!File.Exists(ClapSurge))
        {
            Console.WriteLine("  catalog: local CLAP fixture unavailable, skipping");
            return;
        }

        var settings = new VstInstrumentSettings { SampleRate = 48000, BlockSize = 512 };
        settings.ClapPaths.Add(Path.GetDirectoryName(ClapSurge)!);

        var catalog = PluginCatalogLoader.LoadOrScan(settings, new NullLogger(), forceScan: true);

        Check(catalog.ById.Count > 0, "the catalog is empty");
        Check(catalog.ById.Values.Any(e => e.IsInstrument && e.Name.Contains("Surge", StringComparison.OrdinalIgnoreCase)),
            "the catalog does not contain Surge XT");
        Check(catalog.Layout.Count > 0, "the catalog has no layout");

        Console.WriteLine($"  catalog: {catalog.ById.Count} entry/entries, {catalog.Layout.Count} format group(s)");
    }

    static void UiConfigProbe()
    {
        var engine = new VstInstrumentEngine();

        var settings = engine.GetSettingsConfig(new FakeSettingsContext(PropertyObject.Empty));

        IControllerConfig? actionConfig = null;
        foreach (var kv in settings.Properties)
        {
            if (kv.Key.Id == "group_actions")
            {
                actionConfig = kv.Value;
                break;
            }
        }

        var actions = actionConfig as ObjectConfig;

        Check(actions != null, "the settings page has no action group");
        var actionGroup = actions!;
        Check(actionGroup.Properties.Any(kv => kv.Key.Id == "scan_now" && kv.Value is ButtonConfig),
            "the settings page has no Scan now button");
        Check(actionGroup.Properties.Any(kv => kv.Key.Id == "scan_paths" && kv.Value is ButtonConfig),
            "the settings page has no Scan range button");

        var part = engine.GetPartPropertyConfig(new FakePartPropertyContext
        {
            Parts = [new FakePartView { InstrumentId = "clap:test", PartId = "part-1" }],
        });

        Check(part.Properties.Any(kv => kv.Key.Id == "open_editor" && kv.Value is ButtonConfig),
            "the part panel has no Open Editor button");
        Check(part.Properties.Any(kv => kv.Key.Id == "reload_plugin" && kv.Value is ButtonConfig),
            "the part panel has no Reload Plugin button");
        Check(part.Properties.Any(kv => kv.Key.Id == "add_automation" && kv.Value is ButtonConfig),
            "the part panel has no Add Automation button");
        Check(part.Properties.Any(kv => kv.Key.Id == "clear_automation" && kv.Value is ButtonConfig),
            "the part panel has no Clear Automation button");
        Check(part.Properties.Any(kv => kv.Key.Id == "capture_state" && kv.Value is ButtonConfig),
            "the part panel has no Capture State button");
        Check(part.Properties.Any(kv => kv.Key.Id == "clear_state" && kv.Value is ButtonConfig),
            "the part panel has no Clear State button");

        string stateKey = VstInstrumentSession.StatePropertyKey("clap:test");
        Check(part.Properties.Any(kv => kv.Key.Id == stateKey && kv.Value is TextBoxConfig),
            "the part panel has no per-instrument vsti_state field");
        Check(!part.Properties.Any(kv => kv.Key.Id == "state"),
            "the part panel still exposes the colliding state field");

        var lv2Part = engine.GetPartPropertyConfig(new FakePartPropertyContext
        {
            Parts = [new FakePartView { InstrumentId = "lv2:test", PartId = "part-1" }],
        });

        Check(lv2Part.Properties.Any(kv => kv.Key.Id == "open_editor"),
            "the LV2 part panel has no editor button");
        Check(lv2Part.Properties.Any(kv => kv.Key.Id == VstInstrumentSession.StatePropertyKey("lv2:test")),
            "the LV2 part panel has no state field");

        Console.WriteLine("  UI config: settings and part buttons present");
    }

    static void StateStoreProbe()
    {
        const string instrumentId = "test:state-probe";
        byte[] data = [1, 2, 3, 4, 5];

        InstrumentStateStore.Save(instrumentId, data);

        Check(InstrumentStateStore.TryLoad(instrumentId, out var loaded)
            && loaded.SequenceEqual(data), "state store round-trip failed");

        InstrumentStateStore.Clear(instrumentId);
        Check(!InstrumentStateStore.TryLoad(instrumentId, out _), "state store clear failed");

        Console.WriteLine("  state store round-trip: ok");
    }

    static void StateWriterProbe()
    {
        var context = new FakeWritableContext();

        Check(PartStateWriter.TryWrite(context, "vsti_state_probe", "abc"),
            "state reflection write failed");

        Check(context.Part.Properties.Values.TryGetValue("vsti_state_probe", out var value)
            && value.ToString(out var text) && text == "abc",
            "state value was not written into the part properties");

        Console.WriteLine("  part state write-back: ok");
    }

    static void ParameterFilterProbe()
    {
        var parameters = new List<ParameterInfo>
        {
            new() { Id = 1, Name = "Filter Cutoff", Min = 0, Max = 1, DefaultValue = 0.5, IsAutomatable = true },
            new() { Id = 2, Name = "Filter Resonance", Min = 0, Max = 1, DefaultValue = 0.5, IsAutomatable = true },
            new() { Id = 3, Name = "Volume", Min = 0, Max = 1, DefaultValue = 0.5, IsAutomatable = true },
        };

        var values = new PropertyObject(new Map<string, PropertyValue>
        {
            ["filter"] = PropertyValue.Create("cutoff"),
        });

        var config = VstInstrumentEngine.BuildParameterSelectionConfig(
            parameters, new HashSet<long>(), firstSelection: true, values);

        int count = config.Properties.Count(kv => kv.Value is CheckBoxConfig);
        Check(count == 1, $"parameter filter should keep 1 row, got {count}");

        Console.WriteLine("  parameter selection filter: ok");
    }

    static async Task SessionRender(PluginInfo surge, string? state)
    {
        var entry = new CatalogEntry
        {
            Id = PluginFormats.Clap + ":" + surge.PluginId,
            Format = PluginFormats.Clap,
            PluginId = surge.PluginId,
            Name = surge.Name,
            Vendor = surge.Vendor,
            Category = surge.Category,
            Locator = surge.Locator,
            IsInstrument = true,
            IsAvailable = true,
            Bits = surge.Bits,
            Arch = surge.Arch,
            Rid = surge.Rid,
        };

        var partProperties = new FakePropertyObject(new Dictionary<string, PropertyValue>
        {
            ["sample_rate"] = PropertyValue.Create("48000"),
            ["tail_ms"] = PropertyValue.Create(1000.0),
            ["pre_roll_ms"] = PropertyValue.Create(50.0),
            ["gain_db"] = PropertyValue.Create(0.0),
            ["transpose"] = PropertyValue.Create(0.0),
            ["midi_channel"] = PropertyValue.Create(1.0),
            ["stereo_mode"] = PropertyValue.Create(StereoModes.MonoSum),
            [VstInstrumentSession.StatePropertyKey(entry.Id)] = PropertyValue.Create(state ?? string.Empty),
        });

        var notes = new FakeNoteList();
        notes.Add(new FakeNote(0.5, 1.5, 60));
        notes.Add(new FakeNote(0.5, 1.5, 64));
        notes.Add(new FakeNote(0.5, 1.5, 67));

        var context = new FakeContext(entry.Id, notes, partProperties);
        var settings = new VstInstrumentSettings
        {
            SampleRate = 48000,
            BlockSize = 512,
            PreRollMs = 50,
            TailMs = 1000,
            StereoMode = StereoModes.MonoSum,
        };

        using var session = new VstInstrumentSession(context, entry, settings, []);

        var range = session.GetNextPendingSynthesisRange(0, 10);
        Check(range != null, "the session reported no pending piece");

        await session.SynthesizeNext(0, 10);

        Check(context.Segments.Count == 1, $"expected one segment, got {context.Segments.Count}");

        var segment = context.Segments[0];
        Check(segment.Committed, "the segment was not committed");
        Check(segment.Samples.Length > 0, "the segment is empty");

        float peak = 0;

        foreach (float sample in segment.Samples)
        {
            Check(float.IsFinite(sample), "the segment contains a non-finite sample");
            peak = Math.Max(peak, Math.Abs(sample));
        }

        Check(peak > 0.001f, $"the segment is silent (peak {peak})");
        Check(segment.SampleRate == 48000, "wrong segment sample rate");

        Console.WriteLine($"  session segment: offset={segment.SampleOffset} " +
            $"samples={segment.Samples.Length} peak={peak:0.0000}");

        var client = VstInstrumentBridgeClient.ForPlugin(entry.Bits, entry.Rid);
        Check(session.SlotForTest != 0, "the session has no bridge slot");
        Check(client.EventChannelConnected, "the event channel is not connected");

        string stateKey = VstInstrumentSession.StatePropertyKey(entry.Id);
        client.RaiseStateChangedForTest(session.SlotForTest);

        bool captured = false;

        for (int i = 0; i < 100; i++)
        {
            if (context.Part.Properties.Values.TryGetValue(stateKey, out var stateValue)
                && stateValue.ToString(out var stateText)
                && stateText.Length > 0)
            {
                captured = true;
                break;
            }

            await Task.Delay(50);
        }

        Check(captured, "automatic state capture did not write the vsti_state field");
        InstrumentStateStore.Clear(entry.Id);

        await session.ClearStateInPartAsync();
        Check(!context.Part.Properties.Values.ContainsKey(stateKey),
            "ClearState did not remove the vsti_state field");

        Console.WriteLine("  automatic state capture: ok");
        Console.WriteLine("  state field clear: ok");
    }

    static void AutomationProbe(PluginInfo surge)
    {
        string entryId = PluginFormats.Clap + ":" + surge.PluginId;
        var client = VstInstrumentBridgeClient.ForRid(BridgeIdentity.Rid);

        int slot = client.AllocateSlot();
        var load = client.LoadBlocking(slot, PluginFormats.Clap, surge.PluginId, surge.Locator, 48000, 512);
        Check(load?.Ok == true, "automation probe load failed: " + (load?.Reason ?? "unknown"));

        var parameters = client.GetParametersBlocking(slot);
        client.UnloadBlocking(slot);

        Check(parameters != null && parameters.Count > 0, "the plugin reported no parameters");
        var linear = parameters!.Where(ParameterAutomationStore.IsLinear).Take(5).ToList();
        Check(linear.Count > 0, "the plugin has no linear parameters");

        ParameterAutomationStore.SetParameters(entryId, parameters!,
            linear.Select(p => p.Id).ToList());

        try
        {
            var engine = new VstInstrumentEngine();
            var tracks = engine.GetAutomationConfigs(new FakePartPropertyContext
            {
                Parts = [new FakePartView { InstrumentId = entryId, PartId = "part-1" }],
            });

            int count = tracks.Count(kv => kv.Key.Id.StartsWith("param:", StringComparison.Ordinal));
            Check(count == linear.Count, $"expected {linear.Count} parameter lanes, got {count}");

            Console.WriteLine($"  automation tracks: {count} parameter lane(s)");
        }
        finally
        {
            ParameterAutomationStore.Clear(entryId);
        }
    }

    static void Vst2FilterProbe(VstInstrumentBridgeClient client)
    {
        Check(client.BridgeFormats!.Contains(PluginFormats.Vst2), "the bridge does not host VST2");

        if (File.Exists(Vst2Effect))
        {
            var instruments = client.ScanBlocking(PluginFormats.Vst2, [Vst2Effect], instrumentOnly: true,
                timeout: TimeSpan.FromSeconds(60));

            Check(instruments != null, "VST2 instrument-only scan failed: " + (client.LastBridgeError ?? "no reply"));
            Check(instruments!.All(e => e.IsAvailable && e.IsInstrument),
                "the instrument-only scan published an effect");

            var all = client.ScanBlocking(PluginFormats.Vst2, [Vst2Effect], instrumentOnly: false,
                timeout: TimeSpan.FromSeconds(60));

            Check(all != null && all.Count > 0, "VST2 scan returned nothing");
            Check(all![0].IsAvailable, "the 64-bit VST2 effect did not load: " + (all[0].UnavailableReason ?? "unknown"));
            Check(!all[0].IsInstrument, "Dragonfly Hall Reverb should be an effect");

            Console.WriteLine($"  VST2 effect correctly filtered: {all[0].Name} ({all[0].Category})");

            // VST2 editor: the effect is only loaded to exercise the editor
            // host, not to publish it as an instrument.
            int editorSlot = client.AllocateSlot();
            var effectLoad = client.LoadBlocking(editorSlot, PluginFormats.Vst2, string.Empty,
                Vst2Effect, 48000, 512, instrumentOnly: false);

            if (effectLoad?.Ok == true)
            {
                var editor = client.OpenEditorBlocking(editorSlot, "Dragonfly Reverb");
                Console.WriteLine($"  VST2 editor: ok={editor.Ok} reason={editor.Reason} " +
                    $"hasEditor={effectLoad.HasEditor}");

                if (editor.Ok)
                {
                    Thread.Sleep(300);
                    client.CloseEditorBlocking(editorSlot);
                }

                client.UnloadBlocking(editorSlot);
            }
            else
            {
                Console.WriteLine("  VST2 editor probe skipped: " + (effectLoad?.Reason ?? "load failed"));
            }
        }
        else
        {
            Console.WriteLine("  VST2 64-bit probe skipped (no local file)");
        }

        // The local TDR Nova is a 32-bit DLL. A 64-bit helper must report it as
        // unavailable; running it needs the x86 helper and an x86 .NET runtime.
        if (File.Exists(Vst2Effect32))
        {
            var entries = client.ScanBlocking(PluginFormats.Vst2, [Vst2Effect32], instrumentOnly: false,
                timeout: TimeSpan.FromSeconds(60));

            Check(entries != null, "VST2 32-bit probe failed: " + (client.LastBridgeError ?? "no reply"));

            if (entries!.Count > 0 && (!entries[0].IsAvailable || entries[0].Bits == 32))
            {
                Console.WriteLine($"  32-bit VST2 correctly not loaded by the 64-bit helper: " +
                    $"{entries[0].UnavailableReason}");
            }
        }

        // If a win-x86 helper is staged, it can actually load the 32-bit DLL.
        if (File.Exists(Vst2Effect32) && VstInstrumentBridgeClient.BridgeExecutableExists("win-x86"))
        {
            var x86 = VstInstrumentBridgeClient.ForRid("win-x86");
            Check(x86.EnsureStartedBlocking(), "x86 bridge start failed: " + (x86.LastBridgeError ?? "unknown"));
            Check(x86.BridgeFormats!.Contains(PluginFormats.Vst2), "the x86 bridge does not host VST2");

            var entries = x86.ScanBlocking(PluginFormats.Vst2, [Vst2Effect32], instrumentOnly: false,
                timeout: TimeSpan.FromSeconds(60));

            Check(entries != null, "x86 VST2 scan failed: " + (x86.LastBridgeError ?? "no reply"));
            Check(entries!.Count > 0 && entries[0].IsAvailable,
                "the x86 helper could not load the 32-bit VST2: " + (entries[0].UnavailableReason ?? "unknown"));
            Check(!entries[0].IsInstrument, "TDR Nova should be an effect");
            Check(entries[0].Bits == 32, "the x86 scan should report 32 bits");

            Console.WriteLine($"  x86 bridge scanned 32-bit VST2: {entries[0].Name} ({entries[0].Category})");
        }
        else
        {
            Console.WriteLine("  x86 VST2 probe skipped (no win-x86 bridge staged)");
        }
    }

    static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}