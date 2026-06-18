using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StrafeLab.Models;
using StrafeLab.Services;

namespace StrafeLab;

public partial class AdminView : UserControl
{
    private readonly SupabaseApiClient _supabase;

    public event Action? BackRequested;

    public AdminView(SupabaseApiClient supabase)
    {
        _supabase = supabase;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private async Task RefreshAsync()
    {
        try
        {
            await _supabase.RefreshAdminAccessAsync();
            ModsButton.Visibility = _supabase.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            SubtitleText.Text = _supabase.IsAdmin
                ? "Aggregated summary stats from players who chose to share non-personal practice data. You can also manage moderators."
                : "Aggregated summary stats from players who chose to share non-personal practice data. Moderator view.";

            StatusText.Text = "Loading shared stats...";
            var global = await _supabase.GetAdminGlobalStatsAsync();
            var players = await _supabase.GetAdminPlayerStatsAsync();

            PlayersText.Text = global.Players.ToString(CultureInfo.InvariantCulture);
            AnonymousText.Text = global.AnonymousPlayers.ToString(CultureInfo.InvariantCulture);
            SessionsText.Text = global.Sessions.ToString(CultureInfo.InvariantCulture);
            AttemptsText.Text = global.Attempts.ToString(CultureInfo.InvariantCulture);
            MovingRateText.Text = global.MovingRate.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            PlayersGrid.ItemsSource = players;
            StatusText.Text = "Showing shared summary stats only. Raw input and mouse data are not uploaded.";

            if (_supabase.IsAdmin)
            {
                await RefreshModeratorCountAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task RefreshModeratorCountAsync()
    {
        try
        {
            int count = await _supabase.GetModeratorCountAsync();
            ModsButton.Content = $"Mods ({count})";
        }
        catch
        {
            ModsButton.Content = "Mods";
        }
    }

    private async void ModsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_supabase.IsAdmin) return;
        ModsOverlay.Visibility = Visibility.Visible;
        await RefreshModeratorsAsync();
    }

    private void CloseModsOverlay_Click(object sender, RoutedEventArgs e)
    {
        ModsOverlay.Visibility = Visibility.Collapsed;
        ModSearchResultsGrid.ItemsSource = null;
        ModsStatusText.Text = string.Empty;
    }

    private async void SearchModsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ModsStatusText.Text = "Searching...";
            var results = await _supabase.SearchModeratorCandidatesAsync(ModSearchBox.Text);
            ModSearchResultsGrid.ItemsSource = results;
            ModsStatusText.Text = results.Count == 0
                ? "No matching users found. Ask the player to create an account or enable anonymous stats sharing first, then search for their username."
                : $"Found {results.Count} matching user(s).";
        }
        catch (Exception ex)
        {
            ModsStatusText.Text = ex.Message;
        }
    }

    private async void AddModeratorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModeratorUser user }) return;
        try
        {
            ModsStatusText.Text = $"Adding @{user.Username} as a mod...";
            await _supabase.AddModeratorAsync(user.UserId);
            await RefreshModeratorsAsync();
            await RefreshModeratorCountAsync();
            ModsStatusText.Text = $"@{user.Username} can now view Admin stats.";
        }
        catch (Exception ex)
        {
            ModsStatusText.Text = ex.Message;
        }
    }

    private async void RemoveModeratorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModeratorUser user }) return;
        try
        {
            ModsStatusText.Text = $"Removing @{user.Username} from mods...";
            await _supabase.RemoveModeratorAsync(user.UserId);
            await RefreshModeratorsAsync();
            await RefreshModeratorCountAsync();
            ModsStatusText.Text = $"@{user.Username} is no longer a mod.";
        }
        catch (Exception ex)
        {
            ModsStatusText.Text = ex.Message;
        }
    }

    private async Task RefreshModeratorsAsync()
    {
        var mods = await _supabase.GetModeratorsAsync();
        ModeratorsGrid.ItemsSource = mods;
        ModsHelpText.Text = mods.Count == 0
            ? "No mods have been added yet. Search for a username or e-mail address below, then click Add. Mods can view the shared Admin stats page, but they cannot add or remove other mods."
            : "Current moderators. Mods can view shared Admin stats, but only the admin can manage this list.";
        ModsStatusText.Text = mods.Count == 0 ? "Add a mod by searching for a player below." : $"{mods.Count} moderator(s) configured.";
    }
}
