using MeetVault.Core;
using MeetVault.Core.Backend;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

// Cloud Storage: meetings on the backend service for the SIGNED-IN user; the server list is
// the only source of truth here (local-only meetings never appear).
public sealed partial class BackendPage : Page
{
    public sealed class CloudRow
    {
        public string Id { get; set; } = "";
        public string? ExternalId { get; set; }
        public string Title { get; set; } = "";
        public string Meta { get; set; } = "";
        public string StatusLine { get; set; } = "";
        public string OpenLabel { get; set; } = "";
        public string OpenAutomation { get; set; } = "";
        public string RemoveLabel { get; set; } = "";
        public string RemoveAutomation { get; set; } = "";
    }

    private readonly List<CloudRow> _rows = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pollTimer;

    public BackendPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
        Unloaded += (_, _) => { _pollTimer?.Stop(); _pollTimer = null; };
        RenderSignedState();
    }

    private void RenderSignedState()
    {
        var signedIn = AppSession.IsSignedIn;
        SignInActionButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        if (!signedIn)
        {
            MeetingsList.ItemsSource = null;
            EmptyState.Visibility = Visibility.Visible;
            EmptyState.Text = AppSession.LastError ?? Loc.T("Cloud.SignInNeeded");
            // The sign-in button is the InfoBar's ActionButton: a closed InfoBar hides it, so the
            // signed-out page must open the bar to expose the only sign-in affordance (review fix).
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = EmptyState.Text;
            StatusBar.IsOpen = true;
        }
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is MainWindow window) window.NavigateToAccount();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (!AppSession.IsSignedIn)
        {
            await AppSession.RestoreAsync();
            RenderSignedState();
            if (!AppSession.IsSignedIn) return;
        }
        if (!AppSession.TryCreateClient(out var client)) { RenderSignedState(); return; }
        LoadingRing.IsActive = true;
        StatusBar.IsOpen = false;
        try
        {
            var meetings = await client.ListMeetingsAsync(limit: 200);
            _rows.Clear();
            foreach (var meeting in meetings)
            {
                var state = BackendAnalysisState.FromCloudRow(meeting.Status, meeting.LatestJobType, meeting.LatestJobState, meeting.HasAnalysis);
                var stateKey = state switch
                {
                    AnalysisState.Uploading => "An.Uploading",
                    AnalysisState.Queued => "An.Queued",
                    AnalysisState.Processing => "An.Processing",
                    AnalysisState.Completed => "An.Completed",
                    AnalysisState.Failed => "An.Failed",
                    _ => "An.NotAnalyzed",
                };
                _rows.Add(new CloudRow
                {
                    Id = meeting.Id,
                    ExternalId = meeting.ExternalId,
                    Title = meeting.Title,
                    Meta = Loc.Tf("Cloud.Meta", meeting.StartedAt ?? meeting.CreatedAt ?? "", FormatDuration(meeting.DurationMs)),
                    StatusLine = Loc.T(stateKey),
                    OpenLabel = Loc.T("Cloud.OpenLabel"),
                    OpenAutomation = Loc.T("Cloud.Open.AutomationProperties.Name"),
                    RemoveLabel = Loc.T("Cloud.Remove.Content"),
                    RemoveAutomation = Loc.T("Cloud.Remove.AutomationProperties.Name"),
                });
            }
            MeetingsList.ItemsSource = null;
            MeetingsList.ItemsSource = _rows.ToArray(); // snapshot across the ABI
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // The XAML x:Uid="Cloud.Empty" owns this string; MRT forbids a bare Cloud.Empty key
            // alongside Cloud.Empty.Text, so the code reads the same resource key as the XAML.
            EmptyState.Text = Loc.T("Cloud.Empty.Text");
            SchedulePollIfProcessing();
        }
        catch (BackendApiException ex) when (ex.StatusCode == 401)
        {
            AppSession.NoteAuthFailure(ex);
            RenderSignedState();
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = Loc.Tf("Cloud.Error", DescribeBackendError(ex));
            StatusBar.IsOpen = true;
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    // Auto-refresh while anything is processing; stops when everything is terminal.
    private void SchedulePollIfProcessing()
    {
        var pending = _rows.Any(r => r.StatusLine == Loc.T("An.Queued") || r.StatusLine == Loc.T("An.Processing") || r.StatusLine == Loc.T("An.Uploading"));
        _pollTimer?.Stop();
        _pollTimer = null;
        if (!pending) return;
        _pollTimer = DispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(10);
        _pollTimer.Tick += async (_, _) => await LoadAsync();
        _pollTimer.Start();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CloudRow row) return;
        var local = row.ExternalId is { Length: > 0 } external ? AppStore.Repo.Load(external) : null;
        if (local is not null) PendingDetails.MeetingId = local.Id;
        else PendingDetails.CloudBackendId = row.Id;
        if (App.Window is MainWindow window) window.NavigateToDetails();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CloudRow row) return;
        var confirm = new ContentDialog
        {
            Title = Loc.T("Cloud.RemoveTitle"),
            Content = Loc.T("Cloud.RemoveBody"),
            PrimaryButtonText = Loc.T("Cloud.Remove.Content"),
            CloseButtonText = Loc.T("Det.Cancel"),
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        if (!AppSession.TryCreateClient(out var client)) return;
        try
        {
            await client.DeleteMeetingAsync(row.Id);
            if (row.ExternalId is { Length: > 0 } external
                && AppStore.Repo.Load(external) is { } local
                && AppStore.Repo.LoadBackendSync(local.Id)?.BackendId == row.Id)
            {
                AppStore.Repo.ClearBackendSync(local.Id);
            }
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = Loc.T("Cloud.Removed");
            StatusBar.IsOpen = true;
            await LoadAsync();
        }
        catch (BackendApiException ex)
        {
            if (ex.StatusCode == 401) AppSession.NoteAuthFailure(ex);
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = Loc.Tf("Cloud.Error", DescribeBackendError(ex));
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = Loc.Tf("Cloud.Error", DescribeBackendError(ex));
            StatusBar.IsOpen = true;
        }
    }

    private static string FormatDuration(long ms)
    {
        var total = (int)Math.Round(ms / 1000.0);
        return total < 60 ? total + "s" : $"{total / 60}:{total % 60:00}";
    }

    private void Status(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private static string DescribeBackendError(Exception ex) => ex switch
    {
        BackendApiException api => api.Code + ": " + api.Message,
        _ => Loc.T("Det.BackendUnreachable"),
    };
}
