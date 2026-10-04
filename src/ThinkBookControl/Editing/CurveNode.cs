using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace ThinkBookControl.Editing;
public sealed record CurvePreset(string Name, string Fan1Curve, string Fan2Curve);
public sealed class CurveNode : INotifyPropertyChanged
{
    private string _temperature = "", _rpm = "", _error = "";
    public string Temperature { get => _temperature; set { _temperature = value; Notify(); } }
    public string Rpm { get => _rpm; set { _rpm = value; Notify(); } }
    public string Error { get => _error; set { if (_error == value) return; _error = value; Notify(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
