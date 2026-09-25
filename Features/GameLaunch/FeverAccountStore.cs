using System.Diagnostics;
using System.Text.Json;

namespace IDVBuff.Features.GameLaunch;

/// <summary>
/// Manages persistent profiles for NetEase Fever official accounts,
/// supporting both temporary QR scan sessions and long-term Fever platform sessions.
/// </summary>
public sealed partial class FeverAccountStore
{
    private static readonly Lazy<FeverAccountStore> DefaultInstance = new(() => new FeverAccountStore());
    public static FeverAccountStore Instance => DefaultInstance.Value;

    private readonly string _storageDirectory;
    private readonly string _unisdkDirectory;
    private readonly string _neteaseMpayDirectory;
    private readonly string _backupTempDirectory;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public FeverAccountStore(
        string? customStorageDir = null,
        string? customUnisdkDir = null,
        string? customNeteaseMpayDir = null)
    {
        _storageDirectory = customStorageDir ?? Path.Combine(AppDataPaths.RootDirectory, "FeverAccounts");
        _unisdkDirectory = customUnisdkDir ?? FeverEnvironmentResolver.GetUnisdkDirectory();
        _neteaseMpayDirectory = customNeteaseMpayDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Netease", "Mpay");
        _backupTempDirectory = Path.Combine(_storageDirectory, "backup_temp");

        Directory.CreateDirectory(_storageDirectory);
        Directory.CreateDirectory(Path.Combine(_storageDirectory, "profiles"));
    }

    public string StorageDirectory => _storageDirectory;
    public string UnisdkDirectory => _unisdkDirectory;
    public string NeteaseMpayDirectory => _neteaseMpayDirectory;

    public FeverAccountsMetadata LoadMetadata()
    {
        lock (_lock)
        {
            var metaFile = Path.Combine(_storageDirectory, "accounts.json");
            if (!File.Exists(metaFile))
                return new FeverAccountsMetadata();

            try
            {
                var json = File.ReadAllText(metaFile);
                return JsonSerializer.Deserialize<FeverAccountsMetadata>(json, JsonOptions) ?? new FeverAccountsMetadata();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FeverAccountStore] Failed to read accounts.json: {ex.Message}");
                return new FeverAccountsMetadata();
            }
        }
    }

    public void SaveMetadata(FeverAccountsMetadata metadata)
    {
        lock (_lock)
        {
            var metaFile = Path.Combine(_storageDirectory, "accounts.json");
            var json = JsonSerializer.Serialize(metadata, JsonOptions);
            File.WriteAllText(metaFile, json);
        }
    }

    public IReadOnlyList<FeverAccountProfile> GetAccounts() => LoadMetadata().Accounts;

    public void ImportUnindexedMpaySessions()
    {
        // MPay database filenames name the game cache, not a signed-in account.
    }

    public void AutoImportExistingSessionIfEmpty()
    {
        lock (_lock)
        {
            var meta = LoadMetadata();
            if (meta.HasInitialized)
            {
                return;
            }

            // 1. Try import from official Fever launcher credentials in Windows Registry
            try
            {
                var (feverToken, sdkuid) = ReadFeverRegistryCredentials();
                if (!string.IsNullOrWhiteSpace(feverToken))
                {
                    var parsed = TryParseFeverToken(feverToken);
                    var profile = new FeverAccountProfile
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = parsed.DisplayName ?? "网易账号 1",
                        AccountIdentifier = parsed.Identifier,
                        IsLongTerm = true,
                        LoginType = "FeverLongTerm",
                        FeverToken = feverToken,
                        FeverSdkuid = sdkuid,
                        CreatedAt = DateTimeOffset.Now,
                        LastUsedAt = DateTimeOffset.Now,
                        LastRefreshedAt = DateTimeOffset.Now
                    };

                    meta.Accounts.Add(profile);
                    meta.ActiveAccountId = profile.Id;
                    meta.HasInitialized = true;
                    SaveMetadata(meta);

                    if (!string.IsNullOrWhiteSpace(parsed.Token))
                    {
                        try
                        {
                            var profileDir = Path.Combine(_storageDirectory, "profiles", profile.Id);
                            Directory.CreateDirectory(profileDir);
                            File.WriteAllText(Path.Combine(profileDir, "ticket.txt"), parsed.Token.Trim());
                        }
                        catch { }
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FeverAccountStore] Auto-import Fever registry failed: {ex.Message}");
            }

            meta.HasInitialized = true;
            SaveMetadata(meta);
        }
    }

    /// <summary>
    /// Captures the currently active Fever account from registry and adds or updates it in the store.
    /// </summary>
    public FeverAccountProfile? CaptureCurrentFeverRegistryAccount(string? customName = null)
    {
        lock (_lock)
        {
            var (feverToken, sdkuid) = ReadFeverRegistryCredentials();
            if (string.IsNullOrWhiteSpace(feverToken)) return null;

            var parsed = TryParseFeverToken(feverToken);
            var meta = LoadMetadata();

            var existing = !string.IsNullOrEmpty(parsed.Identifier)
                ? meta.Accounts.FirstOrDefault(a => a.IsLongTerm && string.Equals(a.AccountIdentifier, parsed.Identifier, StringComparison.OrdinalIgnoreCase))
                : meta.Accounts.FirstOrDefault(a => a.IsLongTerm && string.Equals(a.FeverToken, feverToken, StringComparison.Ordinal));

            FeverAccountProfile profile;
            if (existing is not null)
            {
                profile = existing;
                profile.FeverToken = feverToken;
                profile.FeverSdkuid = sdkuid;
                profile.LastUsedAt = DateTimeOffset.Now;
                profile.LastRefreshedAt = DateTimeOffset.Now;
                if (!string.IsNullOrWhiteSpace(customName))
                    profile.Name = customName.Trim();
            }
            else
            {
                var displayName = !string.IsNullOrWhiteSpace(customName)
                    ? customName.Trim()
                    : parsed.DisplayName ?? $"网易账号 {meta.Accounts.Count(a => a.IsLongTerm) + 1}";

                profile = new FeverAccountProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = displayName,
                    AccountIdentifier = parsed.Identifier,
                    IsLongTerm = true,
                    LoginType = "FeverLongTerm",
                    FeverToken = feverToken,
                    FeverSdkuid = sdkuid,
                    CreatedAt = DateTimeOffset.Now,
                    LastUsedAt = DateTimeOffset.Now,
                    LastRefreshedAt = DateTimeOffset.Now
                };
                meta.Accounts.Add(profile);
            }

            meta.ActiveAccountId = profile.Id;
            meta.HasInitialized = true;
            SaveMetadata(meta);

            // Persist parsed ticket in profile directory
            if (!string.IsNullOrWhiteSpace(parsed.Token))
            {
                try
                {
                    var profileDir = Path.Combine(_storageDirectory, "profiles", profile.Id);
                    Directory.CreateDirectory(profileDir);
                    File.WriteAllText(Path.Combine(profileDir, "ticket.txt"), parsed.Token.Trim());
                }
                catch { }
            }

            return profile;
        }
    }

    public FeverAccountProfile? GetActiveAccount()
    {
        var meta = LoadMetadata();
        if (string.IsNullOrEmpty(meta.ActiveAccountId))
            return null;

        return meta.Accounts.FirstOrDefault(a => string.Equals(a.Id, meta.ActiveAccountId, StringComparison.OrdinalIgnoreCase));
    }

    public bool RenameAccount(string accountId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;

        lock (_lock)
        {
            var meta = LoadMetadata();
            var target = meta.Accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (target is null) return false;

            target.Name = newName.Trim();
            SaveMetadata(meta);
            return true;
        }
    }

    public bool DeleteAccount(string accountId)
    {
        lock (_lock)
        {
            var meta = LoadMetadata();
            var target = meta.Accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (target is null) return false;

            meta.Accounts.Remove(target);
            if (string.Equals(meta.ActiveAccountId, accountId, StringComparison.OrdinalIgnoreCase))
            {
                meta.ActiveAccountId = meta.Accounts.FirstOrDefault()?.Id;
            }

            SaveMetadata(meta);

            // Clean up profile folder
            var profileDir = Path.Combine(_storageDirectory, "profiles", accountId);
            try
            {
                if (Directory.Exists(profileDir))
                    Directory.Delete(profileDir, recursive: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FeverAccountStore] Failed to delete profile dir: {ex.Message}");
            }

            return true;
        }
    }

    public string? GetActiveAccountTicket()
    {
        var active = GetActiveAccount();
        return active is not null ? GetAccountTicket(active.Id) : null;
    }

    public string? GetAccountTicket(string accountId)
    {
        var ticketFile = Path.Combine(_storageDirectory, "profiles", accountId, "ticket.txt");
        if (File.Exists(ticketFile))
        {
            try
            {
                var content = File.ReadAllText(ticketFile).Trim();
                if (!string.IsNullOrEmpty(content) &&
                    !string.Equals(content, "aecfrt3rmaaaaajl", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(content, "aecglf6ee4aaaarz", StringComparison.OrdinalIgnoreCase))
                    return content;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FeverAccountStore] Failed to read ticket for {accountId}: {ex.Message}");
            }
        }

        // Fallback for long-term accounts: extract token from FeverToken
        var meta = LoadMetadata();
        var account = meta.Accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is not null && !string.IsNullOrWhiteSpace(account.FeverToken))
        {
            var parsed = TryParseFeverToken(account.FeverToken);
            if (!string.IsNullOrWhiteSpace(parsed.Token))
                return parsed.Token.Trim();
        }

        return null;
    }

    public void UpdateAccountTicket(string accountId, string ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return;

        var profileDir = Path.Combine(_storageDirectory, "profiles", accountId);
        if (Directory.Exists(profileDir))
        {
            try
            {
                File.WriteAllText(Path.Combine(profileDir, "ticket.txt"), ticket.Trim());
            }
            catch { }
        }
    }
}
