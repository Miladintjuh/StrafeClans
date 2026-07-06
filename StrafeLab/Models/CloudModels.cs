namespace StrafeLab.Models;

public sealed class CloudUserProfile
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public bool PrivacyChoiceMade { get; set; }
    public bool IsAnonymous { get; set; }
    public bool ShareDevelopmentStats { get; set; }
}

public sealed class CloudProfileStats
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public bool PrivacyChoiceMade { get; set; }
    public bool IsAnonymous { get; set; }
    public bool ShareDevelopmentStats { get; set; }
    public int Sessions { get; set; }
    public int Attempts { get; set; }
    public int Clean { get; set; }
    public int Moving { get; set; }
    public int Overlap { get; set; }
    public int Slow { get; set; }
    public double CleanRate { get; set; }
    public double MovingRate { get; set; }
    public double AverageCounterDelayMs { get; set; }
    public double AverageClickDelayMs { get; set; }
    public DateTimeOffset? LastSessionAt { get; set; }
}

public sealed class ClanListItem
{
    public string ClanId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string OwnerUsername { get; set; } = string.Empty;
    public int Members { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ClanInviteItem
{
    public string InviteId { get; set; } = string.Empty;
    public string ClanId { get; set; } = string.Empty;
    public string ClanName { get; set; } = string.Empty;
    public string InvitedByUsername { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ClanDashboardRow
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public int Sessions { get; set; }
    public int Attempts { get; set; }
    public int Clean { get; set; }
    public int Moving { get; set; }
    public int Overlap { get; set; }
    public int Slow { get; set; }
    public double CleanRate { get; set; }
    public double MovingRate { get; set; }
    public double AverageCounterDelayMs { get; set; }
    public double AverageClickDelayMs { get; set; }
    public DateTimeOffset? LastSessionAt { get; set; }
}

public sealed class SupabaseLocalSession
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public bool PrivacyChoiceMade { get; set; }
    public bool IsAnonymous { get; set; }
    public bool ShareDevelopmentStats { get; set; } = true;
    public bool IsAdmin { get; set; }
    public bool IsModerator { get; set; }
    public bool CanViewAdmin { get; set; }
    public int ModCount { get; set; }
    public bool NeedsDemo { get; set; }

    public bool HasToken => !string.IsNullOrWhiteSpace(AccessToken);
    public bool CanRefresh => !string.IsNullOrWhiteSpace(RefreshToken);
    public bool IsProbablyExpired => ExpiresIn > 0 && DateTimeOffset.Now >= SavedAt.AddSeconds(Math.Max(30, ExpiresIn - 60));
}


public sealed class AdminGlobalStats
{
    public int Players { get; set; }
    public int AnonymousPlayers { get; set; }
    public int Sessions { get; set; }
    public int Attempts { get; set; }
    public int Clean { get; set; }
    public int Moving { get; set; }
    public int Overlap { get; set; }
    public int Slow { get; set; }
    public double CleanRate { get; set; }
    public double MovingRate { get; set; }
}

public sealed class AdminPlayerStats
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsAnonymous { get; set; }
    public bool IsPublic { get; set; }
    public int Sessions { get; set; }
    public int Attempts { get; set; }
    public int Clean { get; set; }
    public int Moving { get; set; }
    public int Overlap { get; set; }
    public int Slow { get; set; }
    public double CleanRate { get; set; }
    public double MovingRate { get; set; }
    public double AverageCounterDelayMs { get; set; }
    public double AverageClickDelayMs { get; set; }
    public DateTimeOffset? LastSessionAt { get; set; }
}


public sealed class ModeratorUser
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; set; }
}
