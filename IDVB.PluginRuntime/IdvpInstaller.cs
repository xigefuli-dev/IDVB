using IdentityVisionBridge.PluginPackaging;

namespace IdentityVisionBridge.PluginRuntime;

public sealed class IdvpInstaller
{
    private readonly PluginDirectories _directories;
    private readonly PluginStateRepository _state;
    private readonly string _hostVersion;
    private readonly string _pluginApiVersion;

    public IdvpInstaller(
        PluginDirectories directories,
        PluginStateRepository state,
        string hostVersion,
        string pluginApiVersion = "2.0.0")
    {
        _directories = directories;
        _state = state;
        _hostVersion = hostVersion;
        _pluginApiVersion = pluginApiVersion;
    }

    public async Task<IdvpValidatedPackage> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var reader = new IdvpPackageReader();
        return await reader.ValidateAsync(
            packagePath,
            options: new IdvpValidationOptions
            {
                AllowUnsigned = _directories.DeveloperMode,
                ExtractFiles = false
            },
            cancellationToken: cancellationToken);
    }

    public async Task<PluginInstallResult> InstallAsync(
        string packagePath,
        PluginInstallApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        _directories.EnsureCreated();
        var stagingDirectory = Path.Combine(_directories.Staging, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        string? targetDirectory = null;
        var committed = false;
        try
        {
            var reader = new IdvpPackageReader();
            var package = await reader.ValidateAsync(
                packagePath,
                stagingDirectory,
                new IdvpValidationOptions
                {
                    AllowUnsigned = _directories.DeveloperMode,
                    ExtractFiles = true
                },
                cancellationToken);

            ValidateCompatibility(package.Manifest);
            var catalog = await _state.ReadCatalogAsync(cancellationToken);
            var existing = catalog.Plugins.SingleOrDefault(plugin => plugin.Id == package.Manifest.Id);
            var requested = package.Manifest.Capabilities.ToHashSet(StringComparer.Ordinal);
            if (!requested.IsSubsetOf(approval.ApprovedCapabilities))
            {
                throw new InvalidOperationException("All requested plugin capabilities must be explicitly approved.");
            }

            targetDirectory = _directories.GetPackageDirectory(package.Manifest.Id, package.Manifest.Version);
            if (Directory.Exists(targetDirectory))
            {
                throw new InvalidOperationException("This plugin version is already installed.");
            }

            var publisherTrust = await ValidatePublisherAsync(package, approval, cancellationToken);
            ValidateUpgrade(existing, package, approval, publisherTrust.KeyRotated);
            await RecordPublisherTrustAsync(package, publisherTrust, cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
            Directory.Move(stagingDirectory, targetDirectory);
            committed = true;

            var updatedCatalog = await _state.UpdateCatalogAsync(
                current => UpsertCatalogEntry(current, existing, package, requested),
                cancellationToken);
            var updatedEntry = updatedCatalog.Plugins.Single(plugin => plugin.Id == package.Manifest.Id);
            return new PluginInstallResult
            {
                CatalogEntry = updatedEntry,
                InstalledVersion = package.Manifest.Version,
                RequiresRestart = true,
                PublisherWasNewlyTrusted = publisherTrust.TrustChanged
            };
        }
        catch (Exception installException)
        {
            if (committed && targetDirectory is not null && Directory.Exists(targetDirectory))
            {
                try
                {
                    Directory.Move(targetDirectory, stagingDirectory);
                    committed = false;
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Plugin installation failed and its committed package directory could not be rolled back.",
                        installException,
                        rollbackException);
                }
            }

            throw;
        }
        finally
        {
            if (!committed && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    public async Task SetEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _state.UpdateCatalogAsync(
            catalog => catalog with
            {
                Plugins = catalog.Plugins.Select(plugin =>
                    plugin.Id == pluginId
                        ? plugin with
                        {
                            Enabled = enabled && !plugin.CapabilityApprovalRequired && plugin.QuarantineReason is null,
                            QuarantineReason = enabled ? plugin.QuarantineReason : null
                        }
                        : plugin).ToArray()
            },
            cancellationToken);
    }

    public async Task ApproveCapabilitiesAsync(
        string pluginId,
        IReadOnlySet<string> capabilities,
        CancellationToken cancellationToken = default)
    {
        var catalog = await _state.ReadCatalogAsync(cancellationToken);
        var entry = catalog.Plugins.SingleOrDefault(plugin => plugin.Id == pluginId)
            ?? throw new InvalidOperationException("Plugin is not installed.");
        var version = entry.PendingVersion ?? entry.ActiveVersion
            ?? throw new InvalidOperationException("Plugin has no installed active version.");
        var manifest = await ReadInstalledManifestAsync(entry.Id, version, cancellationToken);
        var requested = manifest.Capabilities.ToHashSet(StringComparer.Ordinal);
        if (!requested.IsSubsetOf(capabilities))
        {
            throw new InvalidOperationException("All requested capabilities must be approved.");
        }

        await _state.UpdateCatalogAsync(
            current => current with
            {
                Plugins = current.Plugins.Select(plugin => plugin.Id == pluginId
                    ? plugin with
                    {
                        GrantedCapabilities = requested.Order(StringComparer.Ordinal).ToArray(),
                        CapabilityApprovalRequired = false
                    }
                    : plugin).ToArray()
            },
            cancellationToken);
    }

    public async Task MarkForUninstallAsync(
        string pluginId,
        bool deleteData = false,
        CancellationToken cancellationToken = default)
    {
        await _state.UpdateCatalogAsync(
            catalog => catalog with
            {
                Plugins = catalog.Plugins.Select(plugin => plugin.Id == pluginId
                    ? plugin with
                    {
                        Enabled = false,
                        PendingDelete = true,
                        DeleteDataOnUninstall = deleteData
                    }
                    : plugin).ToArray()
            },
            cancellationToken);
    }

    public async Task ClearQuarantineAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        await _state.UpdateCatalogAsync(
            catalog => catalog with
            {
                Plugins = catalog.Plugins.Select(plugin => plugin.Id == pluginId
                    ? plugin with { Enabled = false, QuarantineReason = null }
                    : plugin).ToArray()
            },
            cancellationToken);
    }

    public async Task DisableAllAsync(CancellationToken cancellationToken = default)
    {
        await _state.UpdateCatalogAsync(
            catalog => catalog with
            {
                Plugins = catalog.Plugins.Select(plugin => plugin with { Enabled = false }).ToArray()
            },
            cancellationToken);
    }

    public async Task ScheduleRollbackAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        var catalog = await _state.ReadCatalogAsync(cancellationToken);
        var entry = catalog.Plugins.SingleOrDefault(plugin => plugin.Id == pluginId)
            ?? throw new InvalidOperationException("Plugin is not installed.");
        if (entry.PendingVersion is not null)
            throw new InvalidOperationException("A plugin version change is already waiting for restart.");
        var rollbackVersion = entry.PreviousVersions.LastOrDefault()
            ?? throw new InvalidOperationException("The plugin has no retained version to roll back to.");
        if (!Directory.Exists(_directories.GetPackageDirectory(pluginId, rollbackVersion)))
            throw new InvalidOperationException("The retained rollback package is missing.");

        await _state.UpdateCatalogAsync(
            current => current with
            {
                Plugins = current.Plugins.Select(plugin => plugin.Id == pluginId
                    ? plugin with
                    {
                        Enabled = false,
                        PendingVersion = rollbackVersion,
                        PreviousVersions = plugin.PreviousVersions.SkipLast(1).ToArray(),
                        QuarantineReason = null
                    }
                    : plugin).ToArray()
            },
            cancellationToken);
    }

    public async Task ApplyStartupChangesAsync(CancellationToken cancellationToken = default)
    {
        _directories.EnsureCreated();
        var catalog = await _state.ReadCatalogAsync(cancellationToken);
        var retained = new List<PluginCatalogEntry>();
        foreach (var entry in catalog.Plugins)
        {
            if (entry.PendingDelete)
            {
                var pluginRoot = Path.Combine(_directories.Packages, entry.Id);
                if (Directory.Exists(pluginRoot))
                {
                    Directory.Delete(pluginRoot, recursive: true);
                }

                if (entry.DeleteDataOnUninstall)
                {
                    var dataDirectory = _directories.GetDataDirectory(entry.PublisherId, entry.Id);
                    if (Directory.Exists(dataDirectory))
                        Directory.Delete(dataDirectory, recursive: true);
                }

                continue;
            }

            if (entry.PendingVersion is not null)
            {
                var previous = entry.ActiveVersion is null
                    ? entry.PreviousVersions
                    : entry.PreviousVersions.Append(entry.ActiveVersion).Distinct(StringComparer.Ordinal).ToArray();
                retained.Add(entry with
                {
                    ActiveVersion = entry.PendingVersion,
                    PendingVersion = null,
                    PreviousVersions = previous
                });
            }
            else
            {
                retained.Add(entry);
            }
        }

        await _state.UpdateCatalogAsync(current => current with { Plugins = retained }, cancellationToken);
    }

    public async Task RecheckCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await _state.ReadCatalogAsync(cancellationToken);
        var updates = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in catalog.Plugins.Where(static plugin => plugin.ActiveVersion is not null))
        {
            var manifest = await ReadInstalledManifestAsync(entry.Id, entry.ActiveVersion!, cancellationToken);
            updates[entry.Id] = IsCompatible(manifest);
        }

        await _state.UpdateCatalogAsync(
            current => current with
            {
                Plugins = current.Plugins.Select(plugin => updates.TryGetValue(plugin.Id, out var compatible) && !compatible
                    ? plugin with { Enabled = false, QuarantineReason = "The plugin is incompatible with this IDVB version." }
                    : plugin).ToArray()
            },
            cancellationToken);
    }

    private async Task<PublisherTrustDecision> ValidatePublisherAsync(
        IdvpValidatedPackage package,
        PluginInstallApproval approval,
        CancellationToken cancellationToken)
    {
        if (!package.IsSigned)
        {
            if (!_directories.DeveloperMode)
            {
                throw new InvalidOperationException("Unsigned plugins require developer mode.");
            }

            return new PublisherTrustDecision(false, false);
        }

        var publishers = await _state.ReadPublishersAsync(cancellationToken);
        var existing = publishers.Publishers.SingleOrDefault(
            publisher => publisher.PublisherId == package.Manifest.Publisher.Id);
        if (existing is not null)
        {
            if (!string.Equals(existing.KeyId, package.Signature.KeyId, StringComparison.OrdinalIgnoreCase))
            {
                if (!approval.TrustPublisher)
                {
                    throw new InvalidOperationException("The publisher key changed and must be trusted again.");
                }

                return new PublisherTrustDecision(true, true);
            }

            return new PublisherTrustDecision(false, false);
        }

        if (!approval.TrustPublisher)
        {
            throw new InvalidOperationException("The publisher key has not been trusted by the user.");
        }

        return new PublisherTrustDecision(true, false);
    }

    private async Task RecordPublisherTrustAsync(
        IdvpValidatedPackage package,
        PublisherTrustDecision decision,
        CancellationToken cancellationToken)
    {
        if (!decision.TrustChanged)
            return;

        await _state.UpdatePublishersAsync(
            current => current with
            {
                Publishers = current.Publishers
                    .Where(publisher => publisher.PublisherId != package.Manifest.Publisher.Id)
                    .Append(new TrustedPublisher
                    {
                        PublisherId = package.Manifest.Publisher.Id,
                        PublisherName = package.Manifest.Publisher.Name,
                        KeyId = package.Signature.KeyId!,
                        TrustedAt = DateTimeOffset.UtcNow
                    }).ToArray()
            },
            cancellationToken);
    }

    private void ValidateUpgrade(
        PluginCatalogEntry? existing,
        IdvpValidatedPackage package,
        PluginInstallApproval approval,
        bool keyRotated)
    {
        if (existing is null)
        {
            return;
        }

        if (existing.PublisherId != package.Manifest.Publisher.Id ||
            (!keyRotated && !string.Equals(
                existing.PublisherKeyId,
                package.Manifest.Publisher.KeyId,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A plugin ID cannot be taken over by another publisher or key.");
        }

        var currentText = existing.PendingVersion ?? existing.ActiveVersion;
        if (currentText is null || !SemanticVersion.TryParse(currentText, out var current) ||
            !SemanticVersion.TryParse(package.Manifest.Version, out var incoming))
        {
            throw new InvalidOperationException("The installed or incoming plugin version is invalid.");
        }

        var comparison = incoming.CompareTo(current);
        if (comparison == 0)
        {
            throw new InvalidOperationException("This plugin version is already installed.");
        }

        if (comparison < 0 && !(_directories.DeveloperMode && approval.AllowDeveloperDowngrade))
        {
            throw new InvalidOperationException("Plugin downgrade is only allowed explicitly in developer mode.");
        }
    }

    private static PluginCatalog UpsertCatalogEntry(
        PluginCatalog catalog,
        PluginCatalogEntry? existing,
        IdvpValidatedPackage package,
        IReadOnlySet<string> requested)
    {
        var replacement = new PluginCatalogEntry
        {
            Id = package.Manifest.Id,
            DisplayName = package.Manifest.DisplayName,
            PublisherId = package.Manifest.Publisher.Id,
            PublisherName = package.Manifest.Publisher.Name,
            PublisherKeyId = package.Manifest.Publisher.KeyId,
            ActiveVersion = existing?.ActiveVersion,
            PendingVersion = package.Manifest.Version,
            PreviousVersions = existing?.PreviousVersions ?? [],
            Enabled = false,
            GrantedCapabilities = requested.Order(StringComparer.Ordinal).ToArray(),
            CapabilityApprovalRequired = false,
            QuarantineReason = null,
            PendingDelete = false,
            DeleteDataOnUninstall = false,
            InstalledAt = existing?.InstalledAt ?? DateTimeOffset.UtcNow
        };

        return catalog with
        {
            Plugins = catalog.Plugins.Where(plugin => plugin.Id != package.Manifest.Id).Append(replacement).ToArray()
        };
    }

    private void ValidateCompatibility(IdvpManifest manifest)
    {
        if (!IsCompatible(manifest))
        {
            throw new InvalidOperationException("The plugin is incompatible with this IDVB or Plugin API version.");
        }
    }

    private bool IsCompatible(IdvpManifest manifest) =>
        SemanticVersionRange.Contains(manifest.Compatibility.PluginApi, _pluginApiVersion) &&
        SemanticVersionRange.Contains(manifest.Compatibility.Host, _hostVersion);

    private async Task<IdvpManifest> ReadInstalledManifestAsync(
        string pluginId,
        string version,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directories.GetPackageDirectory(pluginId, version), "manifest.json");
        await using var stream = File.OpenRead(path);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<IdvpManifest>(
                   stream,
                   new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web),
                   cancellationToken)
               ?? throw new InvalidDataException("Installed plugin manifest is empty.");
    }

    private sealed record PublisherTrustDecision(bool TrustChanged, bool KeyRotated);
}
