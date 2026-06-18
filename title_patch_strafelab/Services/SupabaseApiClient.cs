using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using StrafeLab.Models;

namespace StrafeLab.Services;

public sealed class SupabaseApiClient
{
    private const string SupabaseUrl = "https://vazqnkgqvxjngyuxcqio.supabase.co";
    private const string PublishableKey = "sb_publishable_32Dw1_xsWdqT3tPyg_2y9Q_NBQc2zqP";

    private readonly HttpClient _http = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _sessionFile;

    public SupabaseLocalSession? Session { get; private set; }
    public bool IsSignedIn => Session?.HasToken == true;
    public string DisplayName => string.IsNullOrWhiteSpace(Session?.Username)
        ? (string.IsNullOrWhiteSpace(Session?.Email) ? "Not signed in" : Session!.Email)
        : $"@{Session!.Username}";
    public bool IsAdmin => Session?.IsAdmin == true;
    public bool IsModerator => Session?.IsModerator == true;
    public bool CanViewAdmin => Session?.CanViewAdmin == true || IsAdmin || IsModerator;

    public SupabaseApiClient()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrafeLab");
        Directory.CreateDirectory(root);
        _sessionFile = Path.Combine(root, "supabase-session.json");
        LoadLocalSession();
    }

    public async Task<string> SignInAsync(string email, string password)
    {
        var payload = new { email = email.Trim(), password };
        using var doc = await SendJsonAsync(HttpMethod.Post, AuthUrl("/token?grant_type=password"), payload, requireAuth: false);
        SaveSessionFromAuthResponse(doc.RootElement);
        await LoadProfileAsync();
        SaveLocalSession();
        return $"Signed in as {DisplayName}.";
    }

    public async Task<string> SignUpAsync(string email, string password, string username)
    {
        username = NormalizeUsername(username);
        var payload = new
        {
            email = email.Trim(),
            password,
            data = new { username, display_name = username }
        };

        using var doc = await SendJsonAsync(HttpMethod.Post, AuthUrl("/signup"), payload, requireAuth: false);
        if (doc.RootElement.TryGetProperty("access_token", out var tokenEl) && tokenEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tokenEl.GetString()))
        {
            SaveSessionFromAuthResponse(doc.RootElement);
            await UpsertProfileAsync(username);
            SaveLocalSession();
            return $"Account created and signed in as @{username}.";
        }

        return "Account created. Check email verification if Supabase requires it, then sign in.";
    }

    public async Task<string> SignInAnonymouslyAsync(string displayName)
    {
        string username = NormalizeUsername(displayName);
        var payload = new
        {
            data = new
            {
                username,
                display_name = username,
                share_development_stats = true
            }
        };

        using var doc = await SendJsonAsync(HttpMethod.Post, AuthUrl("/signup"), payload, requireAuth: false);
        SaveSessionFromAuthResponse(doc.RootElement);
        if (Session == null || !Session.HasToken) throw new InvalidOperationException("Anonymous stats sharing could not be started. Make sure anonymous sign-ins are enabled in Supabase Auth settings.");
        Session.Username = username;
        Session.IsAnonymous = true;
        Session.ShareDevelopmentStats = true;
        Session.IsPublic = false;
        Session.PrivacyChoiceMade = false;
        await UpsertProfileRecordAsync(username, isPublic: false, privacyChoiceMade: false, isAnonymous: true, shareDevelopmentStats: true);
        SaveLocalSession();
        return $"Stats sharing is on for @{username}. You can stop sharing by signing out.";
    }

    public async Task<string> SignOutAsync()
    {
        try
        {
            if (IsSignedIn)
            {
                await SendJsonAsync(HttpMethod.Post, AuthUrl("/logout"), new { }, requireAuth: true);
            }
        }
        catch
        {
            // Local sign-out should still work if the token is already invalid.
        }

        Session = null;
        if (File.Exists(_sessionFile)) File.Delete(_sessionFile);
        return "Signed out.";
    }


    public void ForgetLocalSession()
    {
        Session = null;
        try
        {
            if (File.Exists(_sessionFile)) File.Delete(_sessionFile);
        }
        catch
        {
            // Local cleanup should never block local-only use.
        }
    }

    public async Task<bool> TryRefreshOrClearAsync()
    {
        if (Session == null) return false;
        if (!Session.IsProbablyExpired) return true;
        if (!Session.CanRefresh)
        {
            Session = null;
            return false;
        }

        try
        {
            using var doc = await SendJsonAsync(HttpMethod.Post, AuthUrl("/token?grant_type=refresh_token"), new { refresh_token = Session.RefreshToken }, requireAuth: false);
            SaveSessionFromAuthResponse(doc.RootElement);
            await LoadProfileAsync();
            SaveLocalSession();
            return true;
        }
        catch
        {
            Session = null;
            if (File.Exists(_sessionFile)) File.Delete(_sessionFile);
            return false;
        }
    }

    public async Task UpsertProfileAsync(string username)
    {
        await EnsureAuthenticatedAsync();
        var session = Session ?? throw new InvalidOperationException("You need to sign in before updating a profile.");
        username = NormalizeUsername(username);
        await UpsertProfileRecordAsync(username, session.IsPublic, session.PrivacyChoiceMade, session.IsAnonymous, session.ShareDevelopmentStats);
        session.Username = username;
        SaveLocalSession();
    }

    public async Task LoadProfileAsync()
    {
        await EnsureProfileAsync();
    }

    public async Task EnsureProfileAsync()
    {
        await EnsureAuthenticatedAsync();
        var session = Session ?? throw new InvalidOperationException("Sign in first.");

        using (var doc = await SendJsonAsync(HttpMethod.Get, RestUrl($"/profiles?id=eq.{Uri.EscapeDataString(session.UserId)}&select=id,username,display_name,profile_public,privacy_choice_made,is_anonymous,share_development_stats"), null, requireAuth: true))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                ApplyProfileToSession(doc.RootElement[0]);
                await RefreshAdminFlagAsync();
                SaveLocalSession();
                return;
            }
        }

        string username = MakeDefaultUsername(session);
        await UpsertProfileRecordAsync(username, session.IsPublic, session.PrivacyChoiceMade, session.IsAnonymous, session.ShareDevelopmentStats);

        using var created = await SendJsonAsync(HttpMethod.Get, RestUrl($"/profiles?id=eq.{Uri.EscapeDataString(session.UserId)}&select=id,username,display_name,profile_public,privacy_choice_made,is_anonymous,share_development_stats"), null, requireAuth: true);
        if (created.RootElement.ValueKind == JsonValueKind.Array && created.RootElement.GetArrayLength() > 0)
        {
            ApplyProfileToSession(created.RootElement[0]);
            await RefreshAdminFlagAsync();
            SaveLocalSession();
        }
    }

    public async Task<bool> RefreshAdminFlagAsync() => await RefreshAdminAccessAsync();

    public async Task<bool> RefreshAdminAccessAsync()
    {
        await EnsureAuthenticatedAsync();
        using var doc = await RpcAsync("current_user_admin_access", new { });
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];

        bool isAdmin = GetBool(root, "is_admin");
        bool isModerator = GetBool(root, "is_mod");
        bool canViewAdmin = GetBool(root, "can_view_admin") || isAdmin || isModerator;
        int modCount = GetInt(root, "mod_count");

        if (Session != null)
        {
            Session.IsAdmin = isAdmin;
            Session.IsModerator = isModerator;
            Session.CanViewAdmin = canViewAdmin;
            Session.ModCount = modCount;
            SaveLocalSession();
        }
        return canViewAdmin;
    }

    public async Task<CloudProfileStats> GetMyProfileStatsAsync()
    {
        await EnsureProfileAsync();
        using var doc = await RpcAsync("my_profile_stats", new { });
        return ParseProfileStats(doc.RootElement);
    }

    public async Task<CloudProfileStats> GetPublicProfileStatsAsync(string username)
    {
        await EnsureAuthenticatedAsync();
        using var doc = await RpcAsync("public_profile_stats", new { p_username = NormalizeUsername(username) });
        return ParseProfileStats(doc.RootElement);
    }

    public async Task SetProfileVisibilityAsync(bool isPublic)
    {
        await EnsureProfileAsync();
        var body = new
        {
            profile_public = isPublic,
            privacy_choice_made = true,
            updated_at = DateTimeOffset.UtcNow
        };
        using var _ = await SendJsonAsync(HttpMethod.Patch, RestUrl($"/profiles?id=eq.{Uri.EscapeDataString(Session!.UserId)}&select=id,profile_public,privacy_choice_made"), body, requireAuth: true, prefer: "return=representation");
        Session.IsPublic = isPublic;
        Session.PrivacyChoiceMade = true;
        SaveLocalSession();
    }

    public async Task<string> CreateClanAsync(string name)
    {
        await EnsureProfileAsync();
        using var doc = await RpcAsync("create_clan", new { p_name = name.Trim() });
        return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() ?? string.Empty : doc.RootElement.ToString();
    }

    public async Task InviteToClanAsync(string clanId, string username)
    {
        await EnsureAuthenticatedAsync();
        using var _ = await RpcAsync("invite_to_clan", new { p_clan_id = clanId, p_username = NormalizeUsername(username) });
    }

    public async Task RespondToInviteAsync(string inviteId, bool accept)
    {
        await EnsureAuthenticatedAsync();
        using var _ = await RpcAsync("respond_clan_invite", new { p_invite_id = inviteId, p_accept = accept });
    }

    public async Task<IReadOnlyList<ClanListItem>> GetMyClansAsync()
    {
        await EnsureProfileAsync();
        using var doc = await RpcAsync("my_clans", new { });
        var rows = new List<ClanListItem>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            rows.Add(new ClanListItem
            {
                ClanId = GetString(row, "clan_id"),
                Name = GetString(row, "name"),
                Role = GetString(row, "role"),
                OwnerUsername = GetString(row, "owner_username"),
                Members = GetInt(row, "members"),
                CreatedAt = GetDate(row, "created_at") ?? default
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<ClanInviteItem>> GetPendingInvitesAsync()
    {
        await EnsureProfileAsync();
        using var doc = await RpcAsync("my_pending_invites", new { });
        var rows = new List<ClanInviteItem>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            rows.Add(new ClanInviteItem
            {
                InviteId = GetString(row, "invite_id"),
                ClanId = GetString(row, "clan_id"),
                ClanName = GetString(row, "clan_name"),
                InvitedByUsername = GetString(row, "invited_by_username"),
                CreatedAt = GetDate(row, "created_at") ?? default
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<ClanDashboardRow>> GetClanDashboardAsync(string clanId)
    {
        await EnsureAuthenticatedAsync();
        using var doc = await RpcAsync("clan_dashboard", new { p_clan_id = clanId });
        var rows = new List<ClanDashboardRow>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            rows.Add(new ClanDashboardRow
            {
                UserId = GetString(row, "user_id"),
                Username = GetString(row, "username"),
                IsPublic = GetBool(row, "profile_public"),
                Sessions = GetInt(row, "sessions"),
                Attempts = GetInt(row, "attempts"),
                Clean = GetInt(row, "clean"),
                Moving = GetInt(row, "moving"),
                Overlap = GetInt(row, "overlap"),
                Slow = GetInt(row, "slow"),
                CleanRate = GetDouble(row, "clean_rate"),
                MovingRate = GetDouble(row, "moving_rate"),
                AverageCounterDelayMs = GetDouble(row, "avg_counter_delay_ms"),
                AverageClickDelayMs = GetDouble(row, "avg_click_delay_ms"),
                LastSessionAt = GetDate(row, "last_session_at")
            });
        }
        return rows;
    }

    public async Task<AdminGlobalStats> GetAdminGlobalStatsAsync()
    {
        await EnsureAuthenticatedAsync();
        using var doc = await RpcAsync("admin_global_stats", new { });
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
        return new AdminGlobalStats
        {
            Players = GetInt(root, "players"),
            AnonymousPlayers = GetInt(root, "anonymous_players"),
            Sessions = GetInt(root, "sessions"),
            Attempts = GetInt(root, "attempts"),
            Clean = GetInt(root, "clean"),
            Moving = GetInt(root, "moving"),
            Overlap = GetInt(root, "overlap"),
            Slow = GetInt(root, "slow"),
            CleanRate = GetDouble(root, "clean_rate"),
            MovingRate = GetDouble(root, "moving_rate")
        };
    }

    public async Task<IReadOnlyList<AdminPlayerStats>> GetAdminPlayerStatsAsync()
    {
        await EnsureAuthenticatedAsync();
        using var doc = await RpcAsync("admin_player_stats", new { });
        var rows = new List<AdminPlayerStats>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            rows.Add(new AdminPlayerStats
            {
                UserId = GetString(row, "user_id"),
                Username = GetString(row, "username"),
                IsAnonymous = GetBool(row, "is_anonymous"),
                IsPublic = GetBool(row, "profile_public"),
                Sessions = GetInt(row, "sessions"),
                Attempts = GetInt(row, "attempts"),
                Clean = GetInt(row, "clean"),
                Moving = GetInt(row, "moving"),
                Overlap = GetInt(row, "overlap"),
                Slow = GetInt(row, "slow"),
                CleanRate = GetDouble(row, "clean_rate"),
                MovingRate = GetDouble(row, "moving_rate"),
                AverageCounterDelayMs = GetDouble(row, "avg_counter_delay_ms"),
                AverageClickDelayMs = GetDouble(row, "avg_click_delay_ms"),
                LastSessionAt = GetDate(row, "last_session_at")
            });
        }
        return rows;
    }

    public async Task<int> GetModeratorCountAsync()
    {
        await EnsureAuthenticatedAsync();
        using var doc = await RpcAsync("moderator_count", new { });
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Number && root.TryGetInt32(out int value)) return value;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Number && root[0].TryGetInt32(out value)) return value;
        if (root.ValueKind == JsonValueKind.Object)
        {
            int named = GetInt(root, "moderator_count");
            if (named > 0) return named;
            named = GetInt(root, "count");
            if (named > 0) return named;
        }
        return 0;
    }

    public async Task<IReadOnlyList<ModeratorUser>> GetModeratorsAsync()
    {
        await EnsureAuthenticatedAsync();
        using var doc = await RpcAsync("list_moderators", new { });
        return ParseModeratorRows(doc.RootElement, includeCreatedAt: true);
    }

    public async Task<IReadOnlyList<ModeratorUser>> SearchModeratorCandidatesAsync(string query)
    {
        await EnsureAuthenticatedAsync();
        using var doc = await RpcAsync("search_moderator_candidates", new { p_query = query?.Trim() ?? string.Empty });
        return ParseModeratorRows(doc.RootElement, includeCreatedAt: false);
    }

    public async Task AddModeratorAsync(string userId)
    {
        await EnsureAuthenticatedAsync();
        using var _ = await RpcAsync("add_moderator", new { p_user_id = userId });
        await RefreshAdminAccessAsync();
    }

    public async Task RemoveModeratorAsync(string userId)
    {
        await EnsureAuthenticatedAsync();
        using var _ = await RpcAsync("remove_moderator", new { p_user_id = userId });
        await RefreshAdminAccessAsync();
    }

    public async Task UploadSessionSummaryAsync(SessionSummary summary, string mode = "trainer")
    {
        await EnsureProfileAsync();
        if (Session?.IsAnonymous == true && !Session.ShareDevelopmentStats) return;
        var raw = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(summary, _json));
        var body = new[]
        {
            new
            {
                user_id = Session!.UserId,
                session_id = summary.SessionId,
                started_at = summary.StartedAt,
                ended_at = summary.EndedAt,
                mode,
                attempts = summary.Attempts,
                clean = summary.Perfect,
                moving = summary.MovingAtClick,
                overlap = summary.Overlap,
                slow = summary.Late,
                early = summary.EarlyClick,
                avg_counter_delay_ms = summary.AverageCounterDelayMs,
                avg_click_delay_ms = summary.AverageClickFromCounterMs,
                raw
            }
        };

        await SendJsonAsync(HttpMethod.Post, RestUrl("/stat_sessions?on_conflict=user_id,session_id"), body, requireAuth: true, prefer: "resolution=merge-duplicates,return=minimal");
    }

    public async Task<int> UploadLocalSummariesAsync(IEnumerable<SessionSummary> summaries)
    {
        int uploaded = 0;
        foreach (var summary in summaries.OrderBy(s => s.StartedAt))
        {
            await UploadSessionSummaryAsync(summary);
            uploaded++;
        }
        return uploaded;
    }


    private async Task UpsertProfileRecordAsync(string username, bool isPublic, bool privacyChoiceMade, bool isAnonymous, bool shareDevelopmentStats)
    {
        var session = Session ?? throw new InvalidOperationException("Sign in first.");
        var body = new[]
        {
            new
            {
                id = session.UserId,
                username,
                display_name = username,
                profile_public = isPublic,
                privacy_choice_made = privacyChoiceMade,
                is_anonymous = isAnonymous,
                share_development_stats = shareDevelopmentStats,
                updated_at = DateTimeOffset.UtcNow
            }
        };

        using var _ = await SendJsonAsync(HttpMethod.Post, RestUrl("/profiles?on_conflict=id&select=id,username,display_name,profile_public,privacy_choice_made,is_anonymous,share_development_stats"), body, requireAuth: true, prefer: "resolution=merge-duplicates,return=representation");
    }

    private void ApplyProfileToSession(JsonElement profile)
    {
        if (Session == null) return;
        Session.Username = GetString(profile, "username");
        Session.IsPublic = GetBool(profile, "profile_public");
        Session.PrivacyChoiceMade = GetBool(profile, "privacy_choice_made");
        Session.IsAnonymous = GetBool(profile, "is_anonymous");
        Session.ShareDevelopmentStats = GetBool(profile, "share_development_stats");
    }

    private static string MakeDefaultUsername(SupabaseLocalSession session)
    {
        string source = string.IsNullOrWhiteSpace(session.Email) ? "player" : session.Email.Split('@')[0];
        var chars = source.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '.' ? c : '_').ToArray();
        string baseName = new string(chars).Trim('.', '_');
        if (baseName.Length < 3) baseName = "player";
        if (baseName.Length > 20) baseName = baseName[..20];
        string suffix = new string(session.UserId.Where(char.IsLetterOrDigit).TakeLast(6).ToArray()).ToLowerInvariant();
        if (suffix.Length < 3) suffix = Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
        return NormalizeUsername($"{baseName}.{suffix}");
    }

    private async Task<JsonDocument> RpcAsync(string name, object payload)
        => await SendJsonAsync(HttpMethod.Post, RestUrl($"/rpc/{name}"), payload, requireAuth: true);

    private async Task EnsureAuthenticatedAsync()
    {
        if (!await TryRefreshOrClearAsync()) throw new InvalidOperationException("Sign in first.");
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, object? payload, bool requireAuth, string? prefer = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("apikey", PublishableKey);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(prefer)) request.Headers.TryAddWithoutValidation("Prefer", prefer);
        if (requireAuth)
        {
            if (Session?.HasToken != true) throw new InvalidOperationException("Sign in first.");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Session.AccessToken}");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {PublishableKey}");
        }

        if (payload != null)
        {
            string json = JsonSerializer.Serialize(payload, _json);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string message = TryExtractMessage(text);
            throw new InvalidOperationException($"Supabase {(int)response.StatusCode} {response.StatusCode}: {message}");
        }

        if (string.IsNullOrWhiteSpace(text)) text = "{}";
        return JsonDocument.Parse(text);
    }

    private void SaveSessionFromAuthResponse(JsonElement root)
    {
        var user = root.TryGetProperty("user", out var userEl) ? userEl : default;
        Session = new SupabaseLocalSession
        {
            AccessToken = GetString(root, "access_token"),
            RefreshToken = GetString(root, "refresh_token"),
            ExpiresIn = GetInt(root, "expires_in"),
            SavedAt = DateTimeOffset.Now,
            UserId = user.ValueKind == JsonValueKind.Object ? GetString(user, "id") : Session?.UserId ?? string.Empty,
            Email = user.ValueKind == JsonValueKind.Object ? GetString(user, "email") : Session?.Email ?? string.Empty,
            Username = Session?.Username ?? string.Empty,
            IsPublic = Session?.IsPublic ?? false,
            PrivacyChoiceMade = Session?.PrivacyChoiceMade ?? false,
            IsAnonymous = user.ValueKind == JsonValueKind.Object ? GetBool(user, "is_anonymous") || (Session?.IsAnonymous ?? false) : Session?.IsAnonymous ?? false,
            ShareDevelopmentStats = Session?.ShareDevelopmentStats ?? true,
            IsAdmin = Session?.IsAdmin ?? false,
            IsModerator = Session?.IsModerator ?? false,
            CanViewAdmin = Session?.CanViewAdmin ?? false,
            ModCount = Session?.ModCount ?? 0
        };
    }

    private void LoadLocalSession()
    {
        try
        {
            if (!File.Exists(_sessionFile)) return;
            var session = JsonSerializer.Deserialize<SupabaseLocalSession>(File.ReadAllText(_sessionFile));
            if (session?.HasToken == true) Session = session;
        }
        catch
        {
            Session = null;
        }
    }

    private void SaveLocalSession()
    {
        if (Session == null) return;
        File.WriteAllText(_sessionFile, JsonSerializer.Serialize(Session, _json));
    }

    private static string NormalizeUsername(string username)
    {
        string normalized = username.Trim().TrimStart('@').ToLowerInvariant();
        if (normalized.Length < 3) throw new InvalidOperationException("Username must be at least 3 characters.");
        if (normalized.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '.'))) throw new InvalidOperationException("Username can only use letters, numbers, underscore, and dot.");
        return normalized;
    }

    private static string AuthUrl(string path) => SupabaseUrl.TrimEnd('/') + "/auth/v1" + path;
    private static string RestUrl(string path) => SupabaseUrl.TrimEnd('/') + "/rest/v1" + path;

    private static IReadOnlyList<ModeratorUser> ParseModeratorRows(JsonElement root, bool includeCreatedAt)
    {
        var rows = new List<ModeratorUser>();
        if (root.ValueKind != JsonValueKind.Array) return rows;
        foreach (var row in root.EnumerateArray())
        {
            rows.Add(new ModeratorUser
            {
                UserId = GetString(row, "user_id"),
                Username = GetString(row, "username"),
                Email = GetString(row, "email"),
                CreatedAt = includeCreatedAt ? GetDate(row, "created_at") : null
            });
        }
        return rows;
    }

    private static bool ParseBooleanPayload(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.True) return true;
        if (root.ValueKind == JsonValueKind.False || root.ValueKind == JsonValueKind.Null || root.ValueKind == JsonValueKind.Undefined) return false;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) return ParseBooleanPayload(root[0]);
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("current_user_is_admin", out var a)) return ParseBooleanPayload(a);
            if (root.TryGetProperty("is_admin", out var b)) return ParseBooleanPayload(b);
        }
        return bool.TryParse(root.ToString(), out var value) && value;
    }

    private static CloudProfileStats ParseProfileStats(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
        if (root.ValueKind != JsonValueKind.Object) return new CloudProfileStats();
        return new CloudProfileStats
        {
            UserId = GetString(root, "user_id"),
            Username = GetString(root, "username"),
            DisplayName = GetString(root, "display_name"),
            IsPublic = GetBool(root, "profile_public"),
            PrivacyChoiceMade = GetBool(root, "privacy_choice_made"),
            IsAnonymous = GetBool(root, "is_anonymous"),
            ShareDevelopmentStats = GetBool(root, "share_development_stats"),
            Sessions = GetInt(root, "sessions"),
            Attempts = GetInt(root, "attempts"),
            Clean = GetInt(root, "clean"),
            Moving = GetInt(root, "moving"),
            Overlap = GetInt(root, "overlap"),
            Slow = GetInt(root, "slow"),
            CleanRate = GetDouble(root, "clean_rate"),
            MovingRate = GetDouble(root, "moving_rate"),
            AverageCounterDelayMs = GetDouble(root, "avg_counter_delay_ms"),
            AverageClickDelayMs = GetDouble(root, "avg_click_delay_ms"),
            LastSessionAt = GetDate(root, "last_session_at")
        };
    }

    private static bool GetBool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False || v.ValueKind == JsonValueKind.Null) return false;
        return bool.TryParse(v.ToString(), out bool b) && b;
    }

    private static string GetString(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : string.Empty;

    private static int GetInt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        return int.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out i) ? i : 0;
    }

    private static double GetDouble(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
        return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    private static DateTimeOffset? GetDate(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return DateTimeOffset.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }

    private static string TryExtractMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string msg = GetString(root, "message");
            if (!string.IsNullOrWhiteSpace(msg)) return msg;
            string error = GetString(root, "error_description");
            if (!string.IsNullOrWhiteSpace(error)) return error;
            error = GetString(root, "error");
            if (!string.IsNullOrWhiteSpace(error)) return error;
        }
        catch { }
        return string.IsNullOrWhiteSpace(text) ? "No response body." : text;
    }
}
