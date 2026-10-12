using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MeetVault.Views;

// Semantic color for a meeting status label (Ready vs Paused were indistinguishable).
public sealed class StatusBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var state = value as string ?? "";
        var key = state == "Ready" ? "SystemFillColorSuccessBrush"
            : state is "Paused" or "In progress" ? "SystemFillColorCautionBrush"
            : "TextFillColorSecondaryBrush";
        return Application.Current.Resources.TryGetValue(key, out var brush) ? brush : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
