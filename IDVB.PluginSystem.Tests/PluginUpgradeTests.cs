using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;

namespace IDVB.PluginSystem.Tests;

public sealed class PluginUpgradeTests
{
    [Fact]
    public async Task UpgradeActivatesOnNextStartAndPreservesPriorVersionAndData()
    {
        using var fixture = new PluginPackageTestFixture();
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.5.0");
        var v1 = await fixture.PackAsync(fileName: "v1.idvp");
        await installer.InstallAsync(v1, new PluginInstallApproval { TrustPublisher = true });
        await installer.ApplyStartupChangesAsync();
        var dataDirectory = directories.GetDataDirectory("tests.publisher", "tests.match-notifier");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "preserved.txt"), "data");

        var v2 = await fixture.PackAsync(fixture.CreateManifest("1.1.0"), fileName: "v2.idvp");
        var result = await installer.InstallAsync(v2, new PluginInstallApproval());

        Assert.Equal("1.0.0", result.CatalogEntry.ActiveVersion);
        Assert.Equal("1.1.0", result.CatalogEntry.PendingVersion);
        await installer.ApplyStartupChangesAsync();
        var activated = (await state.ReadCatalogAsync()).Plugins.Single();
        Assert.Equal("1.1.0", activated.ActiveVersion);
        Assert.Contains("1.0.0", activated.PreviousVersions);
        Assert.True(File.Exists(Path.Combine(dataDirectory, "preserved.txt")));

        await installer.ScheduleRollbackAsync("tests.match-notifier");
        var scheduled = (await state.ReadCatalogAsync()).Plugins.Single();
        Assert.Equal("1.1.0", scheduled.ActiveVersion);
        Assert.Equal("1.0.0", scheduled.PendingVersion);
        await installer.ApplyStartupChangesAsync();
        var rolledBack = (await state.ReadCatalogAsync()).Plugins.Single();
        Assert.Equal("1.0.0", rolledBack.ActiveVersion);
        Assert.Contains("1.1.0", rolledBack.PreviousVersions);
    }

    [Fact]
    public async Task PublisherKeyRotationRequiresFreshConfirmation()
    {
        using var fixture = new PluginPackageTestFixture();
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.5.0");
        var v1 = await fixture.PackAsync(fileName: "v1.idvp");
        await installer.InstallAsync(v1, new PluginInstallApproval { TrustPublisher = true });
        await installer.ApplyStartupChangesAsync();
        var rotatedKeyPath = Path.Combine(fixture.Root, "rotated.pem");
        await File.WriteAllTextAsync(
            rotatedKeyPath,
            IdentityVisionBridge.PluginPackaging.IdvpCrypto.CreatePublisherKey().PrivateKeyPem);
        var v2 = await fixture.PackAsync(
            fixture.CreateManifest("1.1.0"),
            fileName: "rotated.idvp",
            privateKeyPath: rotatedKeyPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            v2,
            new PluginInstallApproval()));
        var accepted = await installer.InstallAsync(
            v2,
            new PluginInstallApproval { TrustPublisher = true });
        Assert.True(accepted.PublisherWasNewlyTrusted);
    }

    [Fact]
    public async Task RejectedRotatedKeyDowngradeDoesNotChangeTrustedPublisher()
    {
        using var fixture = new PluginPackageTestFixture();
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.5.0");
        var v1 = await fixture.PackAsync(fileName: "v1.idvp");
        await installer.InstallAsync(v1, new PluginInstallApproval { TrustPublisher = true });
        await installer.ApplyStartupChangesAsync();
        var originalKey = (await state.ReadPublishersAsync()).Publishers.Single().KeyId;
        var rotatedKeyPath = Path.Combine(fixture.Root, "rejected-rotated.pem");
        await File.WriteAllTextAsync(
            rotatedKeyPath,
            IdentityVisionBridge.PluginPackaging.IdvpCrypto.CreatePublisherKey().PrivateKeyPem);
        var downgrade = await fixture.PackAsync(
            fixture.CreateManifest("0.9.0"),
            fileName: "downgrade.idvp",
            privateKeyPath: rotatedKeyPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            downgrade,
            new PluginInstallApproval { TrustPublisher = true }));

        Assert.Equal(originalKey, (await state.ReadPublishersAsync()).Publishers.Single().KeyId);
    }

    [Fact]
    public async Task CapabilityExpansionRequiresExplicitApproval()
    {
        using var fixture = new PluginPackageTestFixture();
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.5.0");
        var v1 = await fixture.PackAsync(fileName: "v1.idvp");
        await installer.InstallAsync(v1, new PluginInstallApproval { TrustPublisher = true });
        await installer.ApplyStartupChangesAsync();
        var v2 = await fixture.PackAsync(
            fixture.CreateManifest("1.1.0", [PluginCapabilityIds.NotificationsPost]),
            fileName: "v2.idvp");

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            v2,
            new PluginInstallApproval()));
        var accepted = await installer.InstallAsync(
            v2,
            new PluginInstallApproval
            {
                ApprovedCapabilities = new HashSet<string> { PluginCapabilityIds.NotificationsPost }
            });
        Assert.Contains(PluginCapabilityIds.NotificationsPost, accepted.CatalogEntry.GrantedCapabilities);
        Assert.False(accepted.CatalogEntry.Enabled);
    }
}
