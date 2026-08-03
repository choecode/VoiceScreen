using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace VoiceScreen.App.Models;

public sealed class SubtitleLine : INotifyPropertyChanged
{
    private string _text = string.Empty;

    public required string Kind { get; init; }
    public string Text { get => _text; set { _text = value; OnPropertyChanged(); } }
    public Brush Foreground => Kind switch
    {
        "sent" => Brushes.LightGreen,
        "mine" => Brushes.LightSkyBlue,
        "status" => Brushes.Gold,
        _ => Brushes.White
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
