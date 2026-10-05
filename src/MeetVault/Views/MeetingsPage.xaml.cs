using Microsoft.UI.Xaml.Controls;
using MeetVault.Core;
using Microsoft.UI.Xaml;

namespace MeetVault.Views;

/// <summary>Display projection of a <see cref="Meeting"/>; keeps formatted text out of the XAML.</summary>
public sealed record MeetingCard(string Title, string When, string Duration, string Status);

// ponytail: demo data lives here so Dashboard and Meetings read one list; swap for LocalMeetingRepository in P4.
public static class DemoMeetings
{
    public static IReadOnlyList<Meeting> All { get; } = new[]
    {
        new Meeting("demo-roadmap", "Quarterly roadmap sync",
            new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(7)),
            new DateTimeOffset(2026, 10, 2, 10, 18, 0, TimeSpan.FromHours(7)),
            MeetingStatus.Ready, null, null),
        new Meeting("demo-interview", "Customer interview: Northwind",
            new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(7)),
            new DateTimeOffset(2026, 10, 5, 14, 27, 0, TimeSpan.FromHours(7)),
            MeetingStatus.Ready, null, null),
        new Meeting("demo-standup", "Team standup",
            new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.FromHours(7)),
            null, MeetingStatus.Recording, null, null),
    };

    public static IReadOnlyList<MeetingCard> Cards { get; } = All.Select(Card).ToArray();

    private static MeetingCard Card(Meeting m) => new(m.Title, m.StartedAt.ToString("g"), Duration(m), m.Status.ToString());

    private static string Duration(Meeting m)
    {
        if (m.EndedAt is not { } ended) return "In progress";
        var minutes = (int)Math.Round((ended - m.StartedAt).TotalMinutes);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }
}

public sealed partial class MeetingsPage : Page
{
    public MeetingsPage()
    {
        InitializeComponent();
        ApplyFilter(string.Empty);
        StatusBar.Message = $"{DemoMeetings.Cards.Count} meetings from local demo data.";
        StatusBar.IsOpen = true;
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => ApplyFilter(sender.Text);

    private void ApplyFilter(string query)
    {
        var term = query.Trim();
        MeetingList.ItemsSource = DemoMeetings.Cards
            .Where(c => term.Length == 0 || c.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        EmptyState.Visibility = MeetingList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyState.Visibility == Visibility.Visible)
            EmptyState.Text = term.Length == 0
                ? "No meetings yet. Start one to populate this list."
                : $"No meetings match \"{term}\".";
    }
}
