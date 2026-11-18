using System;
using System.Runtime.InteropServices;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// VST2.4 C ABI, written directly from aeffect.h / audioeffect.h.
//
// The ABI has been stable since 2006. There is no VST2 SDK dependency and no
// third-party wrapper: the dispatcher, processReplacing, parameter accessors
// and the audioMaster callback are the only entry points an instrument host
// needs.
//
// Calling convention: VST2 defines VSTCALLBACK as __cdecl on Windows (not
// stdcall). On x64 the distinction disappears; on the x86 helper it matters.
//
// VstIntPtr is intptr_t. Writing it as int truncates half the argument on x64.
internal static unsafe class Vst2Native
{
    public const int Magic = ('V' << 24) | ('s' << 16) | ('t' << 8) | 'P';

    // AEffectOpcodes
    public const int EffOpen = 0;
    public const int EffClose = 1;
    public const int EffSetProgram = 2;
    public const int EffGetProgram = 3;
    public const int EffSetProgramName = 4;
    public const int EffGetProgramName = 5;
    public const int EffGetParamLabel = 6;
    public const int EffGetParamDisplay = 7;
    public const int EffGetParamName = 8;
    public const int EffSetSampleRate = 10;
    public const int EffSetBlockSize = 11;
    public const int EffMainsChanged = 12;
    public const int EffEditGetRect = 13;
    public const int EffEditOpen = 14;
    public const int EffEditClose = 15;
    public const int EffEditIdle = 19;
    public const int EffGetChunk = 23;
    public const int EffSetChunk = 24;
    public const int EffProcessEvents = 25;
    public const int EffCanBeAutomated = 26;
    public const int EffGetProgramNameIndexed = 29;
    public const int EffGetPlugCategory = 35;
    public const int EffGetEffectName = 45;
    public const int EffGetVendorString = 47;
    public const int EffGetProductString = 48;
    public const int EffGetVendorVersion = 49;
    public const int EffCanDo = 51;
    public const int EffGetTailSize = 52;
    public const int EffGetVstVersion = 58;
    public const int EffStartProcess = 71;
    public const int EffStopProcess = 72;

    // AEffectFlags
    public const int EffFlagsHasEditor = 1 << 0;
    public const int EffFlagsCanReplacing = 1 << 4;
    public const int EffFlagsProgramChunks = 1 << 5;
    public const int EffFlagsIsSynth = 1 << 8;
    public const int EffFlagsNoSoundInStop = 1 << 9;
    public const int EffFlagsCanDoubleReplacing = 1 << 12;

    // AudioMasterOpcodes
    public const int AudioMasterVersion = 1;
    public const int AudioMasterWantMidi = 6;
    public const int AudioMasterGetTime = 7;
    public const int AudioMasterGetSampleRate = 17;
    public const int AudioMasterGetBlockSize = 18;
    public const int AudioMasterGetCurrentProcessLevel = 24;
    public const int AudioMasterGetVendorString = 32;
    public const int AudioMasterGetProductString = 33;
    public const int AudioMasterGetVendorVersion = 34;
    public const int AudioMasterCanDo = 37;
    public const int AudioMasterGetLanguage = 38;
    public const int AudioMasterUpdateDisplay = 42;
    public const int AudioMasterOpenFileSelector = 45;
    public const int AudioMasterGetChunkFile = 49;

    public const int VstMidiType = 1;

    // VstTimeInfo flags
    public const int VstTransportPlaying = 1 << 1;
    public const int VstPpqPosValid = 1 << 9;
    public const int VstTempoValid = 1 << 10;
    public const int VstTimeSigValid = 1 << 13;

    // The bridge is offline. VST2's process level enum:
    // 0 unknown, 1 user, 2 realtime, 3 prefetch, 4 offline.
    public const int ProcessLevelOffline = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct VstEventsHeader
    {
        public int NumEvents;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VstMidiEvent
    {
        public int Type;
        public int ByteSize;
        public int DeltaFrames;
        public int Flags;
        public int NoteLength;
        public int NoteOffset;
        public byte Data0;
        public byte Data1;
        public byte Data2;
        public byte Data3;
        public byte Detune;
        public byte NoteOffVelocity;
        public byte Reserved1;
        public byte Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VstTimeInfo
    {
        public double SamplePos;
        public double SampleRate;
        public double NanoSeconds;
        public double PpqPos;
        public double Tempo;
        public double BarStartPos;
        public double CycleStartPos;
        public double CycleEndPos;
        public int TimeSigNumerator;
        public int TimeSigDenominator;
        public int SmpteOffset;
        public int SmpteFrameRate;
        public int SamplesToNextClock;
        public int Flags;
    }

    // AEffect field offsets depend on pointer width. The first version of the
    // reference host hard-coded the x86 numbers and corrupted memory on x64.
    public readonly struct AEffectLayout
    {
        public readonly int Dispatcher;
        public readonly int Process;
        public readonly int SetParameter;
        public readonly int GetParameter;
        public readonly int NumPrograms;
        public readonly int NumParams;
        public readonly int NumInputs;
        public readonly int NumOutputs;
        public readonly int Flags;
        public readonly int InitialDelay;
        public readonly int UniqueId;
        public readonly int Version;
        public readonly int ProcessReplacing;
        public readonly int ProcessDoubleReplacing;

        AEffectLayout(int dispatcher, int process, int setParameter, int getParameter,
            int numPrograms, int numParams, int numInputs, int numOutputs, int flags,
            int initialDelay, int uniqueId, int version, int processReplacing, int processDoubleReplacing)
        {
            Dispatcher = dispatcher;
            Process = process;
            SetParameter = setParameter;
            GetParameter = getParameter;
            NumPrograms = numPrograms;
            NumParams = numParams;
            NumInputs = numInputs;
            NumOutputs = numOutputs;
            Flags = flags;
            InitialDelay = initialDelay;
            UniqueId = uniqueId;
            Version = version;
            ProcessReplacing = processReplacing;
            ProcessDoubleReplacing = processDoubleReplacing;
        }

        public static AEffectLayout ForPointerSize(int pointerSize)
        {
            static int AlignUp(int value, int alignment)
                => (value + alignment - 1) / alignment * alignment;

            // magic is a 4-byte int; the first pointer is aligned to pointer size.
            int dispatcher = AlignUp(4, pointerSize);
            int process = dispatcher + pointerSize;
            int setParameter = process + pointerSize;
            int getParameter = setParameter + pointerSize;

            int numPrograms = getParameter + pointerSize;
            int numParams = numPrograms + 4;
            int numInputs = numParams + 4;
            int numOutputs = numInputs + 4;
            int flags = numOutputs + 4;

            int resvd1 = AlignUp(flags + 4, pointerSize);
            int resvd2 = resvd1 + pointerSize;
            int initialDelay = resvd2 + pointerSize;

            // initialDelay, realQualities, offQualities, ioRatio (4 x int),
            // then object / user pointers, uniqueID / version (2 x int).
            int uniqueId = initialDelay + 16 + pointerSize * 2;
            int version = uniqueId + 4;
            int processReplacing = AlignUp(version + 4, pointerSize);
            int processDoubleReplacing = processReplacing + pointerSize;

            return new AEffectLayout(dispatcher, process, setParameter, getParameter,
                numPrograms, numParams, numInputs, numOutputs, flags,
                initialDelay, uniqueId, version, processReplacing, processDoubleReplacing);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr VstPluginMain(IntPtr hostCallback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr VstDispatcherProc(IntPtr effect, int opcode, int index,
        IntPtr value, IntPtr ptr, float opt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VstSetParameterProc(IntPtr effect, int index, float value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate float VstGetParameterProc(IntPtr effect, int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VstProcessProc(IntPtr effect, IntPtr inputs, IntPtr outputs, int frames);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr VstHostCallbackProc(IntPtr effect, int opcode, int index,
        IntPtr value, IntPtr ptr, float opt);

    public static IntPtr Call(VstDispatcherProc dispatcher, IntPtr effect, int opcode, int index,
        IntPtr value, IntPtr ptr, float opt)
        => dispatcher(effect, opcode, index, value, ptr, opt);

    public static string ReadString(VstDispatcherProc dispatcher, IntPtr effect, int opcode, int index = 0)
    {
        var buffer = new byte[256];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            Call(dispatcher, effect, opcode, index, IntPtr.Zero, handle.AddrOfPinnedObject(), 0);
        }
        finally
        {
            handle.Free();
        }

        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
            end = buffer.Length;

        return System.Text.Encoding.UTF8.GetString(buffer, 0, end);
    }
}
