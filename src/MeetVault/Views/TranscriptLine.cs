using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;

namespace MeetVault.Views;

// Plain class (not a record): XAML compiled bindings need settable properties. The INPC
// plumbing exists only for the Details playhead highlight; other properties stay plain.
public sealed class TranscriptLine : INotifyPropertyChanged
{
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public TranscriptLine() { }
    public TranscriptLine(string speaker, string text) { Speaker = speaker; Text = text; }

    private static readonly SolidColorBrush HighlightBrush = new(Windows.UI.Color.FromArgb(0x33, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
    private bool _isActive;

    /// <summary>Playhead highlight: true while the Details player sits inside this line.
    /// Never serialized: this type is the Gemini payload record (a live WinUI brush in the
    /// JSON would throw at serialize time under source-gen — review r8).</summary>
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
        }
    }

    // Always non-null so the row Border stays hit-testable (tapping a row seeks the player).
    [JsonIgnore]
    public Brush RowBackground => _isActive ? HighlightBrush : TransparentBrush;

    public event PropertyChangedEventHandler? PropertyChanged;
}

