using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MeetVault.Views;

// Semantic color for a meeting status KIND (the raw enum name, never the localized label —
// the bound display text is the per-locale Mtg.Status* string).
public sealed class StatusBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var kind = value as string ?? "";
        var key = kind == "Ready" ? "SystemFillColorSuccessBrush"
            : kind == "Failed" ? "SystemFillColorCriticalBrush"
            : kind is "Recording" or "Paused" or "Processing" ? "SystemFillColorCautionBrush"
            : "TextFillColorSecondaryBrush";
        return Application.Current.Resources.TryGetValue(key, out var brush) ? brush : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
