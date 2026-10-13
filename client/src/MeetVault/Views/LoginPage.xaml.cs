using MeetVault.Core.Backend;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class LoginPage : Page
{
    private bool _busy;

    public LoginPage()
    {
        InitializeComponent();
        Render();
        Loaded += async (_, _) =>
        {
            if (!AppSession.IsSignedIn && AppStore.Secrets.Get("AuthToken") is { Length: > 0 })
            {
                await AppSession.RestoreAsync();
                Render();
            }
        };
    }

    private void Render()
    {
        var signedIn = AppSession.IsSignedIn;
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (signedIn && AppSession.User is { } user)
        {
            AccountText.Text = Loc.Tf("Acc.SignedInAs", user.Email);
            ExpiryText.Text = AppSession.ExpiresAt is { Length: > 0 } at ? Loc.Tf("Acc.ExpiresAt", at) : "";
        }
        if (AppSession.LastError is { Length: > 0 } err)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = err;
            StatusBar.IsOpen = true;
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        StatusBar.IsOpen = false;
        if (AppSession.Url.Length == 0)
        {
            Fail(Loc.T("Acc.NoUrl"));
            return;
        }
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
        if (email.Length == 0 || password.Length == 0)
        {
            Fail(Loc.T("Acc.MissingFields"));
            return;
        }
        _busy = true;
        SignInButton.IsEnabled = false;
        SignInRing.IsActive = true;
        AppSession.ClearError();
        try
        {
            await AppSession.SignInAsync(email, password);
            PasswordBox.Password = "";
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = Loc.T("Acc.SignedIn");
            StatusBar.IsOpen = true;
            Render();
        }
        catch (BackendApiException ex) when (ex.Code == "invalid_credentials")
        {
            Fail(Loc.T("Acc.InvalidCredentials"));
        }
        catch (Exception ex)
        {
            Fail(Loc.Tf("Acc.SignInFailed", ex.Message));
        }
        finally
        {
            _busy = false;
            SignInButton.IsEnabled = true;
            SignInRing.IsActive = false;
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        SignOutButton.IsEnabled = false;
        try { await AppSession.SignOutAsync(); }
        finally
        {
            SignOutButton.IsEnabled = true;
            EmailBox.Text = "";
            PasswordBox.Password = "";
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = Loc.T("Acc.SignedOut");
            StatusBar.IsOpen = true;
            Render();
        }
    }

    private void Fail(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
