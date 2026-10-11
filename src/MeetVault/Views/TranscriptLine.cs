namespace MeetVault.Views;

// Plain class (not a record): XAML compiled bindings need settable properties.
public sealed class TranscriptLine
{
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public TranscriptLine() { }
    public TranscriptLine(string speaker, string text) { Speaker = speaker; Text = text; }
    public TranscriptLine WithSpeaker(string speaker) => new(speaker, Text);

    // Diarization-off lines carry no name: the name column collapses and the text keeps a
    // small left margin. Computed so the template needs no converters; JsonIgnore keeps
    // them out of the Gemini transcript payload.
    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Visibility SpeakerVisibility => Speaker.Length == 0 ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    [System.Text.Json.Serialization.JsonIgnore]
    public double NameWidth => Speaker.Length == 0 ? 0 : 72;
    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Thickness NameMargin => Speaker.Length == 0 ? new Microsoft.UI.Xaml.Thickness(0) : new Microsoft.UI.Xaml.Thickness(0, 0, 8, 0);
    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Thickness TextMargin => Speaker.Length == 0 ? new Microsoft.UI.Xaml.Thickness(8, 0, 0, 0) : new Microsoft.UI.Xaml.Thickness(0);
}

