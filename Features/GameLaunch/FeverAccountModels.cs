namespace IDVBuff.Features.GameLaunch;

/// <summary>
/// Represents a saved NetEase Fever official account profile.
/// </summary>
public sealed class FeverAccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? AccountIdentifier { get; set; }
    public bool IsLongTerm { get; set; }
    public string? LoginType { get; set; }
    public string? FeverToken { get; set; }
    public string? FeverSdkuid { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastRefreshedAt { get; set; }
}

/// <summary>
/// Root metadata document for saved Fever accounts.
/// </summary>
public sealed class FeverAccountsMetadata
{
    public bool HasInitialized { get; set; }
    public string? ActiveAccountId { get; set; }
    public List<FeverAccountProfile> Accounts { get; set; } = [];
}
