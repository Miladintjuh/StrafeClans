using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StrafeLab.Models;
using StrafeLab.Services;

namespace StrafeLab;

public partial class ClanView : UserControl
{
    private readonly SupabaseApiClient _supabase;
    private readonly Func<IReadOnlyList<SessionSummary>> _localSummaries;

    public event Action? BackRequested;
    public event Action<string>? ProfileRequested;

    public ClanView(SupabaseApiClient supabase, Func<IReadOnlyList<SessionSummary>> localSummaries)
    {
        _supabase = supabase;
        _localSummaries = localSummaries;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAllAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();
    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private async void CreateClanButton_Click(object sender, RoutedEventArgs e)
    {
        bool ok = await RunCloudActionAsync(async () =>
        {
            string name = ClanNameBox.Text.Trim();
            if (name.Length < 2) return "Enter a clan name first.";
            string clanId = await _supabase.CreateClanAsync(name);
            ClanNameBox.Text = string.Empty;
            return string.IsNullOrWhiteSpace(clanId) ? "Clan created." : $"Clan created ({clanId}).";
        });
        if (ok) await RefreshAllAsync();
    }

    private async void InviteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ClansGrid.SelectedItem is not ClanListItem clan)
        {
            StatusText.Text = "Select a clan first.";
            return;
        }

        bool ok = await RunCloudActionAsync(async () =>
        {
            await _supabase.InviteToClanAsync(clan.ClanId, InviteUsernameBox.Text);
            InviteUsernameBox.Text = string.Empty;
            return "Invite sent.";
        });
        if (ok) await RefreshAllAsync();
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCloudActionAsync(async () =>
        {
            int count = await _supabase.UploadLocalSummariesAsync(_localSummaries());
            return $"Uploaded {count} local session summaries.";
        });
        await RefreshDashboardForSelectedClanAsync();
    }

    private async void ClansGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => await RefreshDashboardForSelectedClanAsync();

    private async void InvitesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (InvitesGrid.SelectedItem is not ClanInviteItem invite) return;
        var answer = MessageBox.Show($"Accept invite to {invite.ClanName}?", "StrafeLab clan invite", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        bool accept = answer == MessageBoxResult.Yes;
        await RunCloudActionAsync(async () =>
        {
            await _supabase.RespondToInviteAsync(invite.InviteId, accept);
            return accept ? "Invite accepted." : "Invite declined.";
        });
        await RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        if (!_supabase.IsSignedIn)
        {
            SubtitleText.Text = "Sign in first from Account.";
            StatusText.Text = "Not signed in.";
            return;
        }

        await RunCloudActionAsync(async () =>
        {
            SubtitleText.Text = $"Signed in as {_supabase.DisplayName}.";
            ClansGrid.ItemsSource = await _supabase.GetMyClansAsync();
            InvitesGrid.ItemsSource = await _supabase.GetPendingInvitesAsync();
            return "Clan data refreshed.";
        });
        await RefreshDashboardForSelectedClanAsync();
    }

    private async Task RefreshDashboardForSelectedClanAsync()
    {
        if (ClansGrid.SelectedItem is not ClanListItem clan)
        {
            StatsGrid.ItemsSource = null;
            return;
        }

        await RunCloudActionAsync(async () =>
        {
            var rows = (await _supabase.GetClanDashboardAsync(clan.ClanId)).ToList();
            StatsGrid.ItemsSource = rows;
            TeamCoachingText.Text = BuildTeamCoaching(rows);
            return $"Showing stats for {clan.Name}.";
        });
    }


    private static string BuildTeamCoaching(IReadOnlyList<ClanDashboardRow> rows)
    {
        var active = rows.Where(r => r.Attempts > 0).ToList();
        if (active.Count == 0) return "No uploaded session summaries yet. Ask players to upload summaries after practice.";
        double moving = active.Average(r => r.MovingRate);
        double clean = active.Average(r => r.CleanRate);
        double avgCounter = active.Average(r => r.AverageCounterDelayMs);
        var mostMoving = active.OrderByDescending(r => r.MovingRate).First();
        var slowest = active.OrderByDescending(r => r.AverageCounterDelayMs).First();
        if (moving >= 25)
            return $"Team focus: moving shots. Average moving rate {moving:0}%. Start with 20 reps where every player must release A/D before M1. Highest moving rate: @{mostMoving.Username} ({mostMoving.MovingRate:0}%).";
        if (avgCounter > 80)
            return $"Team focus: slow counters. Average counter delay {avgCounter:0.0} ms. Run left/right rhythm reps. Slowest average: @{slowest.Username} ({slowest.AverageCounterDelayMs:0.0} ms).";
        if (clean < 55)
            return $"Team focus: clean consistency. Average clean rate {clean:0}%. Run 10-rep streak challenges and review the first mistake after each streak break.";
        return $"Team looks stable. Avg clean {clean:0}%, moving {moving:0}%, counter {avgCounter:0.0} ms. Next focus: reduce individual outliers and compare left/right bias.";
    }


    private void PlayerProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ClanDashboardRow row } && row.IsPublic && !string.IsNullOrWhiteSpace(row.Username))
        {
            ProfileRequested?.Invoke(row.Username);
        }
    }

    private async Task<bool> RunCloudActionAsync(Func<Task<string>> action)
    {
        try
        {
            StatusText.Text = "Working...";
            string result = await action();
            StatusText.Text = result;
            return !result.StartsWith("Enter ", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return false;
        }
    }
}
