namespace TuneLab.VstInstruments.Bridge.Hosts;

internal interface ILv2EditorWindow : System.IDisposable
{
    bool IsAlive { get; }

    void BringToFront();

    void NotifyPortEvent(uint port, float value);
}