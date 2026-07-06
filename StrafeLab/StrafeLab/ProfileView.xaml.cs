using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StrafeLab.Models;
using StrafeLab.Services;

namespace StrafeLab;

public partial class ProfileView : UserControl
{
    private readonly SupabaseApiClient _supabase;
    private readonly string? _username;
    private readonly bool _ownProfile;

    public event Action? BackRequested;
    public event Action? AuthChanged;

    public ProfileView(SupabaseApiClient supabase, string? username = null)
    {
        _supabase = supabase;
        _username = string.IsNullOrWhiteSpace(username) ? null : username.Trim().TrimStart('@');
        _ownProfile = _username == null || string.Equals(_username, _supabase.Session?.Username, StringComparison.OrdinalIgnoreCase);
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _supabase.SignOutAsync();
            AuthChanged?.Invoke();
            BackRequested?.Invoke();
        }
        catch (Exception ex)
        {
            SubtitleText.Text = ex.Message;
        }
    }

    private async void KeepPrivateButton_Click(object sender, RoutedEventArgs e) => await SetVisibilityAsync(false);
    private async void MakePublicButton_Click(object sender, RoutedEventArgs e) => await SetVisibilityAsync(true);
    private async void SetPrivateButton_Click(object sender, RoutedEventArgs e) => await SetVisibilityAsync(false);
    private async void SetPublicButton_Click(object sender, RoutedEventArgs e) => await SetVisibilityAsync(true);
    private async void SaveRecoveryEmailButton_Click(object sender, RoutedEventArgs e) => await SaveRecoveryEmailAsync();


    private async Task SaveRecoveryEmailAsync()
    {
        if (!_ownProfile) return;
        try
        {
            await _supabase.SetRecoveryEmailAsync(RecoveryEmailBox.Text);
            SubtitleText.Text = "Recovery email saved. Supabase may send a confirmation email before password resets are active.";
            AuthChanged?.Invoke();
        }
        catch (Exception ex)
        {
            SubtitleText.Text = ToCleanProfileError(ex.Message);
        }
    }

    private static string ToCleanProfileError(string raw)
    {
        string lower = (raw ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("email")) return "That email could not be saved. Check the address or try again later.";
        if (lower.Contains("rate limit")) return "Supabase is rate-limiting account changes. Wait a minute and try again.";
        return string.IsNullOrWhiteSpace(raw) ? "Profile update failed." : raw;
    }

    private async Task SetVisibilityAsync(bool isPublic)
    {
        if (!_ownProfile) return;
        try
        {
            await _supabase.SetProfileVisibilityAsync(isPublic);
            AuthChanged?.Invoke();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SubtitleText.Text = ex.Message;
        }
    }

    private async Task LoadAsync()
    {
        if (!_supabase.IsSignedIn)
        {
            SubtitleText.Text = "Sign in first.";
            return;
        }

        try
        {
            if (_ownProfile) await _supabase.EnsureProfileAsync();

            CloudProfileStats stats = _ownProfile
                ? await _supabase.GetMyProfileStatsAsync()
                : await _supabase.GetPublicProfileStatsAsync(_username ?? string.Empty);

            Render(stats);
        }
        catch (Exception ex)
        {
            SubtitleText.Text = ex.Message;
        }
    }

    private void Render(CloudProfileStats stats)
    {
        string username = string.IsNullOrWhiteSpace(stats.Username) ? _supabase.DisplayName : "@" + stats.Username;
        TitleText.Text = _ownProfile ? "Profile" : username;
        SubtitleText.Text = _ownProfile
            ? (stats.IsAnonymous
                ? "Anonymous stats-sharing profile. Only saved summary statistics are uploaded; raw key/mouse data stays local."
                : "Your synced summary statistics. Raw key/mouse data is not uploaded.")
            : "Public profile summary.";

        IdentityText.Text = _ownProfile ? $"Signed in as {username}{(stats.IsAnonymous ? " · anonymous" : string.Empty)}" : username;
        VisibilityText.Text = $"Visibility: {(stats.IsPublic ? "public" : "private")}";
        PrivacyChoicePanel.Visibility = Visibility.Collapsed;
        VisibilityPromptText.Visibility = _ownProfile ? Visibility.Visible : Visibility.Collapsed;
        VisibilityPromptText.Text = stats.PrivacyChoiceMade
            ? "You can change this any time. Private profiles cannot be opened from ranking lists."
            : "Choose whether other clan members can open your profile from ranking lists. Private is the default until you decide.";
        SetPrivateButton.Visibility = _ownProfile ? Visibility.Visible : Visibility.Collapsed;
        SetPublicButton.Visibility = _ownProfile ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Visibility = _ownProfile ? Visibility.Visible : Visibility.Collapsed;
        RecoveryEmailBox.Text = _supabase.Session?.Email ?? string.Empty;
        RecoveryEmailBox.Visibility = _ownProfile ? Visibility.Visible : Visibility.Collapsed;
        SaveRecoveryEmailButton.Visibility = _ownProfile ? Visibility.Visible : Visibility.Collapsed;

        SessionsText.Text = stats.Sessions.ToString(CultureInfo.InvariantCulture);
        AttemptsText.Text = stats.Attempts.ToString(CultureInfo.InvariantCulture);
        CleanRateText.Text = stats.CleanRate.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        MovingRateText.Text = stats.MovingRate.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        CleanText.Text = stats.Clean.ToString(CultureInfo.InvariantCulture);
        MovingText.Text = stats.Moving.ToString(CultureInfo.InvariantCulture);
        CounterText.Text = stats.AverageCounterDelayMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        ClickText.Text = stats.AverageClickDelayMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
    }
}
