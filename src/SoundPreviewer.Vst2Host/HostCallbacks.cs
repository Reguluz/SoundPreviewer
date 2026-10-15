using Jacobi.Vst.Core;
using Jacobi.Vst.Core.Host;

namespace SoundPreviewer.Vst2Host;

internal sealed class HostCallbacks : IVstHostCommandStub
{
    public const int BlockSize = 512;
    public IVstPluginContext PluginContext { get; set; } = null!;
    public IVstHostCommands20 Commands { get; }
    public long SamplePosition { get; set; }
    public int SampleRate { get; }
    public int Bpm { get; }
    public string Directory { get; }
    public HostCallbacks(int sampleRate, int bpm, string directory)
    {
        SampleRate = sampleRate;
        Bpm = bpm;
        Directory = directory;
        Commands = new Callbacks(this);
    }

    private sealed class Callbacks(HostCallbacks host) : IVstHostCommands20
    {
        public bool BeginEdit(int index) => false;
        public bool EndEdit(int index) => false;
        public VstCanDoResult CanDo(string cando) => cando is "sendVstEvents" or "sendVstMidiEvent" or "sendVstTimeInfo" ? VstCanDoResult.Yes : VstCanDoResult.Unknown;
        public bool CloseFileSelector(VstFileSelect fileSelect) => false;
        public bool OpenFileSelector(VstFileSelect fileSelect) => false;
        public VstAutomationStates GetAutomationState() => VstAutomationStates.Off;
        public int GetBlockSize() => BlockSize;
        public string GetDirectory() => host.Directory;
        public int GetInputLatency() => 0;
        public int GetOutputLatency() => 0;
        public VstHostLanguage GetLanguage() => VstHostLanguage.English;
        public VstProcessLevels GetProcessLevel() => VstProcessLevels.Offline;
        public string GetProductString() => "SoundPreviewer VST2 x86";
        public float GetSampleRate() => host.SampleRate;
        public VstTimeInfo GetTimeInfo(VstTimeInfoFlags filterFlags) => new()
        {
            SamplePosition = host.SamplePosition,
            SampleRate = host.SampleRate,
            PpqPosition = host.SamplePosition * host.Bpm / (60.0 * host.SampleRate),
            Tempo = host.Bpm,
            TimeSignatureNumerator = 4,
            TimeSignatureDenominator = 4,
            Flags = VstTimeInfoFlags.TransportPlaying | VstTimeInfoFlags.PpqPositionValid | VstTimeInfoFlags.TempoValid | VstTimeInfoFlags.TimeSignatureValid
        };
        public string GetVendorString() => "SoundPreviewer";
        public int GetVendorVersion() => 1000;
        public bool IoChanged() => false;
        public bool ProcessEvents(VstEvent[] events) => false;
        public bool SizeWindow(int width, int height) => false;
        public bool UpdateDisplay() => true;
        public int GetCurrentPluginID() => host.PluginContext?.PluginInfo?.PluginID ?? 0;
        public int GetVersion() => 2400;
        public void ProcessIdle() { }
        public void SetParameterAutomated(int index, float value) { }
    }
}
