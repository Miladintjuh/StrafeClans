using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StrafeLab.Services;

namespace StrafeLab;

public partial class LoginView : UserControl
{
    private readonly SupabaseApiClient _supabase;
    private readonly bool _startupWelcome;

    public event Action? BackRequested;
    public event Action? AuthChanged;
    public event Action? LocalOnlyRequested;

    public LoginView(SupabaseApiClient supabase, bool startupWelcome = false)
    {
        _supabase = supabase;
        _startupWelcome = startupWelcome;
        InitializeComponent();
        RefreshState();
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthActionAsync(async () =>
        {
            var message = await _supabase.SignInAsync(EmailBox.Text, PasswordBox.Password);
            AuthChanged?.Invoke();
            return message;
        });
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthActionAsync(async () =>
        {
            var message = await _supabase.SignUpAsync(EmailBox.Text, PasswordBox.Password, UsernameBox.Text);
            AuthChanged?.Invoke();
            return message;
        });
    }

    private async void ShareStatsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthActionAsync(async () =>
        {
            var message = await _supabase.SignInAnonymouslyAsync(AnonymousNameBox.Text);
            AuthChanged?.Invoke();
            return message;
        });
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthActionAsync(async () =>
        {
            var message = await _supabase.SignOutAsync();
            AuthChanged?.Invoke();
            return message;
        });
    }

    private void FullOptOutButton_Click(object sender, RoutedEventArgs e)
    {
        _supabase.ForgetLocalSession();
        RefreshState();
        StatusText.Text = "Cloud sharing is off. StrafeLab will stay local for this run and show this first-time screen again next launch.";
        AuthChanged?.Invoke();
        LocalOnlyRequested?.Invoke();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private async void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunAuthActionAsync(async () =>
        {
            var message = await _supabase.SignInAsync(EmailBox.Text, PasswordBox.Password);
            AuthChanged?.Invoke();
            return message;
        });
    }

    private async Task RunAuthActionAsync(Func<Task<string>> action)
    {
        SetBusy(true);
        try
        {
            StatusText.Text = await action();
            RefreshState();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshState()
    {
        BackButton.Visibility = _startupWelcome ? Visibility.Collapsed : Visibility.Visible;
        AccountTitleText.Text = _supabase.IsSignedIn
            ? "Account"
            : (_startupWelcome ? "Welcome to StrafeLab" : "Choose how you want to use StrafeLab");
        AccountSubtitleText.Text = _supabase.IsSignedIn
            ? "Manage your account and synced practice summaries."
            : (_startupWelcome ? "Choose a privacy option or sign in to continue." : "Choose how you want to use StrafeLab.");
        StatusText.Text = _supabase.IsSignedIn ? $"Signed in as {_supabase.DisplayName}." : (_startupWelcome ? "Pick one option below to open StrafeLab." : "Not signed in.");
        SignOutButton.Visibility = _supabase.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.IsEnabled = _supabase.IsSignedIn;
        ShareStatsButton.IsEnabled = !_supabase.IsSignedIn;
        FullOptOutButton.IsEnabled = true;
    }

    private void SetBusy(bool busy)
    {
        SignInButton.IsEnabled = !busy;
        CreateButton.IsEnabled = !busy;
        SignOutButton.IsEnabled = !busy && _supabase.IsSignedIn;
        ShareStatsButton.IsEnabled = !busy && !_supabase.IsSignedIn;
        FullOptOutButton.IsEnabled = !busy;
    }
}
