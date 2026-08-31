using IDVBuff.UpdateCore;
using IDVBuff.Diagnostics;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed record MapSubscriptionCheckResult(
    int CheckedCount,
    int UpToDateCount,
    int AppliedCount,
    int FailedCount);

public sealed record MapSubscriptionReconciliationResult(
    int RemovedSubscriptionCount,
    int IncompleteSubscriptionCount);

public sealed class MapSubscriptionService
{
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private readonly string _rootDirectory;
    private readonly MapSubscriptionStore _store;
    private readonly MapRepository _repository;
    private readonly IdvmPackageService _packages;

    public MapSubscriptionService(MapRepository repository, string? dataRoot = null)
    {
        _repository = repository;
        _packages = new IdvmPackageService(repository);
        _rootDirectory = Path.Combine(
            dataRoot ?? global::IDVBuff.AppDataPaths.RootDirectory,
            "MapSubscriptions");
        _store = new MapSubscriptionStore(_rootDirectory);
    }

    public IReadOnlyList<MapSubscriptionRecord> GetSubscriptions() => _store.Load();

    public async Task<MapSubscriptionReconciliationResult> ReconcileInstalledMapsAsync(
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken);
        try
        {
            var records = _store.Load().ToList();
            var installedMapIds = (await _repository.GetCatalogSnapshotAsync())
                .Maps.Select(map => map.Id).ToHashSet();
            var removed = 0;
            var incomplete = 0;
            foreach (var record in records.ToArray())
            {
                var action = MapSubscriptionReconciliation.Evaluate(record, installedMapIds);
                if (action == MapSubscriptionReconciliationAction.RemoveSubscription)
                {
                    records.Remove(record);
                    removed++;
                    continue;
                }
                if (action != MapSubscriptionReconciliationAction.ForceReapply)
                    continue;
                record.LastAppliedPlaintextSha256 = null;
                incomplete++;
            }
            if (removed > 0 || incomplete > 0)
                _store.Save(records);
            return new MapSubscriptionReconciliationResult(removed, incomplete);
        }
        finally { OperationGate.Release(); }
    }

    public async Task<MapSubscriptionRecord> AddAsync(
        string linkText,
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken);
        try
        {
            var link = MapSubscriptionLink.Parse(linkText);
            var records = _store.Load().ToList();
            if (records.Any(item => string.Equals(
                item.FeedUri, link.FeedUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("该更新订阅链接已经添加。");
            var record = new MapSubscriptionRecord
            {
                Link = link.ToUriString(),
                FeedUri = link.FeedUri.AbsoluteUri,
                PublisherKeyId = link.PublisherKeyId
            };
            records.Add(record);
            _store.Save(records);
            return record;
        }
        finally { OperationGate.Release(); }
    }

    public async Task SetEnabledAsync(Guid id, bool enabled)
    {
        await OperationGate.WaitAsync();
        try
        {
            var records = _store.Load().ToList();
            var record = records.SingleOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException("找不到更新订阅。");
            record.Enabled = enabled;
            _store.Save(records);
        }
        finally { OperationGate.Release(); }
    }

    public async Task RemoveAsync(Guid id)
    {
        await OperationGate.WaitAsync();
        try
        {
            var records = _store.Load().ToList();
            records.RemoveAll(item => item.Id == id);
            _store.Save(records);
        }
        finally { OperationGate.Release(); }
    }

    public async Task<MapSubscriptionCheckResult> CheckAndApplyAsync(
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken);
        try
        {
            var updaterPath = Path.Combine(AppContext.BaseDirectory, "Updater", "IDVB.Updater.exe");
            if (!File.Exists(updaterPath))
                throw new FileNotFoundException("找不到独立更新器，无法安全更新地图订阅。", updaterPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = Path.GetDirectoryName(updaterPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--map-subscriptions");
            startInfo.ArgumentList.Add("--subscription-root");
            startInfo.ArgumentList.Add(_rootDirectory);
            startInfo.ArgumentList.Add("--from-main-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动独立地图订阅更新器。");
            await process.WaitForExitAsync(cancellationToken);
            var applied = await ApplyPendingAsync(cancellationToken);
            if (process.ExitCode == 4)
                throw new InvalidOperationException("独立地图订阅更新器未能完成初始化，请查看 updater.log。");
            var records = _store.Load();
            var checkedRecords = records.Where(item => item.Enabled).ToArray();
            var failed = checkedRecords.Count(item => !string.IsNullOrWhiteSpace(item.LastError));
            return new MapSubscriptionCheckResult(
                checkedRecords.Length,
                Math.Max(0, checkedRecords.Length - applied - failed),
                applied,
                failed);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    private async Task<int> ApplyPendingAsync(CancellationToken cancellationToken)
    {
        var pendingRoot = Path.Combine(_rootDirectory, "pending");
        if (!Directory.Exists(pendingRoot)) return 0;
        var applied = 0;
        foreach (var receiptPath in Directory.EnumerateFiles(
            pendingRoot, "pending.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = JsonSerializer.Deserialize<PendingMapSubscriptionUpdate>(
                await File.ReadAllTextAsync(receiptPath, cancellationToken),
                MapSubscriptionProtocol.JsonOptions)
                ?? throw new InvalidDataException("地图订阅待应用回执无效。");
            var records = _store.Load().ToList();
            var record = records.SingleOrDefault(item => item.Id == receipt.SubscriptionId);
            if (record is null || !record.Enabled)
                continue;
            if (string.Equals(
                record.LastAppliedPlaintextSha256,
                receipt.Publication.PlaintextSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                DeletePendingFiles(receiptPath, receipt.PackagePath);
                continue;
            }
            try
            {
                await ApplyOneAsync(record, receipt, cancellationToken);
                record.PublisherHandle = receipt.Publication.PublisherHandle;
                record.LastAppliedAtUtc = DateTimeOffset.UtcNow;
                record.LastPublishedAtUtc = receipt.Publication.PublishedAtUtc;
                record.LastAppliedPlaintextSha256 = receipt.Publication.PlaintextSha256;
                record.LastAppliedVersion = receipt.Publication.Version;
                record.LastError = null;
                _store.Save(records);
                DeletePendingFiles(receiptPath, receipt.PackagePath);
                applied++;
            }
            catch (Exception exception)
            {
                record.PublisherHandle = receipt.Publication.PublisherHandle;
                record.LastError = "应用失败：" + exception.Message;
                _store.Save(records);
                OutputLog.Write(
                    "WARN",
                    "MAP/SUBSCRIPTION",
                    $"Unable to apply map subscription {record.Id} version {receipt.Publication.Version}.",
                    exception);
            }
        }
        return applied;
    }

    private async Task ApplyOneAsync(
        MapSubscriptionRecord record,
        PendingMapSubscriptionUpdate receipt,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(receipt.PackagePath)
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                    receipt.PackagePath, cancellationToken))),
                receipt.Publication.PlaintextSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("待应用 IDVM 与已验证订阅回执不一致。");
        IdvmImportPlan? plan = await _packages.InspectAsync(receipt.PackagePath, cancellationToken);
        var sourceNames = plan.Classes.Select(item => item.SourceName).ToArray();
        IdvmImportResult? imported = null;
        try
        {
            imported = await _packages.ImportAsync(plan, cancellationToken);
            plan = null;
            var mappings = sourceNames.Zip(
                imported.CreatedClasses,
                (source, local) => new MapSubscriptionImportedClass(source, local)).ToArray();
            var promotion = await _repository.PromoteSubscriptionImportAsync(
                mappings,
                record.ClassBindings,
                record.InstalledMapIds,
                record.Id,
                receipt.Publication.PublisherHandle,
                receipt.Publication.PublisherKeyId,
                receipt.Publication.Version,
                cancellationToken);
            record.ClassBindings = new Dictionary<string, string>(
                promotion.ClassBindings, StringComparer.OrdinalIgnoreCase);
            record.InstalledMapIds = promotion.InstalledMapIds.ToList();
        }
        catch
        {
            if (plan is not null) await plan.DisposeAsync();
            if (imported is not null)
            {
                foreach (var className in imported.CreatedClasses.Reverse())
                {
                    try
                    {
                        if ((await _repository.GetCatalogSnapshotAsync()).Classes.Contains(
                            className, StringComparer.OrdinalIgnoreCase))
                            await _repository.DeleteClassAsync(className);
                    }
                    catch { }
                }
            }
            throw;
        }
    }

    private static void DeletePendingFiles(string receiptPath, string packagePath)
    {
        try { if (File.Exists(receiptPath)) File.Delete(receiptPath); } catch { }
        try { if (File.Exists(packagePath)) File.Delete(packagePath); } catch { }
    }
}
