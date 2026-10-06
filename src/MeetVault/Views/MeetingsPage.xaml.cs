using Microsoft.UI.Xaml.Controls;
using MeetVault.Core;
using Microsoft.UI.Xaml;

namespace MeetVault.Views;

/// <summary>Display projection of a <see cref="Meeting"/>; keeps formatted text out of the XAML.</summary>
public sealed record MeetingCard(string Id, string Title, string When, string Duration, string Status, bool IsDraft);

public static class MeetingCards
{
    public static MeetingCard ToCard(Meeting m) => new(m.Id, m.Title, m.StartedAt.ToString("g"), Duration(m), StatusLabel(m.Status), m.Status is MeetingStatus.Recording or MeetingStatus.Paused);

    // Plain label for the raw enum.
    public static string StatusLabel(MeetingStatus status) => status switch
    {
        MeetingStatus.Recording => "Recording",
        MeetingStatus.Paused => "Paused",
        MeetingStatus.Processing => "Processing",
        MeetingStatus.Ready => "Ready",
        MeetingStatus.Failed => "Did not finish",
        _ => "Unknown",
    };

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
            }
            return _repo;
        }
    }

    public static IReadOnlyList<MeetingCard> Cards()
        => Repo.ListMeetings().Select(MeetingCards.ToCard).ToArray();
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

    // Click a row: an unfinished draft resumes in Live, a finished meeting opens in Details.
    private void OpenMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MeetingCard card })
        {
            if (card.IsDraft)
            {
                PendingRecovery.MeetingId = card.Id;
                (App.Window as MainWindow)?.NavigateToLive();
            }
            else
            {
                PendingDetails.MeetingId = card.Id;
                (App.Window as MainWindow)?.NavigateToDetails();
            }
        }
    }

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

