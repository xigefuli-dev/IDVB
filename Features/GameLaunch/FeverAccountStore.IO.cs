using System.Diagnostics;

namespace IDVBuff.Features.GameLaunch;

public sealed partial class FeverAccountStore
{
    /// <summary>
    /// Prepares a clean environment before presenting the native login/QR dialog.
    /// Backs up the current environment so it can be restored on cancellation,
    /// and completely clears existing session databases so MPay will always present the login window.
    /// </summary>
    public void PrepareForNewLogin()
    {
        StopFeverProcesses();

        // 1. Backup current session to backup_temp
        try
        {
            if (Directory.Exists(_backupTempDirectory))
                Directory.Delete(_backupTempDirectory, recursive: true);

            var backupMpay = Path.Combine(_backupTempDirectory, "mpay");
            Directory.CreateDirectory(backupMpay);
            if (Directory.Exists(_neteaseMpayDirectory))
            {
                foreach (var f in Directory.GetFiles(_neteaseMpayDirectory))
                {
                    try { File.Copy(f, Path.Combine(backupMpay, Path.GetFileName(f)), true); } catch { }
                }
            }

            var backupUnisdk = Path.Combine(_backupTempDirectory, "unisdk");
            Directory.CreateDirectory(backupUnisdk);
            if (Directory.Exists(_unisdkDirectory))
            {
                foreach (var f in Directory.GetFiles(_unisdkDirectory))
                {
                    try { File.Copy(f, Path.Combine(backupUnisdk, Path.GetFileName(f)), true); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FeverAccountStore] Backup temp failed: {ex.Message}");
        }

        // 2. Clear CEF LocalStorage & cookies in MPay dir
        var mwsStorageDir = Path.Combine(_neteaseMpayDirectory, "MWSlocalStorage");
        DeleteDirectoryWithRetry(mwsStorageDir);

        // 3. Clear session databases in %APPDATA%\Netease\Mpay
        if (Directory.Exists(_neteaseMpayDirectory))
        {
            foreach (var file in Directory.GetFiles(_neteaseMpayDirectory))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var fname = Path.GetFileName(file).ToLowerInvariant();
                if (ext is ".db" or ".lang" or ".log" || fname.Contains("h55") || fname.Contains("a50"))
                {
                    DeleteFileWithRetry(file);
                }
            }
        }

        // 4. Clear session databases in Fever's unisdk directory
        if (Directory.Exists(_unisdkDirectory))
        {
            foreach (var file in Directory.GetFiles(_unisdkDirectory))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".db" or ".lang" or ".log")
                {
                    DeleteFileWithRetry(file);
                }
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
    /// Restores the previous session from backup_temp if the user canceled the login dialog.
    /// </summary>
    public async Task RollbackNewLoginAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var backupMpay = Path.Combine(_backupTempDirectory, "mpay");
            if (Directory.Exists(backupMpay) && Directory.Exists(_neteaseMpayDirectory))
            {
                foreach (var f in Directory.GetFiles(backupMpay))
                {
                    var dest = Path.Combine(_neteaseMpayDirectory, Path.GetFileName(f));
                    await CopyFileSafeAsync(f, dest, cancellationToken).ConfigureAwait(false);
                }
            }

            var backupUnisdk = Path.Combine(_backupTempDirectory, "unisdk");
            if (Directory.Exists(backupUnisdk) && Directory.Exists(_unisdkDirectory))
            {
                foreach (var f in Directory.GetFiles(backupUnisdk))
                {
                    var dest = Path.Combine(_unisdkDirectory, Path.GetFileName(f));
                    await CopyFileSafeAsync(f, dest, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FeverAccountStore] RollbackNewLogin failed: {ex.Message}");
        }
    }

    public async Task<FeverAccountProfile> CaptureCurrentAccountAsync(
        string? preferredName = null,
        string? ticket = null,
        bool isLongTerm = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            throw new InvalidOperationException("未取得登录凭据，不能保存账号。请重新登录。");

        var meta = LoadMetadata();
        var identifier = ExtractAccountIdentifier(ticket);

        FeverAccountProfile profile;
        lock (_lock)
        {
            var existing = !string.IsNullOrEmpty(identifier)
                ? meta.Accounts.FirstOrDefault(a => string.Equals(a.AccountIdentifier, identifier, StringComparison.OrdinalIgnoreCase))
                : null;

            if (existing is not null)
            {
                profile = existing;
                profile.LastUsedAt = DateTimeOffset.Now;
                profile.LastRefreshedAt = DateTimeOffset.Now;
                profile.IsLongTerm = isLongTerm;
                profile.LoginType = isLongTerm ? "FeverLongTerm" : "TemporaryQr";
                if (!string.IsNullOrWhiteSpace(preferredName))
                    profile.Name = preferredName.Trim();
            }
            else
            {
                var profileId = Guid.NewGuid().ToString("N");
                string displayName;
                if (!string.IsNullOrWhiteSpace(preferredName))
                {
                    displayName = preferredName.Trim();
                }
                else if (isLongTerm)
                {
                    displayName = $"长期账号 {meta.Accounts.Count(a => a.IsLongTerm) + 1}";
                }
                else
                {
                    displayName = $"临时扫码 {meta.Accounts.Count(a => !a.IsLongTerm) + 1}";
                }

                profile = new FeverAccountProfile
                {
                    Id = profileId,
                    Name = displayName,
                    AccountIdentifier = identifier,
                    IsLongTerm = isLongTerm,
                    LoginType = isLongTerm ? "FeverLongTerm" : "TemporaryQr",
                    CreatedAt = DateTimeOffset.Now,
                    LastUsedAt = DateTimeOffset.Now,
                    LastRefreshedAt = DateTimeOffset.Now
                };

                meta.Accounts.Add(profile);
            }

            meta.ActiveAccountId = profile.Id;
            SaveMetadata(meta);
        }

        var profileDir = Path.Combine(_storageDirectory, "profiles", profile.Id);
        Directory.CreateDirectory(profileDir);

        // 1. Snapshot %APPDATA%\Netease\Mpay
        if (Directory.Exists(_neteaseMpayDirectory))
        {
            var targetMpayDir = Path.Combine(profileDir, "netease_mpay");
            Directory.CreateDirectory(targetMpayDir);
            foreach (var file in Directory.GetFiles(_neteaseMpayDirectory))
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
            foreach (var file in Directory.GetFiles(_unisdkDirectory))
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

        // Check if game is currently running
        if (Process.GetProcessesByName("dwrg").Length > 0)
            throw new InvalidOperationException("第五人格游戏正在运行中，请先退出游戏后再切换账号。");

        // Ensure Fever is closed so files are not locked
        StopFeverProcesses();

        if (target.IsLongTerm)
        {
            if (!string.IsNullOrEmpty(target.FeverToken))
            {
                WriteFeverRegistryCredentials(target.FeverToken, target.FeverSdkuid);
            }
        }

        var profileDir = Path.Combine(_storageDirectory, "profiles", accountId);
        if (Directory.Exists(profileDir))
        {
            // 1. Clean current system directories before restoring to prevent file mix-ups
            if (Directory.Exists(_neteaseMpayDirectory))
            {
                foreach (var file in Directory.GetFiles(_neteaseMpayDirectory))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".db" or ".lang" or ".log")
                    {
                        DeleteFileWithRetry(file);
                    }
                }
            }

            if (Directory.Exists(_unisdkDirectory))
            {
                foreach (var file in Directory.GetFiles(_unisdkDirectory))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".db" or ".lang" or ".log")
                    {
                        DeleteFileWithRetry(file);
                    }
                }
            }

            // 2. Restore %APPDATA%\Netease\Mpay
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

            // 3. Restore Fever unisdk
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

        if (active.IsLongTerm)
        {
            if (!string.IsNullOrEmpty(active.FeverToken))
            {
                WriteFeverRegistryCredentials(active.FeverToken, active.FeverSdkuid);
            }
            return;
        }

        var profileDir = Path.Combine(_storageDirectory, "profiles", active.Id);
        if (!Directory.Exists(profileDir)) return;

        // Check if %APPDATA%\Netease\Mpay has the active account's session files
        var activeTicket = GetAccountTicket(active.Id) ?? active.AccountIdentifier;
        bool mpayReady = false;
        if (Directory.Exists(_neteaseMpayDirectory))
        {
            var files = Directory.GetFiles(_neteaseMpayDirectory, "*.db");
            if (!string.IsNullOrEmpty(activeTicket))
            {
                mpayReady = files.Any(f => Path.GetFileName(f).StartsWith(activeTicket, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                mpayReady = files.Length > 0;
            }
        }

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

    private static string? ExtractAccountIdentifier(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;

        var trimmed = ticket.Trim();

        // Try JSON parsing
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
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

    /// <summary>
    /// Extracts the 16-character NetEase MPay token from database filenames matching "*-g-h55*.db" or "*-g-*.db".
    /// </summary>
    public static string? TryExtractTokenFromMpayDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        try
        {
            var files = Directory.GetFiles(directory, "*-g-*.db");
            // Prefer h55 database
            var h55File = files.FirstOrDefault(f => Path.GetFileName(f).Contains("-g-h55"));
            var targetFile = h55File ?? files.FirstOrDefault();
            if (targetFile is not null)
            {
                var filename = Path.GetFileName(targetFile);
                var gIdx = filename.IndexOf("-g-", StringComparison.OrdinalIgnoreCase);
                if (gIdx >= 8)
                {
                    return filename[..gIdx].Trim();
                }
            }
        }
        catch { }

        return null;
    }
}

