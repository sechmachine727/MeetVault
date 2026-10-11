using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MeetVault.Views;

public sealed partial class OverlayViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isPinned = true;

    // Defaults (owner convention): a light background dim and 18 pt captions.
    [ObservableProperty]
    private double _opacity = 0.2;

    [ObservableProperty]
    private double _fontSize = 18;

    // History-buffer retention for the overlay's own list — NOT the visible count
    // (that is ViewLines, the single overlay.viewlines setting the Settings row and
    // the cogwheel flyout share; overlay.lines is retired — read once as a fallback
    // and migrated forward).
    [ObservableProperty]
    private double _lineCount = 40;

    // Conversation settings owned by the overlay cogwheel (persisted as
    // overlay.viewlines / overlay.fontfamily).
    [ObservableProperty]
    private double _viewLines = 4;

    [ObservableProperty]
    private string _fontFamily = "Segoe UI";

    // Interim (partial) text of the open streaming utterance; "" clears. Plain property:
    // the overlay updates/rebuilds explicitly on partials — no INPC needed.
    public string Partial { get; set; } = "";

    public ObservableCollection<TranscriptLine> Lines { get; } = [];

    public void AddLine(string speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        Lines.Add(new TranscriptLine(speaker, text));
        TrimToLineCount();
    }

    partial void OnLineCountChanged(double value) => TrimToLineCount();

    private void TrimToLineCount()
    {
        var keep = Math.Max(1, (int)LineCount);
        while (Lines.Count > keep)
            Lines.RemoveAt(0);
    }
}
