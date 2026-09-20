using IdentityVisionBridge.PluginPackaging;
using System.Text;
using System.Text.Json;

namespace IDVB.PluginSystem.Tests;

public sealed class IdvpPackageSecurityTests
{
    [Fact]
    public async Task SignedPackageRoundTripsAndVerifiesPublisherKey()
    {
        using var fixture = new PluginPackageTestFixture();
        var path = await fixture.PackAsync();

        var package = await new IdvpPackageReader().ValidateAsync(
            path,
            options: new IdvpValidationOptions { ExtractFiles = false });

        Assert.True(package.IsSigned);
        Assert.Equal(64, package.Signature.KeyId!.Length);
        Assert.Equal(package.Manifest.Publisher.KeyId, package.Signature.KeyId);
    }

    [Fact]
    public async Task UnsignedPackageRequiresExplicitDeveloperAllowance()
    {
        using var fixture = new PluginPackageTestFixture();
        var path = await fixture.PackAsync(signed: false);
        var reader = new IdvpPackageReader();

        await Assert.ThrowsAsync<IdvpPackageException>(() => reader.ValidateAsync(
            path,
            options: new IdvpValidationOptions { ExtractFiles = false }));
        var package = await reader.ValidateAsync(
            path,
            options: new IdvpValidationOptions { AllowUnsigned = true, ExtractFiles = false });
        Assert.False(package.IsSigned);
    }

    [Fact]
    public async Task PayloadTamperingIsRejected()
    {
        using var fixture = new PluginPackageTestFixture();
        var path = await fixture.PackAsync();
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0x5A;
        var tampered = Path.Combine(fixture.Root, "tampered.idvp");
        await File.WriteAllBytesAsync(tampered, bytes);

        await Assert.ThrowsAnyAsync<Exception>(() => new IdvpPackageReader().ValidateAsync(
            tampered,
            options: new IdvpValidationOptions { ExtractFiles = false }));
    }

    [Fact]
    public async Task PackageCarryingSharedSdkIsRejected()
    {
        using var fixture = new PluginPackageTestFixture();
        var sdkPath = typeof(IdentityVisionBridge.PluginSdk.IIdvbPlugin).Assembly.Location;
        File.Copy(sdkPath, Path.Combine(fixture.Source, Path.GetFileName(sdkPath)));

        await Assert.ThrowsAsync<IdvpPackageException>(() => fixture.PackAsync());
    }

    [Fact]
    public async Task ExtractionRequiresAnEmptyDestination()
    {
        using var fixture = new PluginPackageTestFixture();
        var path = await fixture.PackAsync();
        var destination = Path.Combine(fixture.Root, "extract");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "existing.txt"), "keep");

        await Assert.ThrowsAsync<IdvpPackageException>(() => new IdvpPackageReader().ValidateAsync(
            path,
            destination,
            new IdvpValidationOptions { ExtractFiles = true }));
    }

    [Fact]
    public async Task PathTraversalEntryIsRejected()
    {
        using var fixture = new PluginPackageTestFixture();
        var path = await fixture.PackAsync(signed: false);
        var mutated = Path.Combine(fixture.Root, "traversal.idvp");
        await IdvpMutationHelper.RewriteAsync(
            path,
            mutated,
            static (entryPath, data) => (true, entryPath, data),
            [("../escape.txt", Encoding.UTF8.GetBytes("escape"))]);

        await Assert.ThrowsAsync<IdvpPackageException>(() => new IdvpPackageReader().ValidateAsync(
            mutated,
            options: new IdvpValidationOptions { AllowUnsigned = true, ExtractFiles = false }));
    }

    [Fact]
    public async Task ExtraAndMissingPayloadFilesAreRejected()
    {
        using var fixture = new PluginPackageTestFixture();
        var path = await fixture.PackAsync(signed: false);
        var extra = Path.Combine(fixture.Root, "extra.idvp");
        await IdvpMutationHelper.RewriteAsync(
            path,
            extra,
            static (entryPath, data) => (true, entryPath, data),
            [("extra.txt", Encoding.UTF8.GetBytes("extra"))]);
        var missing = Path.Combine(fixture.Root, "missing.idvp");
        await IdvpMutationHelper.RewriteAsync(
            path,
            missing,
            static (entryPath, data) =>
                (entryPath != "IDVB.Sample.MatchNotifier.deps.json", entryPath, data));

        var reader = new IdvpPackageReader();
        await Assert.ThrowsAsync<IdvpPackageException>(() => reader.ValidateAsync(
            extra,
            options: new IdvpValidationOptions { AllowUnsigned = true, ExtractFiles = false }));
        await Assert.ThrowsAsync<IdvpPackageException>(() => reader.ValidateAsync(
            missing,
            options: new IdvpValidationOptions { AllowUnsigned = true, ExtractFiles = false }));
    }

    [Fact]
    public async Task CaseConflictingAndHashMismatchedFilesAreRejected()
    {
        using var fixture = new PluginPackageTestFixture();
        var path = await fixture.PackAsync(signed: false);
        var duplicate = Path.Combine(fixture.Root, "duplicate.idvp");
        await IdvpMutationHelper.RewriteAsync(
            path,
            duplicate,
            static (entryPath, data) => (true, entryPath, data),
            [("MANIFEST.JSON", Encoding.UTF8.GetBytes("{}"))]);
        var hashMismatch = Path.Combine(fixture.Root, "hash.idvp");
        await IdvpMutationHelper.RewriteAsync(
            path,
            hashMismatch,
            static (entryPath, data) =>
            {
                if (entryPath == "IDVB.Sample.MatchNotifier.dll") data[0] ^= 0x01;
                return (true, entryPath, data);
            });

        var reader = new IdvpPackageReader();
        await Assert.ThrowsAsync<IdvpPackageException>(() => reader.ValidateAsync(
            duplicate,
            options: new IdvpValidationOptions { AllowUnsigned = true, ExtractFiles = false }));
        await Assert.ThrowsAsync<IdvpPackageException>(() => reader.ValidateAsync(
            hashMismatch,
            options: new IdvpValidationOptions { AllowUnsigned = true, ExtractFiles = false }));
    }

    [Fact]
    public async Task InvalidSignatureIsRejected()
    {
        using var fixture = new PluginPackageTestFixture();
        var path = await fixture.PackAsync();
        var invalid = Path.Combine(fixture.Root, "invalid-signature.idvp");
        await IdvpMutationHelper.RewriteAsync(
            path,
            invalid,
            static (entryPath, data) =>
            {
                if (entryPath != "signature.json") return (true, entryPath, data);
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                var signature = root.GetProperty("signature").GetString()!;
                var replacement = signature[..^2] + "AA";
                var changed = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    algorithm = root.GetProperty("algorithm").GetString(),
                    keyId = root.GetProperty("keyId").GetString(),
                    publicKeySpki = root.GetProperty("publicKeySpki").GetString(),
                    signature = replacement
                });
                return (true, entryPath, changed);
            });

        await Assert.ThrowsAsync<IdvpPackageException>(() => new IdvpPackageReader().ValidateAsync(
            invalid,
            options: new IdvpValidationOptions { ExtractFiles = false }));
    }

    [Fact]
    public async Task EntryTypeMismatchIsRejectedDuringPackageValidation()
    {
        using var fixture = new PluginPackageTestFixture();
        var manifest = fixture.CreateManifest() with
        {
            EntryPoint = fixture.CreateManifest().EntryPoint with { Type = "Missing.PluginType" }
        };

        await Assert.ThrowsAsync<IdvpPackageException>(() => fixture.PackAsync(manifest));
    }

    [Fact]
    public async Task ManagedHostAssemblyReferenceIsRejected()
    {
        using var fixture = new PluginPackageTestFixture();
        var hostCoupledAssembly = typeof(IDVBuff.Plugins.AutoClicker.AutoClickerPlugin).Assembly.Location;
        File.Copy(
            hostCoupledAssembly,
            Path.Combine(fixture.Source, Path.GetFileName(hostCoupledAssembly)));

        await Assert.ThrowsAsync<IdvpPackageException>(() => fixture.PackAsync());
    }

    [Fact]
    public async Task UnknownCapabilityAndFalseRiskDeclarationAreRejected()
    {
        using var fixture = new PluginPackageTestFixture();
        var unknownCapability = fixture.CreateManifest(capabilities: ["host.internal.write"]);
        var falseRisk = fixture.CreateManifest() with
        {
            Risks = new IdvpRiskDeclarations { NativeCode = true }
        };

        await Assert.ThrowsAsync<IdvpPackageException>(() => fixture.PackAsync(unknownCapability));
        await Assert.ThrowsAsync<IdvpPackageException>(() => fixture.PackAsync(falseRisk));
    }
}
