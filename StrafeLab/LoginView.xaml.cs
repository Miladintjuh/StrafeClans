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

    private async void SignInButton_Click(object sender, RoutedEventArgs e) => await SignInAsync();
    private async void CreateButton_Click(object sender, RoutedEventArgs e) => await CreateAsync();

    private async Task SignInAsync()
    {
        await RunAuthActionAsync(async () =>
        {
            var message = await _supabase.SignInAsync(UsernameBox.Text, PasswordBox.Password);
            AuthChanged?.Invoke();
            return message;
        });
    }

    private async Task CreateAsync()
    {
        await RunAuthActionAsync(async () =>
        {
            var message = await _supabase.SignUpAsync(UsernameBox.Text, PasswordBox.Password);
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
        StatusText.Text = "Local-only mode is active. You can still create an online profile later from Profile.";
        AuthChanged?.Invoke();
        LocalOnlyRequested?.Invoke();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private async void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SignInAsync();
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
            StatusText.Text = ToCleanError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshState()
    {
        BackButton.Visibility = _startupWelcome ? Visibility.Collapsed : Visibility.Visible;
        AccountTitleText.Text = _supabase.IsSignedIn ? "Account" : "Welcome to StrafeLab";
        AccountSubtitleText.Text = _supabase.IsSignedIn
            ? "Your online profile is active."
            : "Online is recommended for synced CS2 practice stats. Local-only is optional.";
        StatusText.Text = _supabase.IsSignedIn ? $"Signed in as {_supabase.DisplayName}." : "Create or sign in with username and password.";
        SignOutButton.Visibility = _supabase.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.IsEnabled = _supabase.IsSignedIn;
        FullOptOutButton.IsEnabled = true;
    }

    private void SetBusy(bool busy)
    {
        SignInButton.IsEnabled = !busy;
        CreateButton.IsEnabled = !busy;
        SignOutButton.IsEnabled = !busy && _supabase.IsSignedIn;
        FullOptOutButton.IsEnabled = !busy;
    }

    private static string ToCleanError(string raw)
    {
        string text = raw ?? string.Empty;
        string lower = text.ToLowerInvariant();
        if (lower.Contains("invalid login") || lower.Contains("invalid credentials"))
            return "Username or password is incorrect. Check the fields or create a new online profile.";
        if (lower.Contains("user already") || lower.Contains("already registered") || lower.Contains("already exists"))
            return "This username already exists. Use Sign in instead.";
        if (lower.Contains("password") && (lower.Contains("weak") || lower.Contains("at least")))
            return "Password is too weak. Use at least 6 characters.";
        if (lower.Contains("email_address_invalid") || lower.Contains("email address") && lower.Contains("invalid"))
            return "The generated username login address was rejected by Supabase. This build now uses a valid hidden login domain; update the app and try creating the account again.";
        if (lower.Contains("email_not_confirmed") || lower.Contains("email not confirmed"))
            return "Supabase email confirmation is enabled, but StrafeLab uses username/password accounts without email verification. Turn off email confirmation in Supabase Auth settings, then try again.";
        if (lower.Contains("rate limit"))
            return "Supabase is rate-limiting auth requests. Wait a minute and try again.";
        if (lower.Contains("network") || lower.Contains("connection") || lower.Contains("timed out"))
            return "Could not reach Supabase. Check internet connection, firewall, or project status.";
        if (lower.Contains("username") && lower.Contains("least 3"))
            return "Username must be at least 3 characters.";
        if (text.StartsWith("Supabase", StringComparison.OrdinalIgnoreCase))
            return "Cloud action failed. " + text;
        return string.IsNullOrWhiteSpace(text) ? "Cloud action failed. Try again." : text;
    }
}
