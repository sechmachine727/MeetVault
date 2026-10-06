using Microsoft.UI.Xaml.Controls;
using MeetVault.Core;
using Microsoft.UI.Xaml;

namespace MeetVault.Views;

/// <summary>Display projection of a <see cref="Meeting"/>; keeps formatted text out of the XAML.</summary>
public sealed record MeetingCard(string Title, string When, string Duration, string Status);

// Demo rows double as the first-run seed; AppStore inserts them when the repo is empty.
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
            null, MeetingStatus.Ready, null, null),
    };

    public static IReadOnlyList<MeetingCard> Cards { get; } = All.Select(Card).ToArray();

    public static MeetingCard ToCard(Meeting m) => Card(m);

    private static MeetingCard Card(Meeting m) => new(m.Title, m.StartedAt.ToString("g"), Duration(m), m.Status.ToString());

    private static string Duration(Meeting m)
    {
        if (m.EndedAt is not { } ended) return "In progress";
        var minutes = (int)Math.Round((ended - m.StartedAt).TotalMinutes);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }
}

// Single shared SQLite repo; Meetings/Dashboard project Repo.ListMeetings() to cards.
public static class AppStore
{
    private static LocalMeetingRepository? _repo;

    // M7: single shared secret store; Settings/Details/uploader all use this, never new().
    private static CredentialLockerSecretStore? _secrets;
    public static CredentialLockerSecretStore Secrets => _secrets ??= new();

    public static LocalMeetingRepository Repo
    {
        get
        {
            if (_repo is null)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetVault");
                Directory.CreateDirectory(dir);
                _repo = new LocalMeetingRepository(Path.Combine(dir, "meetings.db"));
                SeedDemoData();
            }
            return _repo;
        }
    }

    private static void SeedDemoData()
    {
        if (_repo!.ListMeetings().Count != 0) return;
        foreach (var m in DemoMeetings.All)
            _repo.Save(m, Array.Empty<Speaker>(), Array.Empty<Segment>());
    }

    public static IReadOnlyList<MeetingCard> Cards()
        => Repo.ListMeetings().Select(DemoMeetings.ToCard).ToArray();
}

public sealed partial class MeetingsPage : Page
{
    public MeetingsPage()
    {
        InitializeComponent();
        ApplyFilter(string.Empty);
        StatusBar.Message = $"{AppStore.Cards().Count} meetings from the local repository.";
        StatusBar.IsOpen = true;
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => ApplyFilter(sender.Text);

    private void ApplyFilter(string query)
    {
        var term = query.Trim();
        var rows = AppStore.Cards().Where(c => term.Length == 0 || c.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        MeetingList.ItemsSource = rows;

        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyState.Visibility == Visibility.Visible)
            EmptyState.Text = term.Length == 0
                ? "No meetings yet. Start one to populate this list."
                : $"No meetings match \"{term}\".";
    }
}

