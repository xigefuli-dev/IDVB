using System.Diagnostics;
using System.Security.Cryptography;
using IDVBuff.UpdateCore;
using Velopack;

namespace IDVBuff.Updater;

internal sealed class UpdaterCoordinator : IDisposable
{
    private readonly UpdaterLaunchOptions _options;
    private readonly EcdsaUpdateFeedVerifier _verifier;
    private readonly SignedWebUpdateSource _source;
    private readonly UpdateManager _manager;
    private UpdateInfo? _update;
    private string? _legacySetupPath;

    public UpdaterCoordinator(UpdaterLaunchOptions options)
    {
        _options = options;
        _verifier = UpdateTrustStore.CreateVerifier();
        _source = new SignedWebUpdateSource(options.UpdateRoot, options.Channel, _verifier);
        _manager = new UpdateManager(
            _source,
            new UpdateOptions { ExplicitChannel = options.Channel });
    }

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "旧版安装";
    public UpdateFeedPayload? Metadata => _source.LastVerifiedPayload;
    public bool WillUseDeltaPackage => _update is { DeltasToTarget.Length: > 0 };
    public int DeltaPackageCount => _update?.DeltasToTarget.Length ?? 0;

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        if (_manager.IsInstalled)
        {
            _update = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
            return _update is not null;
        }

        _ = await _source.FetchVerifiedFeedAsync(cancellationToken);
        if (Metadata?.MigrationBaseline != true)
            throw new InvalidOperationException("当前更新没有提供从传统安装包迁移到内置更新体系的入口。");
        return true;
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        if (_manager.IsInstalled)
        {
            if (_update is null)
                throw new InvalidOperationException("没有可下载的更新。");
            await _manager.DownloadUpdatesAsync(_update, progress, cancellationToken);
            return;
        }

        var metadata = Metadata ?? throw new InvalidOperationException("缺少已验证的迁移安装器信息。");
        var channelUri = UpdateProtocol.GetChannelUri(_options.UpdateRoot, _options.Channel);
        var targetDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVB",
            "UpdateDownloads");
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, metadata.Installer.FileName);
        var partial = target + ".partial";
        if (File.Exists(target))
        {
            if (await HasExpectedHashAsync(target, metadata.Installer.Sha256, cancellationToken))
            {
                _legacySetupPath = target;
                progress(100);
                return;
            }
            throw new IOException($"已有下载文件校验失败，未覆盖该文件：{target}");
        }
        if (File.Exists(partial))
            throw new IOException($"发现未完成的下载文件，未覆盖该文件：{partial}");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await client.GetAsync(
            new Uri(channelUri, metadata.Installer.FileName),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                progress((int)Math.Clamp(written * 100L / metadata.Installer.Size, 0, 100));
            }
        }
        var downloaded = new FileInfo(partial);
        if (downloaded.Length != metadata.Installer.Size
            || !await HasExpectedHashAsync(partial, metadata.Installer.Sha256, cancellationToken))
            throw new CryptographicException("迁移安装器 SHA-256 校验失败。");
        File.Move(partial, target);
        _legacySetupPath = target;
        progress(100);
    }

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        var targetVersion = Metadata?.PublicVersion
            ?? _update?.TargetFullRelease.Version.ToString()
            ?? "unknown";
        await MainShutdownClient.RequestAndWaitAsync(
            _options.MainProcessId,
            targetVersion,
            cancellationToken);

        if (_manager.IsInstalled)
        {
            if (_update is null)
                throw new InvalidOperationException("没有已准备的更新。");
            _manager.WaitExitThenApplyUpdates(
                _update.TargetFullRelease,
                silent: false,
                restart: true,
                restartArgs: ["--updated-from", CurrentVersion]);
            Environment.Exit(0);
        }

        if (string.IsNullOrWhiteSpace(_legacySetupPath) || !File.Exists(_legacySetupPath))
            throw new InvalidOperationException("迁移安装器尚未准备完成。");
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = _legacySetupPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(_legacySetupPath)
        });
        Environment.Exit(0);
    }

    public void Dispose() => _source.Dispose();

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
