using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TuneLab.Foundation;
using TuneLab.SDK;
using TuneLab.VstInstruments.Bridge;
using TuneLab.VstInstruments.Catalog;

namespace TuneLab.VstInstruments;

// The instrument engine. It publishes one menu entry per instrument plugin and
// creates a per-part session that renders that plugin offline.
//
// The engine never loads plugin code itself. Scanning and rendering both go
// through the helper process; a crash there becomes a failed piece, not a dead
// TuneLab.
public sealed class VstInstrumentEngine : IInstrumentSynthesisEngine, IExtensionSettings
{
    public IReadOnlyOrderedMap<string, InstrumentSourceInfo> InstrumentSourceInfos => mInfos;

    public IReadOnlyList<InstrumentSourceLayoutItem> InstrumentSourceLayout => mLayout;

    public void Init()
    {
        LoadScanPaths();
        mInited = true;

        // Seeded default folders can contain many plugins. Never run the first
        // catalog scan on the host/UI thread: a cold scan of common VST3
        // folders can take tens of seconds and would freeze TuneLab startup.
        StartRescan();
    }

    public void Destroy()
    {
        mInited = false;
        mCatalog = PluginCatalog.Empty;
        mInfos = new OrderedMap<string, InstrumentSourceInfo>();
        mLayout = [];
        mScanned = false;

        // Sessions unload their own slots; this is the final guarantee that no
        // helper outlives the extension.
        lock (mSessionLock)
            mSessions.Clear();

        VstInstrumentBridgeClient.ShutdownAll();
    }

    public IInstrumentSynthesisSession CreateSession(IInstrumentSynthesisContext context)
    {
        if (!mCatalog.ById.TryGetValue(context.InstrumentId, out var entry))
        {
            Logger.Error("Unknown VST instrument id: " + context.InstrumentId);
            return new SilentSession("The selected instrument is not in the catalog.", context);
        }

        if (!entry.IsAvailable)
        {
            Logger.Error($"Instrument '{entry.DisplayName}' is unavailable: {entry.UnavailableReason}");
            return new SilentSession(
                $"Instrument '{entry.DisplayName}' is unavailable: {entry.UnavailableReason}", context);
        }

        try
        {
            var session = new VstInstrumentSession(context, entry, mSettings.Clone(),
                ParameterAutomationStore.GetSelectedParameters(entry.Id),
                () => RemoveSession(context.PartId));

            lock (mSessionLock)
                mSessions[context.PartId] = session;

            return session;
        }
        catch (Exception ex)
        {
            Logger.Error($"Cannot create the session for '{entry.DisplayName}': {ex}");
            return new SilentSession("Cannot create the instrument session: " + ex.Message, context);
        }
    }

    public IReadOnlyOrderedMap<PropertyKey, AutomationConfig> GetAutomationConfigs(
        IInstrumentSynthesisPartPropertyContext context)
    {
        var map = new OrderedMap<PropertyKey, AutomationConfig>();

        foreach (var kv in sAutomations)
            map.Add(kv.Key, kv.Value);

        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            "pitchbend", "mod", "vol", "expr", "sustain",
        };

        int colorIndex = 0;

        foreach (var part in context.Parts)
        {
            foreach (var parameter in ParameterAutomationStore.GetSelectedParameters(part.InstrumentId))
            {
                string id = ParameterKey(parameter.Id);

                if (!seen.Add(id))
                    continue;

                if (!double.IsFinite(parameter.Min) || !double.IsFinite(parameter.Max)
                    || parameter.Max <= parameter.Min)
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(parameter.Name) ? id : parameter.Name;
                double defaultValue = Math.Clamp(parameter.DefaultValue, parameter.Min, parameter.Max);
                string color = ParameterColors[colorIndex++ % ParameterColors.Length];

                map.Add((id, name), AutomationConfig.Create(parameter.Min, parameter.Max)
                    .WithDefault(defaultValue)
                    .WithColor(color));
            }
        }

        return map;
    }

    public IReadOnlyOrderedMap<PropertyKey, AutomationConfig> GetSynthesizedParameterConfigs(
        IInstrumentSynthesisPartPropertyContext context) => sEmptyAutomation;

    public ObjectConfig GetPartPropertyConfig(IInstrumentSynthesisPartPropertyContext context)
    {
        var properties = new OrderedMap<PropertyKey, IControllerConfig>();

        foreach (var kv in sPartConfig.Properties)
            properties.Add(kv.Key, kv.Value);

        var part = context.Parts.FirstOrDefault();
        string? partId = part?.PartId;
        string? instrumentId = part?.InstrumentId;

        if (!string.IsNullOrEmpty(instrumentId))
        {
            properties.Add((VstInstrumentSession.StatePropertyKey(instrumentId), L("状态（base64）", "State (base64)")),
                TextBoxConfig.Create().WithMultiline(6));
        }

        if (!string.IsNullOrEmpty(partId))
        {
            properties.Add(("open_editor", L("插件", "Plugin")),
                ButtonConfig.Create(L("打开编辑器", "Open Editor"), click => OpenEditorAsync(partId, click)));

            properties.Add(("reload_plugin", L("插件", "Plugin")),
                ButtonConfig.Create(L("重新加载插件", "Reload Plugin"), click => ReloadPluginAsync(partId, click)));

            properties.Add(("capture_state", L("状态", "State")),
                ButtonConfig.Create(L("捕获状态", "Capture State"),
                    click => CaptureStateAsync(partId, instrumentId!, click)));

            properties.Add(("clear_state", L("状态", "State")),
                ButtonConfig.Create(L("清除状态", "Clear State"),
                    click => ClearStateAsync(partId, instrumentId!, click)));
        }

        if (!string.IsNullOrEmpty(instrumentId))
        {
            properties.Add(("add_automation", L("自动化", "Automation")),
                ButtonConfig.Create(L("添加自动化", "Add Automation"),
                    click => AddAutomationAsync(partId, instrumentId, click)));

            properties.Add(("clear_automation", L("自动化", "Automation")),
                ButtonConfig.Create(L("清除自动化", "Clear Automation"),
                    click => ClearAutomationAsync(partId, instrumentId, click)));
        }

        return ObjectConfig.Create(properties);
    }

    public ObjectConfig GetNotePropertyConfig(IInstrumentSynthesisNotePropertyContext context) => sNoteConfig;

    // ── IExtensionSettings ──

    public ObjectConfig GetSettingsConfig(IExtensionSettingsContext context)
    {
        string rate = mSettings.SampleRate switch
        {
            44100 => "44100",
            96000 => "96000",
            _ => "48000",
        };

        var renderProperties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                ("output_sample_rate", L("渲染采样率", "Render sample rate")),
                ComboBoxConfig.Create()
                    .Append("44100")
                    .Append("48000")
                    .Append("96000")
                    .WithDefault(rate)
            },
            {
                ("block_size", L("渲染块大小", "Render block size")),
                ComboBoxConfig.Create()
                    .Append("128")
                    .Append("256")
                    .Append("512")
                    .Append("1024")
                    .WithDefault(mSettings.BlockSize.ToString(System.Globalization.CultureInfo.InvariantCulture))
            },
            {
                ("pre_roll_ms", L("预滚（毫秒）", "Pre-roll (ms)")),
                SliderConfig.Integer(mSettings.PreRollMs, 0, 500)
                    .WithMinLabel("0")
                    .WithMaxLabel("500")
            },
            {
                ("tail_ms", L("默认尾音（毫秒）", "Default tail (ms)")),
                SliderConfig.Integer(mSettings.TailMs, 0, 30000)
                    .WithMinLabel("0")
                    .WithMaxLabel("30 s")
            },
            {
                ("stereo_mode", L("立体声转单声道", "Stereo to mono")),
                ComboBoxConfig.Create()
                    .Append(StereoModes.MonoSum)
                    .Append(StereoModes.MonoSumMinus3Db)
                    .Append(StereoModes.Left)
                    .Append(StereoModes.Right)
                    .WithDefault(StereoModes.Normalize(mSettings.StereoMode))
            },
        };

        var scanProperties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                ("scan_depth", L("搜索深度", "Search depth")),
                SliderConfig.Integer(mSettings.ScanDepth, 0, 12)
                    .WithMinLabel(L("当前目录", "This folder"))
                    .WithMaxLabel(L("深", "Deep"))
            },
        };

        // Buttons are first-class SDK 1.2 configs. They hold no value, so they
        // are not persisted and are skipped by presets / CLI.
        var actionProperties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                ("scan_now", ""),
                ButtonConfig.Create(L("立即扫描", "Scan now"), ScanNowAsync)
            },
            {
                ("scan_paths", ""),
                ButtonConfig.Create(L("扫描范围...", "Scan range..."), EditScanPathsAsync)
            },
        };

        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            { (SettingGroupRender, L("渲染", "Rendering")), ObjectConfig.Create(renderProperties) },
            { (SettingGroupScan, L("目录", "Catalog")), ObjectConfig.Create(scanProperties) },
            { (SettingGroupActions, L("操作", "Actions")), ObjectConfig.Create(actionProperties) },
        };

        return ObjectConfig.Create(properties);
    }

    public void ApplySettings(PropertyObject settings)
    {
        var previous = mSettings.Clone();

        // Scan folders now live in the extension's own file, edited by the
        // "Scan range..." button. Migrate old host-side path lists once.
        if (!mScanPathsLoaded)
        {
            var oldPaths = settings.GetObject(SettingGroupPaths);
            var clap = ReadPaths(oldPaths, "clap_paths");
            var vst2 = ReadPaths(oldPaths, "vst2_paths");
            var vst3 = ReadPaths(oldPaths, "vst3_paths");
            var lv2 = ReadPaths(oldPaths, "lv2_paths");
            var au = ReadPaths(oldPaths, "au_paths");

            if (clap.Count > 0 || vst2.Count > 0 || vst3.Count > 0 || lv2.Count > 0 || au.Count > 0)
            {
                // Preserve a migrated format; formats absent from the old host
                // settings keep the seeded defaults.
                if (clap.Count > 0) mSettings.ClapPaths = clap;
                if (vst2.Count > 0) mSettings.Vst2Paths = vst2;
                if (vst3.Count > 0) mSettings.Vst3Paths = vst3;
                if (lv2.Count > 0) mSettings.Lv2Paths = lv2;
                if (au.Count > 0) mSettings.AuPaths = au;
            }
            else if (mSettings.ClapPaths.Count == 0 && mSettings.Vst2Paths.Count == 0
                && mSettings.Vst3Paths.Count == 0 && mSettings.Lv2Paths.Count == 0
                && mSettings.AuPaths.Count == 0)
            {
                DefaultScanPaths.Apply(mSettings);
            }

            ScanPathStore.Save(mSettings);
            mScanPathsLoaded = true;
        }

        var render = settings.GetObject(SettingGroupRender);
        mSettings.SampleRate = VstInstrumentSettings.ParseSampleRate(
            render.GetString("output_sample_rate", "48000"), 48000);
        mSettings.BlockSize = VstInstrumentSettings.ParseBlockSize(
            render.GetString("block_size", "512"), 512);
        mSettings.PreRollMs = (int)Math.Clamp(render.GetDouble("pre_roll_ms", 50), 0, 500);
        mSettings.TailMs = (int)Math.Clamp(render.GetDouble("tail_ms", 2000), 0, 30000);
        mSettings.StereoMode = StereoModes.Normalize(render.GetString("stereo_mode", StereoModes.MonoSum));

        var scan = settings.GetObject(SettingGroupScan);
        mSettings.ScanDepth = (int)Math.Clamp(scan.GetDouble("scan_depth", 6), 0, 12);

        if (mSettings.SameAs(previous))
            return;

        mScanned = false;
        mForceScan = true;

        if (mInited)
            StartRescan();
    }

    // ── Catalog ──

    void EnsureScanned()
    {
        if (mScanned)
            return;

        lock (mScanLock)
        {
            if (mScanned)
                return;

            var catalog = PluginCatalogLoader.LoadOrScan(mSettings, Logger, mForceScan);

            if (!mInited)
                return;

            var infos = new OrderedMap<string, InstrumentSourceInfo>();

            foreach (var entry in catalog.ById.Values)
            {
                if (!entry.IsInstrument || !entry.IsAvailable)
                    continue;

                infos.Add(entry.Id, new InstrumentSourceInfo
                {
                    Name = entry.DisplayName,
                    Description = BuildDescription(entry),
                });
            }

            mCatalog = catalog;
            mInfos = infos;
            mLayout = catalog.Layout;
            mScanned = true;
            mForceScan = false;

            Logger.Info($"VST instrument catalog: {infos.Count} instrument(s)");
        }
    }

    void StartRescan()
    {
        _ = Task.Run(() =>
        {
            try
            {
                EnsureScanned();
            }
            catch (Exception ex)
            {
                Logger.Error("VST instrument rescan failed: " + ex);
            }
        });
    }

    // ── Part-panel buttons ──

    VstInstrumentSession? FindSession(string? partId)
    {
        if (string.IsNullOrEmpty(partId))
            return null;

        lock (mSessionLock)
            return mSessions.TryGetValue(partId, out var session) ? session : null;
    }

    void RemoveSession(string? partId)
    {
        if (string.IsNullOrEmpty(partId))
            return;

        lock (mSessionLock)
            mSessions.Remove(partId);
    }

    async Task OpenEditorAsync(string partId, IButtonClickContext click)
    {
        var session = FindSession(partId);

        if (session == null)
        {
            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                L("该 part 的会话尚未创建。", "The part session is not active yet."));
            return;
        }

        var result = await session.OpenEditorAsync();

        if (!result.Ok)
        {
            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                result.Reason ?? L("插件编辑器无法打开。", "The plugin editor could not be opened."));
        }
    }

    async Task ReloadPluginAsync(string partId, IButtonClickContext click)
    {
        var session = FindSession(partId);

        if (session == null)
        {
            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                L("该 part 的会话尚未创建。", "The part session is not active yet."));
            return;
        }

        session.RequestReload();

        await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
            L("插件将在下一次渲染时重新加载。", "The plugin will reload on the next render."));
    }

    async Task CaptureStateAsync(string partId, string instrumentId, IButtonClickContext click)
    {
        var session = FindSession(partId);

        if (session == null)
        {
            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                L("该 part 的会话尚未创建。", "The part session is not active yet."));
            return;
        }

        try
        {
            string? base64 = await session.CaptureStateAsync();

            if (string.IsNullOrEmpty(base64))
            {
                await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                    L("该插件没有可读取的状态。", "The plugin reported no state."));
                return;
            }

            try
            {
                InstrumentStateStore.Save(instrumentId, Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
            }

            bool written = await session.WriteStateToPartAsync(base64);

            if (!written)
            {
                var properties = new OrderedMap<PropertyKey, IControllerConfig>
                {
                    {
                        ("captured_state", L("状态（base64）", "State (base64)")),
                        TextBoxConfig.Create(base64).WithMultiline(12)
                    },
                };

                await click.DialogService.PromptInputs(L("插件状态", "Plugin state"),
                    ObjectConfig.Create(properties));
            }

            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                written
                    ? L("状态已写入 part 的 vsti_state 字段，并随工程保存。",
                        "State was written into the part's vsti_state field and will be saved with the project.")
                    : L("状态已按乐器 ID 自动保存；Host 数据层不可写，请把上面的 base64 复制到 vsti_state 字段。",
                        "State saved by instrument ID; the host data layer is not writable, so copy the base64 above into the vsti_state field."));
        }
        catch (Exception ex)
        {
            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                L("读取插件状态失败：", "Reading plugin state failed: ") + ex.Message);
        }
    }

    async Task ClearStateAsync(string? partId, string instrumentId, IButtonClickContext click)
    {
        InstrumentStateStore.Clear(instrumentId);

        var session = FindSession(partId);

        if (session != null)
        {
            await session.ClearStateInPartAsync();
            session.RequestReload();
        }

        await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
            L("已删除该乐器的自动状态缓存，并清除 part 的 vsti_state 字段。",
                "The instrument's automatic state cache and the part's vsti_state field were removed."));
    }

    async Task AddAutomationAsync(string? partId, string instrumentId, IButtonClickContext click)
    {
        if (!mCatalog.ById.TryGetValue(instrumentId, out var entry))
        {
            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                L("找不到该乐器的目录项。", "The instrument is not in the catalog."));
            return;
        }

        int selectedCount = 0;

        try
        {
            IReadOnlyList<ParameterInfo> parameters = [];

            await click.DialogService.RunWithProgress(L("添加自动化", "Add automation"),
                async (progress, token) =>
                {
                    progress.Report(null, L("正在读取插件参数...", "Reading plugin parameters..."));
                    parameters = await Task.Run(() => LoadParameters(entry, progress, token), token);
                });

            var linear = parameters.Where(ParameterAutomationStore.IsLinear).ToList();

            if (linear.Count == 0)
            {
                await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                    L("没有可添加的线性参数。", "No linear parameters to add."));
                return;
            }

            var existing = ParameterAutomationStore.GetSelectedParameters(entry.Id);
            var selectedIds = existing.Select(p => p.Id).ToHashSet();
            bool firstSelection = selectedIds.Count == 0;

            var result = await click.DialogService.PromptInputs(
                L("选择自动化参数", "Select automation parameters"),
                values => BuildParameterSelectionConfig(linear, selectedIds, firstSelection, values));

            if (result == null)
                return;

            var selected = new List<long>();

            foreach (var parameter in linear)
            {
                bool defaultValue = firstSelection || selectedIds.Contains(parameter.Id);

                if (result.GetBoolean(ParameterSelectionKey(parameter.Id), defaultValue))
                    selected.Add(parameter.Id);
            }

            ParameterAutomationStore.SetParameters(entry.Id, parameters, selected);
            selectedCount = selected.Count;

            var session = FindSession(partId);
            session?.UpdateAutomatedParameters(ParameterAutomationStore.GetSelectedParameters(instrumentId));
            session?.NotifyDeclarationsChanged();

            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                string.Format(L("已选择 {0} 个参数作为自动化轨。", "Selected {0} parameter lane(s)."), selectedCount));
        }
        catch (Exception ex)
        {
            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                L("添加自动化失败：", "Adding automation failed: ") + ex.Message);
        }
    }

    async Task ClearAutomationAsync(string? partId, string instrumentId, IButtonClickContext click)
    {
        var parameters = ParameterAutomationStore.GetParameters(instrumentId);

        if (parameters.Count > 0)
            ParameterAutomationStore.SetParameters(instrumentId, parameters, []);
        else
            ParameterAutomationStore.Clear(instrumentId);

        var session = FindSession(partId);
        session?.UpdateAutomatedParameters(ParameterAutomationStore.GetSelectedParameters(instrumentId));
        session?.NotifyDeclarationsChanged();

        await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
            L("已清除该乐器的参数自动化。", "The instrument's parameter automation was cleared."));
    }

    IReadOnlyList<ParameterInfo> LoadParameters(CatalogEntry entry, IProgressReporter progress, CancellationToken token)
    {
        var parameters = ParameterAutomationStore.GetParameters(entry.Id);

        if (parameters.Count == 0)
        {
            progress.Report(null, L("正在加载插件...", "Loading the plugin..."));

            var client = VstInstrumentBridgeClient.ForPlugin(entry.Bits, entry.Rid);

            if (!client.EnsureStartedBlocking())
                throw new InvalidOperationException(client.LastBridgeError ?? "the bridge could not start");

            int slot = client.AllocateSlot();

            try
            {
                var load = client.LoadBlocking(slot, entry.Format, entry.PluginId, entry.Locator,
                    mSettings.SampleRate, mSettings.BlockSize);

                if (load == null)
                    throw new InvalidOperationException(client.LastBridgeError ?? "loading the plugin did not answer");

                if (!load.Ok)
                    throw new InvalidOperationException(load.Reason ?? "the plugin refused to load");

                var fetched = client.GetParametersBlocking(slot);

                if (fetched == null || fetched.Count == 0)
                    throw new InvalidOperationException("the plugin reported no parameters");

                parameters = fetched;

                // Cache metadata even if the user cancels the selection dialog.
                ParameterAutomationStore.SetParameters(entry.Id, parameters, []);
            }
            finally
            {
                client.UnloadBlocking(slot);
            }
        }

        token.ThrowIfCancellationRequested();
        progress.Report(1, L("完成", "Done"));
        return parameters;
    }

    internal static ObjectConfig BuildParameterSelectionConfig(IReadOnlyList<ParameterInfo> linear,
        HashSet<long> selected, bool firstSelection, PropertyObject values)
    {
        string filter = values.GetString("filter", string.Empty).Trim();
        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                ("filter", L("过滤（名称或 ID）", "Filter (name or id)")),
                TextBoxConfig.Create(filter)
            },
        };

        foreach (var parameter in linear)
        {
            if (filter.Length > 0
                && !parameter.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !parameter.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    .Contains(filter, StringComparison.Ordinal))
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(parameter.Name)
                ? ParameterKey(parameter.Id)
                : parameter.Name;

            string label = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}   [{1:0.###} … {2:0.###}]", name, parameter.Min, parameter.Max);

            bool defaultValue = values.GetBoolean(ParameterSelectionKey(parameter.Id),
                firstSelection || selected.Contains(parameter.Id));

            properties.Add((ParameterSelectionKey(parameter.Id), label),
                CheckBoxConfig.Create(defaultValue));
        }

        return ObjectConfig.Create(properties);
    }

    static string ParameterSelectionKey(long id)
        => "sel_" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static string ParameterKey(long id)
        => "param:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    void LoadScanPaths()
    {
        if (!ScanPathStore.TryLoad(out var file))
        {
            // Fresh install: seed the usual per-format folders as ordinary
            // editable rows. They are saved immediately; the host-side
            // migration in ApplySettings can still replace individual lists.
            DefaultScanPaths.Apply(mSettings);
            ScanPathStore.Save(mSettings);
            return;
        }

        mSettings.ClapPaths = [.. file.Clap];
        mSettings.Vst2Paths = [.. file.Vst2];
        mSettings.Vst3Paths = [.. file.Vst3];
        mSettings.Lv2Paths = [.. file.Lv2];
        mSettings.AuPaths = [.. file.Au];

        // Files from builds before default seeding have no flag. Seed only
        // when every list is empty; once the flag is saved, an empty list is
        // the user's explicit choice and must stay empty.
        if (!file.DefaultsInitialized)
        {
            if (mSettings.ClapPaths.Count == 0 && mSettings.Vst2Paths.Count == 0
                && mSettings.Vst3Paths.Count == 0 && mSettings.Lv2Paths.Count == 0
                && mSettings.AuPaths.Count == 0)
            {
                DefaultScanPaths.Apply(mSettings);
            }

            ScanPathStore.Save(mSettings);
        }

        mScanPathsLoaded = true;
    }

    // ── Settings buttons ──

    async Task ScanNowAsync(IButtonClickContext click)
    {
        try
        {
            int count = 0;

            await click.DialogService.RunWithProgress(L("扫描插件", "Scanning plugins"),
                async (progress, token) =>
                {
                    progress.Report(null, L("正在扫描插件目录...", "Scanning plugin folders..."));

                    count = await Task.Run(() =>
                    {
                        mForceScan = true;
                        mScanned = false;
                        EnsureScanned();
                        return mInfos.Count;
                    }, token);

                    progress.Report(1, L("完成", "Done"));
                });

            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                string.Format(L("扫描完成：{0} 个乐器。", "Scan complete: {0} instrument(s)."), count));
        }
        catch (Exception ex)
        {
            await click.DialogService.ShowMessage(L("VST 乐器", "VST Instruments"),
                L("扫描失败：", "Scan failed: ") + ex.Message);
        }
    }

    async Task EditScanPathsAsync(IButtonClickContext click)
    {
        var result = await click.DialogService.PromptInputs(L("扫描范围", "Scan range"), values => BuildScanPathConfig(values));

        if (result == null)
            return;

        ApplyScanPaths(result);

        await click.DialogService.ShowMessage(L("扫描范围", "Scan range"),
            L("扫描路径已更新。点击“立即扫描”刷新目录。",
                "Scan folders updated. Use “Scan now” to refresh the catalog."));
    }

    ObjectConfig BuildScanPathConfig(PropertyObject values)
    {
        // PromptInputs re-declares the form after every edit. Build each list
        // from the form's current sparse values; only fall back to the saved
        // settings when that format's list has not been touched yet. Without
        // this, a folder added with + would be replaced by the stale saved
        // list on the next form reconciliation.
        var clap = values.Map.ContainsKey("clap_paths")
            ? ReadPaths(values, "clap_paths", includeEmpty: true)
            : [.. mSettings.ClapPaths];
        var vst2 = values.Map.ContainsKey("vst2_paths")
            ? ReadPaths(values, "vst2_paths", includeEmpty: true)
            : [.. mSettings.Vst2Paths];
        var vst3 = values.Map.ContainsKey("vst3_paths")
            ? ReadPaths(values, "vst3_paths", includeEmpty: true)
            : [.. mSettings.Vst3Paths];
        var lv2 = values.Map.ContainsKey("lv2_paths")
            ? ReadPaths(values, "lv2_paths", includeEmpty: true)
            : [.. mSettings.Lv2Paths];
        var au = values.Map.ContainsKey("au_paths")
            ? ReadPaths(values, "au_paths", includeEmpty: true)
            : [.. mSettings.AuPaths];

        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                ("clap_paths", L("CLAP 路径", "CLAP folders")),
                BuildPathList(clap, L("添加 CLAP 目录", "Add a CLAP folder"))
            },
            {
                ("vst2_paths", L("VST2 路径", "VST2 folders")),
                BuildPathList(vst2, L("添加 VST2 目录", "Add a VST2 folder"))
            },
            {
                ("vst3_paths", L("VST3 路径", "VST3 folders")),
                BuildPathList(vst3, L("添加 VST3 目录", "Add a VST3 folder"))
            },
            {
                ("lv2_paths", L("LV2 路径", "LV2 folders")),
                BuildPathList(lv2, L("添加 LV2 目录", "Add an LV2 folder"))
            },
            {
                ("au_paths", L("AU 路径", "AU folders")),
                BuildPathList(au, L("添加 AU 目录", "Add an AU folder"))
            },
        };

        return ObjectConfig.Create(properties);
    }

    void ApplyScanPaths(PropertyObject result)
    {
        mSettings.ClapPaths = ReadPaths(result, "clap_paths");
        mSettings.Vst2Paths = ReadPaths(result, "vst2_paths");
        mSettings.Vst3Paths = ReadPaths(result, "vst3_paths");
        mSettings.Lv2Paths = ReadPaths(result, "lv2_paths");
        mSettings.AuPaths = ReadPaths(result, "au_paths");

        ScanPathStore.Save(mSettings);
        mScanPathsLoaded = true;
        mScanned = false;
        mForceScan = true;
    }

    static ListConfig BuildPathList(IEnumerable<string> paths, string title)
    {
        var elements = new List<IControllerConfig>();

        foreach (string path in paths)
        {
            // Keep blank rows while the form is open. The list controller adds
            // an empty value for +; filtering it here would make + look dead.
            elements.Add(PathPickerConfig.CreateFolder(path).WithPickerTitle(title));
        }

        return ListConfig.Create(elements,
        [
            new AddableElement(PathPickerConfig.CreateFolder().WithPickerTitle(title),
                L("添加目录", "Add folder")),
        ]);
    }

    static string BuildDescription(CatalogEntry entry)
    {
        string vendor = string.IsNullOrWhiteSpace(entry.Vendor) ? "Unknown vendor" : entry.Vendor;
        return $"{entry.FormatDisplay} · {vendor} · {entry.Locator}";
    }

    static List<string> ReadPaths(PropertyObject settings, string key, bool includeEmpty = false)
    {
        var result = new List<string>();
        var array = settings.GetValue(key, PropertyArray.Empty);

        foreach (var value in array)
        {
            if (value.ToString(out var path) && (includeEmpty || !string.IsNullOrWhiteSpace(path)))
                result.Add(path);
        }

        return result;
    }

    // ── Declarations ──

    static ObjectConfig BuildPartConfig()
    {
        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                ("gain_db", "Gain (dB)"),
                SliderConfig.Linear(0, -24, 12).WithMinLabel("-24 dB").WithMaxLabel("+12 dB")
            },
            {
                ("transpose", "Transpose (semitones)"),
                SliderConfig.Integer(0, -24, 24).WithMinLabel("-24").WithMaxLabel("+24")
            },
            {
                ("midi_channel", "MIDI channel"),
                SliderConfig.Integer(1, 1, 16)
            },
            {
                ("stereo_mode", "Stereo to mono"),
                ComboBoxConfig.Create()
                    .Append(StereoModes.MonoSum)
                    .Append(StereoModes.MonoSumMinus3Db)
                    .Append(StereoModes.Left)
                    .Append(StereoModes.Right)
                    .WithDefault(StereoModes.MonoSum)
            },
            {
                ("tail_ms", "Tail (ms)"),
                SliderConfig.Integer(2000, 0, 30000).WithMinLabel("0").WithMaxLabel("30 s")
            },
            {
                ("pre_roll_ms", "Pre-roll (ms)"),
                SliderConfig.Integer(50, 0, 500).WithMinLabel("0").WithMaxLabel("500")
            },
            {
                ("sample_rate", "Sample rate"),
                ComboBoxConfig.Create()
                    .Append("44100")
                    .Append("48000")
                    .Append("96000")
                    .WithDefault("48000")
            },
            {
                ("polyphony", "Polyphony (advisory)"),
                SliderConfig.Integer(64, 8, 256)
            },
        };

        return ObjectConfig.Create(properties);
    }

    static ObjectConfig BuildNoteConfig()
    {
        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                ("velocity", "Velocity"),
                SliderConfig.Integer(100, 1, 127)
            },
            {
                ("detune", "Detune (cents)"),
                SliderConfig.Linear(0, -100, 100).WithMinLabel("-100").WithMaxLabel("+100")
            },
        };

        return ObjectConfig.Create(properties);
    }

    static OrderedMap<PropertyKey, AutomationConfig> BuildAutomationConfigs()
    {
        var map = new OrderedMap<PropertyKey, AutomationConfig>();

        map.Add(("pitchbend", "Pitch bend (semitone)"),
            AutomationConfig.Create(-24, 24).WithDefault(0).WithColor("#e06c75"));
        map.Add(("mod", "Modulation"),
            AutomationConfig.Create(0, 127).WithDefault(0).WithColor("#61afef"));
        map.Add(("vol", "Volume"),
            AutomationConfig.Create(0, 127).WithDefault(100).WithColor("#98c379"));
        map.Add(("expr", "Expression"),
            AutomationConfig.Create(0, 127).WithDefault(127).WithColor("#e5c07b"));
        map.Add(("sustain", "Sustain"),
            AutomationConfig.Create(0, 127).WithDefault(0).WithColor("#c678dd"));

        return map;
    }

    static ILogger Logger => TuneLabContext.Global.GetLogger();

    static string L(string zh, string en)
    {
        // A few historical literals were checked in with replacement characters.
        // Show the English text instead of a garbled Chinese label.
        if (zh.Contains('\uFFFD'))
            return en;

        return TuneLabContext.Global.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
    }

    const string SettingGroupPaths = "group_paths";
    const string SettingGroupRender = "group_render";
    const string SettingGroupScan = "group_scan";
    const string SettingGroupActions = "group_actions";

    static readonly OrderedMap<PropertyKey, AutomationConfig> sEmptyAutomation = new();
    static readonly ObjectConfig sPartConfig = BuildPartConfig();
    static readonly ObjectConfig sNoteConfig = BuildNoteConfig();
    static readonly OrderedMap<PropertyKey, AutomationConfig> sAutomations = BuildAutomationConfigs();

    static readonly string[] ParameterColors =
    [
        "#e06c75", "#61afef", "#98c379", "#e5c07b", "#c678dd",
        "#56b6c2", "#d19a66", "#7f9f7f", "#c586c0", "#4ec9b0",
    ];

    readonly VstInstrumentSettings mSettings = new();
    readonly object mScanLock = new();
    readonly object mSessionLock = new();
    readonly Dictionary<string, VstInstrumentSession> mSessions = new(StringComparer.Ordinal);

    volatile PluginCatalog mCatalog = PluginCatalog.Empty;
    volatile IReadOnlyOrderedMap<string, InstrumentSourceInfo> mInfos = new OrderedMap<string, InstrumentSourceInfo>();
    volatile IReadOnlyList<InstrumentSourceLayoutItem> mLayout = [];
    bool mInited;
    bool mScanned;
    bool mForceScan;
    bool mScanPathsLoaded;
}

internal sealed class SilentSession : IInstrumentSynthesisSession
{
    public SilentSession(string? message = null, IInstrumentSynthesisContext? context = null)
    {
        mMessage = message;

        // Status is queried from the UI thread; the live context must never be
        // touched there. Capture the note range once on the data thread.
        if (context != null && !string.IsNullOrEmpty(message))
        {
            foreach (var note in context.Notes)
            {
                mStartTime = Math.Min(mStartTime, note.StartTime.Value);
                mEndTime = Math.Max(mEndTime, note.EndTime.Value);
            }
        }
    }

    public SynthesisRange? GetNextPendingSynthesisRange(double startTime, double endTime) => null;

    public Task SynthesizeNext(double startTime, double endTime, CancellationToken cancellation = default)
        => Task.CompletedTask;

    public IReadOnlyMap<string, SynthesizedParameter> SynthesizedParameters => Map<string, SynthesizedParameter>.Empty;

    public IReadOnlyList<SynthesisStatusSegment> Status
    {
        get
        {
            if (string.IsNullOrEmpty(mMessage) || mStartTime > mEndTime)
                return [];

            double start = mStartTime;
            double end = mEndTime;

            // Surface "plugin missing / unavailable" as a failed status band
            // instead of leaving the part silently empty.
            return
            [
                new SynthesisStatusSegment
                {
                    StartTime = start,
                    EndTime = end,
                    Status = SynthesisSegmentStatus.Failed,
                    Message = mMessage,
                },
            ];
        }
    }

    public IActionEvent SynthesizedParametersChanged => ActionEvent.Empty;

    public IActionEvent StatusChanged => ActionEvent.Empty;

    public void Dispose() { }

    readonly string? mMessage;
    double mStartTime = double.MaxValue;
    double mEndTime = double.MinValue;
}