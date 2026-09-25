using System.Diagnostics;
using System.Text.Json;

namespace IDVBuff.Features.GameLaunch;

/// <summary>
/// Manages persistent profiles for NetEase Fever official accounts,
/// enabling multi-account storage and fast switching.
/// </summary>
public sealed class FeverAccountStore
{
    private static readonly Lazy<FeverAccountStore> DefaultInstance = new(() => new FeverAccountStore());
    public static FeverAccountStore Instance => DefaultInstance.Value;

    private readonly string _storageDirectory;
    private readonly string _unisdkDirectory;
    private readonly string _neteaseMpayDirectory;
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

    /// <summary>
    /// Prepares a clean environment before presenting the native login/QR dialog,
    /// preventing MPay from auto-logging into the existing cached account.
    /// </summary>
    public void PrepareForNewLogin()
    {
        // 1. Ensure Fever processes are stopped so files are unlocked
        StopFeverProcesses();

        // 2. Clear MWSlocalStorage (Chromium/CEF cookies & local storage that cause instant auto-login flash crash)
        var mwsStorageDir = Path.Combine(_neteaseMpayDirectory, "MWSlocalStorage");
        DeleteDirectoryWithRetry(mwsStorageDir);

        // 3. Clear h55 session databases in %APPDATA%\Netease\Mpay
        if (Directory.Exists(_neteaseMpayDirectory))
        {
            foreach (var file in Directory.GetFiles(_neteaseMpayDirectory, "*h55*"))
            {
                DeleteFileWithRetry(file);
            }
        }

        // 4. Clear session databases in Fever's unisdk directory
        if (Directory.Exists(_unisdkDirectory))
        {
            foreach (var file in Directory.GetFiles(_unisdkDirectory, "*mpay*"))
            {
                DeleteFileWithRetry(file);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path)) return;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(50 * (i + 1));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FeverAccountStore] Delete dir {path} failed: {ex.Message}");
                return;
            }
        }
    }

    private static void DeleteFileWithRetry(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(50 * (i + 1));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FeverAccountStore] Delete {path} failed: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Restores the previously active account profile if the user canceled the login dialog.
    /// </summary>
    public async Task RollbackNewLoginAsync(CancellationToken cancellationToken = default)
    {
        var active = GetActiveAccount();
        if (active is not null)
        {
            await SwitchAccountAsync(active.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<FeverAccountProfile> CaptureCurrentAccountAsync(
        string? preferredName = null,
        string? ticket = null,
        CancellationToken cancellationToken = default)
    {
        var meta = LoadMetadata();
        var identifier = ExtractAccountIdentifier(ticket);

        FeverAccountProfile profile;
        lock (_lock)
        {
            // De-duplication check: if account with same identifier already exists, update it
            var existing = !string.IsNullOrEmpty(identifier)
                ? meta.Accounts.FirstOrDefault(a => string.Equals(a.AccountIdentifier, identifier, StringComparison.OrdinalIgnoreCase))
                : null;

            if (existing is not null)
            {
                profile = existing;
                profile.LastUsedAt = DateTimeOffset.Now;
                if (!string.IsNullOrWhiteSpace(preferredName))
                    profile.Name = preferredName.Trim();
            }
            else
            {
                var profileId = Guid.NewGuid().ToString("N");
                var displayName = !string.IsNullOrWhiteSpace(preferredName)
                    ? preferredName.Trim()
                    : $"官服账号 {meta.Accounts.Count + 1}";

                profile = new FeverAccountProfile
                {
                    Id = profileId,
                    Name = displayName,
                    AccountIdentifier = identifier,
                    CreatedAt = DateTimeOffset.Now,
                    LastUsedAt = DateTimeOffset.Now
                };
                meta.Accounts.Add(profile);
            }

            meta.ActiveAccountId = profile.Id;
            SaveMetadata(meta);
        }

        var profileDir = Path.Combine(_storageDirectory, "profiles", profile.Id);
        Directory.CreateDirectory(profileDir);

        // 1. Snapshot %APPDATA%\Netease\Mpay (*h55*)
        if (Directory.Exists(_neteaseMpayDirectory))
        {
            var targetMpayDir = Path.Combine(profileDir, "netease_mpay");
            Directory.CreateDirectory(targetMpayDir);
            foreach (var file in Directory.GetFiles(_neteaseMpayDirectory, "*h55*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = Path.Combine(targetMpayDir, Path.GetFileName(file));
                await CopyFileSafeAsync(file, dest, cancellationToken).ConfigureAwait(false);
            }
        }

        // 2. Snapshot Fever unisdk
        if (Directory.Exists(_unisdkDirectory))
        {
            var targetUnisdkDir = Path.Combine(profileDir, "unisdk");
            Directory.CreateDirectory(targetUnisdkDir);
            foreach (var file in Directory.GetFiles(_unisdkDirectory, "*mpay*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = Path.Combine(targetUnisdkDir, Path.GetFileName(file));
                await CopyFileSafeAsync(file, dest, cancellationToken).ConfigureAwait(false);
            }
        }

        // 3. Save ticket if present
        if (!string.IsNullOrEmpty(ticket))
        {
            await File.WriteAllTextAsync(Path.Combine(profileDir, "ticket.txt"), ticket, cancellationToken).ConfigureAwait(false);
        }

        return profile;
    }

    public async Task<bool> SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var meta = LoadMetadata();
        var target = meta.Accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return false;

        var profileDir = Path.Combine(_storageDirectory, "profiles", accountId);
        if (!Directory.Exists(profileDir))
            return false;

        // Check if game is currently running
        if (Process.GetProcessesByName("dwrg").Length > 0)
            throw new InvalidOperationException("第五人格游戏正在运行中，请先退出游戏后再切换账号。");

        // Ensure Fever is closed so files are not locked
        StopFeverProcesses();

        // 1. Restore %APPDATA%\Netease\Mpay (*h55*)
        var snapshotMpay = Path.Combine(profileDir, "netease_mpay");
        if (Directory.Exists(snapshotMpay))
        {
            Directory.CreateDirectory(_neteaseMpayDirectory);
            foreach (var file in Directory.GetFiles(snapshotMpay))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = Path.Combine(_neteaseMpayDirectory, Path.GetFileName(file));
                await CopyFileSafeAsync(file, dest, cancellationToken).ConfigureAwait(false);
            }
        }

        // 2. Restore Fever unisdk
        var snapshotUnisdk = Path.Combine(profileDir, "unisdk");
        if (Directory.Exists(snapshotUnisdk))
        {
            Directory.CreateDirectory(_unisdkDirectory);
            foreach (var file in Directory.GetFiles(snapshotUnisdk))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = Path.Combine(_unisdkDirectory, Path.GetFileName(file));
                await CopyFileSafeAsync(file, dest, cancellationToken).ConfigureAwait(false);
            }
        }

        lock (_lock)
        {
            meta.ActiveAccountId = accountId;
            target.LastUsedAt = DateTimeOffset.Now;
            SaveMetadata(meta);
        }

        return true;
    }

    public async Task EnsureActiveAccountDeployedAsync(CancellationToken cancellationToken = default)
    {
        var active = GetActiveAccount();
        if (active is null) return;

        var profileDir = Path.Combine(_storageDirectory, "profiles", active.Id);
        if (!Directory.Exists(profileDir)) return;

        // Check if %APPDATA%\Netease\Mpay has h55 mpay.db
        bool mpayReady = Directory.Exists(_neteaseMpayDirectory) &&
                         Directory.GetFiles(_neteaseMpayDirectory, "*h55*mpay.db").Length > 0;

        if (!mpayReady)
        {
            await SwitchAccountAsync(active.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public static void StopFeverProcesses()
    {
        string[] processNames = ["FeverGamesInstaller", "FeverGamesLauncher", "FeverGamesWeb"];
        foreach (var name in processNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    using (proc)
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(1000);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FeverAccountStore] Failed to kill {name}: {ex.Message}");
                }
            }
        }
    }

    private static async Task CopyFileSafeAsync(string source, string dest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                await sourceStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(100 * (attempt + 1), ct).ConfigureAwait(false);
            }
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
                if (!string.IsNullOrEmpty(content))
                    return content;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FeverAccountStore] Failed to read ticket for {accountId}: {ex.Message}");
            }
        }
        return null;
    }

    private static string? ExtractAccountIdentifier(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;

        var trimmed = ticket.Trim();

        // Try JSON parsing
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.TryGetProperty("uid", out var uidProp) && !string.IsNullOrWhiteSpace(uidProp.GetString()))
                return uidProp.GetString();
            if (root.TryGetProperty("username", out var userProp) && !string.IsNullOrWhiteSpace(userProp.GetString()))
                return userProp.GetString();
            if (root.TryGetProperty("account", out var accProp) && !string.IsNullOrWhiteSpace(accProp.GetString()))
                return accProp.GetString();
        }
        catch { }

        // Try query string parsing
        if (trimmed.Contains('='))
        {
            var parts = trimmed.Split('&');
            foreach (var p in parts)
            {
                var kv = p.Split('=');
                if (kv.Length == 2 && (kv[0] is "uid" or "username" or "account") && !string.IsNullOrWhiteSpace(kv[1]))
                    return kv[1].Trim();
            }
        }

        // If ticket is a raw opaque token (e.g. 8~256 chars without whitespace), use it as unique identifier
        if (trimmed.Length >= 8 && trimmed.Length <= 256 && !trimmed.Any(char.IsWhiteSpace))
        {
            return trimmed;
        }

        return null;
    }
}
