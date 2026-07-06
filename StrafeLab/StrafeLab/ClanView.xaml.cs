using System.IO;
using System.Text.Json;
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
    private IReadOnlyList<CloudProfileStats> _publicProfiles = Array.Empty<CloudProfileStats>();

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

    private async void SearchPersonalButton_Click(object sender, RoutedEventArgs e) => await RefreshPersonalRankingsAsync();
    private async void ClearPersonalSearchButton_Click(object sender, RoutedEventArgs e)
    {
        PersonalSearchBox.Text = string.Empty;
        await RefreshPersonalRankingsAsync();
    }

    private void PersonalSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyPersonalSearchFilter();

    private void PersonalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PersonalGrid.SelectedItem is CloudProfileStats row && row.IsPublic && !string.IsNullOrWhiteSpace(row.Username))
        {
            ProfileRequested?.Invoke(row.Username);
        }
    }

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

        bool syncExisting = true;
        if (accept)
        {
            var syncAnswer = MessageBox.Show(
                "Do you want to sync your existing local session summaries with this clan?\n\n" +
                "Yes = upload existing summaries so clan stats include your past training.\n" +
                "No = save a pre-clan snapshot locally and start this clan season from now.",
                "Clan stat sync",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (syncAnswer == MessageBoxResult.Cancel) return;
            syncExisting = syncAnswer == MessageBoxResult.Yes;
            if (!syncExisting) SavePreClanSnapshot(invite.ClanName);
        }

        await RunCloudActionAsync(async () =>
        {
            await _supabase.RespondToInviteAsync(invite.InviteId, accept);
            if (accept && syncExisting)
            {
                int count = await _supabase.UploadLocalSummariesAsync(_localSummaries());
                return $"Invite accepted. Uploaded {count} existing summaries to clan stats.";
            }
            if (accept) return "Invite accepted. Pre-clan snapshot saved locally; future uploads count as the new clan season.";
            return "Invite declined.";
        });
        await RefreshAllAsync();
    }


    private void SavePreClanSnapshot(string clanName)
    {
        var summaries = _localSummaries();
        var snapshot = new
        {
            clanName,
            createdAt = DateTimeOffset.Now,
            sessions = summaries.Count,
            attempts = summaries.Sum(s => s.Attempts),
            cleanRate = summaries.Sum(s => s.Attempts) == 0 ? 0 : summaries.Sum(s => s.Perfect) * 100.0 / summaries.Sum(s => s.Attempts),
            movingRate = summaries.Sum(s => s.Attempts) == 0 ? 0 : summaries.Sum(s => s.MovingAtClick) * 100.0 / summaries.Sum(s => s.Attempts),
            avgCounterDelayMs = summaries.Count == 0 ? 0 : summaries.Average(s => s.AverageCounterDelayMs),
            note = "Non-destructive pre-clan baseline. Source sessions remain local; uploads after joining represent the current clan season."
        };
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrafeLab");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "clan-stat-snapshots.jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(snapshot) + Environment.NewLine);
    }

    private async Task RefreshAllAsync()
    {
        if (!_supabase.IsSignedIn)
        {
            SubtitleText.Text = "Sign in first from Account.";
            StatusText.Text = "Not signed in.";
            PersonalStatusText.Text = "Sign in to view public rankings.";
            PersonalGrid.ItemsSource = null;
            return;
        }

        await RefreshPersonalRankingsAsync();

        await RunCloudActionAsync(async () =>
        {
            SubtitleText.Text = $"Signed in as {_supabase.DisplayName}.";
            ClansGrid.ItemsSource = await _supabase.GetMyClansAsync();
            InvitesGrid.ItemsSource = await _supabase.GetPendingInvitesAsync();
            return "Rankings refreshed.";
        });
        await RefreshDashboardForSelectedClanAsync();
    }

    private async Task RefreshPersonalRankingsAsync()
    {
        await RunCloudActionAsync(async () =>
        {
            _publicProfiles = await _supabase.GetPublicLeaderboardAsync(PersonalSearchBox.Text);
            ApplyPersonalSearchFilter();
            return $"Loaded {_publicProfiles.Count} public profiles.";
        });
    }

    private void ApplyPersonalSearchFilter()
    {
        string query = (PersonalSearchBox.Text ?? string.Empty).Trim().TrimStart('@');
        IEnumerable<CloudProfileStats> rows = _publicProfiles;
        if (!string.IsNullOrWhiteSpace(query))
        {
            rows = rows.Where(r => r.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        var ordered = rows.OrderByDescending(r => r.Attempts >= 20)
                          .ThenByDescending(r => r.CleanRate)
                          .ThenByDescending(r => r.Attempts)
                          .ThenBy(r => r.MovingRate)
                          .ToList();
        PersonalGrid.ItemsSource = ordered;
        PersonalStatusText.Text = ordered.Count == 0
            ? "No public profiles found. Users appear here only after they manually make their profile public."
            : $"Showing {ordered.Count} public profile(s). Double-click a row to open the public profile.";
    }

    private async Task RefreshDashboardForSelectedClanAsync()
    {
        if (ClansGrid.SelectedItem is not ClanListItem clan)
        {
            StatsGrid.ItemsSource = null;
            UpdateClanSummary(Array.Empty<ClanDashboardRow>());
            TeamCoachingText.Text = "Select a clan to see the team practice focus.";
            return;
        }

        await RunCloudActionAsync(async () =>
        {
            var rows = (await _supabase.GetClanDashboardAsync(clan.ClanId)).ToList();
            StatsGrid.ItemsSource = rows;
            UpdateClanSummary(rows);
            TeamCoachingText.Text = BuildTeamCoaching(rows);
            return $"Showing stats for {clan.Name}.";
        });
    }

    private void UpdateClanSummary(IReadOnlyList<ClanDashboardRow> rows)
    {
        var active = rows.Where(r => r.Attempts > 0).ToList();
        ClanMembersText.Text = rows.Count.ToString();
        ClanCleanText.Text = active.Count == 0 ? "0%" : active.Average(r => r.CleanRate).ToString("0") + "%";
        ClanMovingText.Text = active.Count == 0 ? "0%" : active.Average(r => r.MovingRate).ToString("0") + "%";
        var mvp = active.OrderByDescending(r => r.CleanRate).ThenByDescending(r => r.Attempts).FirstOrDefault();
        ClanMvpText.Text = mvp == null ? "—" : "@" + mvp.Username;
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
            StatusText.Text = ToCleanError(ex.Message);
            return false;
        }
    }
    private static string ToCleanError(string raw)
    {
        string text = raw ?? string.Empty;
        string lower = text.ToLowerInvariant();
        if (lower.Contains("username not found")) return "That username was not found. Ask the player to open Profile and copy their exact username.";
        if (lower.Contains("already") && lower.Contains("member")) return "That player is already in this clan.";
        if (lower.Contains("permission") || lower.Contains("rls") || lower.Contains("not allowed")) return "Clan action was blocked by Supabase permissions. Make sure you are the clan owner and the SQL schema is up to date.";
        if (text.StartsWith("Supabase", StringComparison.OrdinalIgnoreCase)) return "Cloud action failed. " + text;
        return string.IsNullOrWhiteSpace(text) ? "Cloud action failed." : text;
    }
}
