using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// Minimal CLAP C ABI. Only what an offline instrument host needs:
// entry/factory/plugin, audio ports, note ports, latency, tail, params, state,
// note/MIDI events and the host thread-check extension.
//
// Layouts follow include/clap/*.h. Function pointers use the platform C calling
// convention. Every struct is unmanaged and accessed through pointers; nothing
// is marshalled field by field on the process path.
//
// CLAP is a 64-bit target here. 32-bit CLAP plugins are rare enough that the
// bridge rejects them instead of maintaining two ABI layouts; VST2 still works
// in the x86 helper.
internal static unsafe class ClapNative
{
    public const uint SupportedMajor = 1;
    public const uint HostMajor = 1;
    public const uint HostMinor = 2;
    public const uint HostRevision = 2;

    // clap/plugin.h
    public const string PluginFactoryId = "clap.plugin-factory";

    // Extension ids.
    public const string AudioPortsExtension = "clap.audio-ports";
    public const string NotePortsExtension = "clap.note-ports";
    public const string LatencyExtension = "clap.latency";
    public const string TailExtension = "clap.tail";
    public const string ParamsExtension = "clap.params";
    public const string StateExtension = "clap.state";
    public const string ThreadCheckExtension = "clap.host-thread-check";
    public const string GuiExtension = "clap.gui";
    public const string HostGuiExtension = "clap.host-gui";

    // "win32" is handed to the plugin as clap_window_t.api and must remain
    // valid for the whole GUI session.
    public static readonly IntPtr Win32Api = AllocUtf8("win32");

    static IntPtr AllocUtf8(string text)
    {
        byte[] bytes = Utf8(text);
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    // clap/event.h. Events the host sends live in the plugin event space.
    public const ushort EventSpacePlugin = 0;

    public const ushort EventTypeNoteOn = 0;
    public const ushort EventTypeNoteOff = 1;
    public const ushort EventTypeNoteChoke = 2;
    public const ushort EventTypeNoteEnd = 3;
    public const ushort EventTypeParamValue = 5;
    public const ushort EventTypeMidi = 8;

    // clap/note-ports.h
    public const uint NoteDialectClap = 1;
    public const uint NoteDialectMidi = 2;
    public const uint NoteDialectMidiMpe = 4;
    public const uint NoteDialectMidi2 = 8;

    // clap/audio-ports.h
    public const uint AudioPortIsMain = 1 << 0;

    // clap/params.h
    public const uint ParamIsStepped = 1 << 0;
    public const uint ParamIsReadonly = 1 << 3;
    public const uint ParamIsAutomatable = 1 << 5;

    // clap/process.h
    public const int ProcessContinue = 0;
    public const int ProcessContinueIfNotQuiet = 1;
    public const int ProcessTail = 2;
    public const int ProcessSleep = 3;
    public const int ProcessError = -1;

    // Upper bound for one block. 64 bytes is enough for every event we write
    // (note = 40, param value = 56, MIDI = 24).
    public const int MaxEventsPerBlock = 512;
    public const int MaxEventBytes = 64;

    static ClapNative()
    {
        // A wrong layout is silent memory corruption, not a compile error.
        // Only the 64-bit layout is supported (see the file header).
        if (IntPtr.Size != 8)
            return;

        Check(sizeof(ClapVersion) == 12, "clap_version_t");
        Check(sizeof(ClapPluginEntry) == 40, "clap_plugin_entry_t");
        Check(sizeof(ClapPluginDescriptor) == 88, "clap_plugin_descriptor_t");
        Check(sizeof(ClapHost) == 88, "clap_host_t");
        Check(sizeof(ClapPluginFactory) == 24, "clap_plugin_factory_t");
        Check(sizeof(ClapPlugin) == 96, "clap_plugin_t");
        Check(sizeof(ClapProcess) == 64, "clap_process_t");
        Check(sizeof(ClapAudioBuffer) == 32, "clap_audio_buffer_t");
        Check(sizeof(ClapInputEvents) == 24, "clap_input_events_t");
        Check(sizeof(ClapOutputEvents) == 16, "clap_output_events_t");
        Check(sizeof(ClapEventHeader) == 16, "clap_event_header_t");
        Check(sizeof(ClapEventNote) == 40, "clap_event_note_t");
        Check(sizeof(ClapEventMidi) == 24, "clap_event_midi_t");
        Check(sizeof(ClapEventParamValue) == 56, "clap_event_param_value_t");
        Check(sizeof(ClapNotePortInfo) == 268, "clap_note_port_info_t");
        Check(sizeof(ClapAudioPortInfo) == 288, "clap_audio_port_info_t");
        Check(sizeof(ClapParamInfo) == 1320, "clap_param_info_t");
        Check(sizeof(ClapOStream) == 16, "clap_ostream_t");
        Check(sizeof(ClapIStream) == 16, "clap_istream_t");
        Check(sizeof(ClapPluginLatency) == 8, "clap_plugin_latency_t");
        Check(sizeof(ClapPluginTail) == 8, "clap_plugin_tail_t");
        Check(sizeof(ClapPluginParams) == 48, "clap_plugin_params_t");
        Check(sizeof(ClapPluginState) == 16, "clap_plugin_state_t");
        Check(sizeof(ClapPluginAudioPorts) == 16, "clap_plugin_audio_ports_t");
        Check(sizeof(ClapPluginNotePorts) == 16, "clap_plugin_note_ports_t");
        Check(sizeof(ClapHostThreadCheck) == 16, "clap_host_thread_check_t");
        Check(sizeof(ClapWindow) == 16, "clap_window_t");
        Check(sizeof(ClapPluginGui) == 96, "clap_plugin_gui_t");
        Check(sizeof(ClapHostGui) == 40, "clap_host_gui_t");
    }

    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new TypeLoadException("CLAP ABI layout mismatch: " + name);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapVersion
    {
        public uint Major;
        public uint Minor;
        public uint Revision;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginEntry
    {
        public ClapVersion Version;
        public delegate* unmanaged[Cdecl]<byte*, byte> Init;
        public delegate* unmanaged[Cdecl]<void> Deinit;
        public delegate* unmanaged[Cdecl]<byte*, IntPtr> GetFactory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginDescriptor
    {
        public ClapVersion Version;
        public byte* Id;
        public byte* Name;
        public byte* Vendor;
        public byte* Url;
        public byte* ManualUrl;
        public byte* SupportUrl;
        public byte* VersionString;
        public byte* Description;
        public byte** Features; // const char* const*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapHost
    {
        public ClapVersion Version;
        public IntPtr HostData;
        public byte* Name;
        public byte* Vendor;
        public byte* Url;
        public byte* VersionString;
        public delegate* unmanaged[Cdecl]<ClapHost*, byte*, IntPtr> GetExtension;
        public delegate* unmanaged[Cdecl]<ClapHost*, void> RequestRestart;
        public delegate* unmanaged[Cdecl]<ClapHost*, void> RequestProcess;
        public delegate* unmanaged[Cdecl]<ClapHost*, void> RequestCallback;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginFactory
    {
        public delegate* unmanaged[Cdecl]<ClapPluginFactory*, uint> GetPluginCount;
        public delegate* unmanaged[Cdecl]<ClapPluginFactory*, uint, ClapPluginDescriptor*> GetPluginDescriptor;
        public delegate* unmanaged[Cdecl]<ClapPluginFactory*, ClapHost*, byte*, ClapPlugin*> CreatePlugin;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPlugin
    {
        public ClapPluginDescriptor* Descriptor;
        public IntPtr PluginData;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> Init;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Destroy;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, double, uint, uint, byte> Activate;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Deactivate;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> StartProcessing;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> StopProcessing;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Reset;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapProcess*, int> Process;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, IntPtr> GetExtension;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> OnMainThread;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapProcess
    {
        public long SteadyTime;
        public uint FramesCount;
        public IntPtr Transport; // clap_event_transport_t*; null is allowed
        public ClapAudioBuffer* AudioInputs;
        public ClapAudioBuffer* AudioOutputs;
        public uint AudioInputsCount;
        public uint AudioOutputsCount;
        public ClapInputEvents* InEvents;
        public ClapOutputEvents* OutEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapAudioBuffer
    {
        public float** Data32;
        public double** Data64;
        public uint ChannelCount;
        public uint Latency;
        public ulong ConstantMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapInputEvents
    {
        public IntPtr Ctx;
        public delegate* unmanaged[Cdecl]<ClapInputEvents*, uint> Size;
        public delegate* unmanaged[Cdecl]<ClapInputEvents*, uint, ClapEventHeader*> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapOutputEvents
    {
        public IntPtr Ctx;
        public delegate* unmanaged[Cdecl]<ClapOutputEvents*, ClapEventHeader*, byte> TryPush;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapEventHeader
    {
        public uint Size;
        public uint Time;
        public ushort SpaceId;
        public ushort Type;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapEventNote
    {
        public ClapEventHeader Header;
        public int NoteId;
        public short PortIndex;
        public short Channel;
        public short Key;
        public double Velocity;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapEventMidi
    {
        public ClapEventHeader Header;
        public ushort PortIndex;
        public byte Data0;
        public byte Data1;
        public byte Data2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapEventParamValue
    {
        public ClapEventHeader Header;
        public uint ParamId;
        public IntPtr Cookie;
        public int NoteId;
        public short PortIndex;
        public short Channel;
        public short Key;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapNotePortInfo
    {
        public uint Id;
        public uint SupportedDialects;
        public uint PreferredDialect;
        public fixed byte Name[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginNotePorts
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte, uint> Count;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, byte, ClapNotePortInfo*, byte> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapAudioPortInfo
    {
        public uint Id;
        public fixed byte Name[256];
        public uint Flags;
        public uint ChannelCount;
        public byte* PortType;
        public uint InPlacePair;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginAudioPorts
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte, uint> Count;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, byte, ClapAudioPortInfo*, byte> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginLatency
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginTail
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapParamInfo
    {
        public uint Id;
        public uint Flags;
        public IntPtr Cookie;
        public fixed byte Name[256];
        public fixed byte Module[1024];
        public double MinValue;
        public double MaxValue;
        public double DefaultValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginParams
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint> Count;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, ClapParamInfo*, byte> GetInfo;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, double*, byte> GetValue;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, double, byte*, uint, byte> ValueToText;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, double*, byte> TextToValue;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapInputEvents*, ClapOutputEvents*, void> Flush;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapOStream
    {
        public IntPtr Ctx;
        public delegate* unmanaged[Cdecl]<ClapOStream*, void*, ulong, long> Write;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapIStream
    {
        public IntPtr Ctx;
        public delegate* unmanaged[Cdecl]<ClapIStream*, void*, ulong, long> Read;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginState
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapOStream*, byte> Save;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapIStream*, byte> Load;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapHostThreadCheck
    {
        public delegate* unmanaged[Cdecl]<ClapHost*, byte> IsMainThread;
        public delegate* unmanaged[Cdecl]<ClapHost*, byte> IsAudioThread;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapWindow
    {
        public byte* Api;
        public IntPtr Specific;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapPluginGui
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, byte, byte> IsApiSupported;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte**, byte*, byte> GetPreferredApi;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, byte, byte> Create;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Destroy;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, double, byte> SetScale;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint*, uint*, byte> GetSize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, uint, byte> SetSize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapWindow*, byte> SetParent;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapWindow*, byte> SetTransient;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, void> SuggestTitle;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> Show;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> Hide;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapHostGui
    {
        public delegate* unmanaged[Cdecl]<ClapHost*, void> ResizeHintsChanged;
        public delegate* unmanaged[Cdecl]<ClapHost*, uint, uint, byte> RequestResize;
        public delegate* unmanaged[Cdecl]<ClapHost*, byte> RequestShow;
        public delegate* unmanaged[Cdecl]<ClapHost*, byte> RequestHide;
        public delegate* unmanaged[Cdecl]<ClapHost*, byte, void> Closed;
    }

    public static byte[] Utf8(string text)
    {
        int length = System.Text.Encoding.UTF8.GetByteCount(text);
        var bytes = new byte[length + 1];
        System.Text.Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    public static string? StringAt(byte* pointer)
        => pointer == null ? null : Marshal.PtrToStringUTF8((IntPtr)pointer);

    public static string? StringAt(IntPtr pointer)
        => pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);

    // Fixed-size char arrays are not NUL-terminated by every plugin. Never read
    // past the declared size.
    public static string FixedString(byte* pointer, int maxLength)
    {
        if (pointer == null)
            return string.Empty;

        int length = 0;
        while (length < maxLength && pointer[length] != 0)
            length++;

        return length == 0 ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)pointer, length) ?? string.Empty;
    }

    public static List<string> FeaturesAt(byte** features)
    {
        var result = new List<string>();

        if (features == null)
            return result;

        for (int index = 0; index < 256; index++)
        {
            byte* item = features[index];
            if (item == null)
                break;

            string? text = StringAt(item);
            if (!string.IsNullOrEmpty(text))
                result.Add(text);
        }

        return result;
    }

    // The catalog lists instruments only. A plugin that advertises both
    // "instrument" and "audio-effect" is an effect with a sidechain; keep it out.
    public static bool LooksLikeInstrument(IReadOnlyList<string> features)
    {
        bool instrument = false;
        bool effect = false;

        foreach (string feature in features)
        {
            if (string.Equals(feature, "instrument", StringComparison.OrdinalIgnoreCase))
                instrument = true;
            else if (string.Equals(feature, "audio-effect", StringComparison.OrdinalIgnoreCase))
                effect = true;
        }

        return instrument && !effect;
    }
}
