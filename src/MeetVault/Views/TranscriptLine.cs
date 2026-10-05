namespace MeetVault.Views;

// Plain class (not a record): XAML compiled bindings need settable properties.
public sealed class TranscriptLine
{
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public TranscriptLine() { }
    public TranscriptLine(string speaker, string text) { Speaker = speaker; Text = text; }
    public TranscriptLine WithSpeaker(string speaker) => new(speaker, Text);
}

