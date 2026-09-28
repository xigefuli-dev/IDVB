using IDVBuff.Features.Maps;
using IDVBuff.UpdateCore;
using System.Text.Json.Nodes;

namespace IDVBuff.Tests;

public sealed partial class IdvmPackageServiceTests
{
    [Fact]
    public void SubscriptionReconciliationRemovesRecordWhenAllOwnedMapsWereDeleted()
    {
        var record = new MapSubscriptionRecord
        {
            InstalledMapIds = [Guid.NewGuid()]
        };

        Assert.Equal(
            MapSubscriptionReconciliationAction.RemoveSubscription,
            MapSubscriptionReconciliation.Evaluate(record, new HashSet<Guid>()));
    }

    [Fact]
    public void SubscriptionReconciliationForcesReapplyWhenOnlySomeOwnedMapsAreMissing()
    {
        var presentId = Guid.NewGuid();
        var record = new MapSubscriptionRecord
        {
            InstalledMapIds = [presentId, Guid.NewGuid()]
        };

        Assert.Equal(
            MapSubscriptionReconciliationAction.ForceReapply,
            MapSubscriptionReconciliation.Evaluate(record, new HashSet<Guid> { presentId }));
    }

    [Fact]
    public async Task SubscriptionPromotionKeepsLocalClassAndAtomicallyReplacesOwnedMaps()
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            await source.CreateClassAsync("S1");
            var firstSource = await source.SaveAsync(CreateDraft(
                root, "subscription-v1.png", "S1", "订阅地图 v1"));
            var algorithmPath = Path.Combine(root, "subscription-structure.idva");
            await File.WriteAllTextAsync(algorithmPath, NormalIdva);
            await source.GeneratePrebuiltStructureLinesAsync("S1", algorithmPath);
            Assert.Equal(MapAcquisitionKind.Local, firstSource.AcquisitionKind);
            var firstPackage = Path.Combine(root, "subscription-v1.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.AllClasses, null, firstPackage);

            var target = new MapRepository(Path.Combine(root, "target"));
            var targetPackages = new IdvmPackageService(target);
            var firstPlan = await targetPackages.InspectAsync(firstPackage);
            var firstSourceNames = firstPlan.Classes.Select(item => item.SourceName).ToArray();
            var firstImport = await targetPackages.ImportAsync(firstPlan);
            Assert.All(firstImport.ImportedMaps, map =>
                Assert.Equal(MapAcquisitionKind.ImportedPackage, map.AcquisitionKind));
            var firstPromotion = await target.PromoteSubscriptionImportAsync(
                firstSourceNames.Zip(firstImport.CreatedClasses,
                    (sourceName, localName) => new MapSubscriptionImportedClass(sourceName, localName)).ToArray(),
                new Dictionary<string, string>(),
                [],
                Guid.NewGuid(),
                "@mapper",
                new string('A', 64),
                "v1", isOfficialPublisher: true, isBuilderPublisher: true);
            var localClass = firstPromotion.ClassBindings["S1"];
            var oldLocalMap = Assert.Single(
                await target.GetMapsAsync(),
                map => firstPromotion.InstalledMapIds.Contains(map.Id));

            var updatedDraft = await source.CreateDraftAsync(firstSource.Id);
            Assert.NotNull(updatedDraft);
            updatedDraft!.Title = "订阅地图 v2";
            await source.SaveAsync(updatedDraft);
            await source.GeneratePrebuiltStructureLinesAsync("S1", algorithmPath);
            var secondPackage = Path.Combine(root, "subscription-v2.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.AllClasses, null, secondPackage);
            var secondPlan = await targetPackages.InspectAsync(secondPackage);
            var secondSourceNames = secondPlan.Classes.Select(item => item.SourceName).ToArray();
            var secondImport = await targetPackages.ImportAsync(secondPlan);

            var secondPromotion = await target.PromoteSubscriptionImportAsync(
                secondSourceNames.Zip(secondImport.CreatedClasses,
                    (sourceName, localName) => new MapSubscriptionImportedClass(sourceName, localName)).ToArray(),
                firstPromotion.ClassBindings,
                firstPromotion.InstalledMapIds,
                Guid.NewGuid(),
                "@mapper",
                new string('A', 64),
                "v2", isOfficialPublisher: true, isBuilderPublisher: true);

            var current = Assert.Single(
                await target.GetMapsAsync(),
                map => secondPromotion.InstalledMapIds.Contains(map.Id));
            Assert.Equal("订阅地图 v2", current.Title);
            Assert.Equal(localClass, current.Class);
            Assert.Equal(MapAcquisitionKind.Subscription, current.AcquisitionKind);
            Assert.Equal("@mapper", current.SubscriptionPublisherHandle);
            Assert.True(current.SubscriptionPublisherIsOfficial);
            Assert.True(current.SubscriptionPublisherIsBuilder);
            Assert.Equal("v2", current.SubscriptionVersion);
            Assert.DoesNotContain((await target.GetMapsAsync()), map => map.Id == oldLocalMap.Id);
            Assert.True(Directory.Exists(Path.GetDirectoryName(target.GetFloorOnePath(oldLocalMap))));
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(root, "target"), ".subscription-retired-*.json"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("missing-prebuilt")]
    [InlineData("missing-anchor")]
    [InlineData("missing-both-anchors")]
    [InlineData("unreadable-feature")]
    public async Task SubscriptionPromotionKeepsOldScannableMapWhenReplacementLosesFeature(
        string failureKind)
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            await source.CreateClassAsync("S1");
            await source.SaveAsync(CreateDraft(
                root, "subscription-source.png", "S1", "可扫描地图"));
            var algorithmPath = Path.Combine(root, "subscription-structure.idva");
            await File.WriteAllTextAsync(algorithmPath, NormalIdva);
            await source.GeneratePrebuiltStructureLinesAsync("S1", algorithmPath);
            var package = Path.Combine(root, "healthy.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.AllClasses, null, package);

            var target = new MapRepository(Path.Combine(root, "target"));
            var packages = new IdvmPackageService(target);
            var first = await packages.ImportAsync(await packages.InspectAsync(package));
            var firstPromotion = await target.PromoteSubscriptionImportAsync(
                [new MapSubscriptionImportedClass("S1", first.CreatedClasses.Single())],
                new Dictionary<string, string>(), [], Guid.NewGuid(), "@mapper",
                new string('A', 64), "v1");
            var installedId = Assert.Single(firstPromotion.InstalledMapIds);
            var installed = Assert.Single(await target.GetMapsAsync(), map => map.Id == installedId);
            Assert.True(target.TryGetValidSideEntranceFeaturePath(
                installed, "1f", out _, out var initialFailure), initialFailure);

            var brokenPackage = Path.Combine(root, "broken.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.AllClasses, null, brokenPackage);
            var second = await packages.ImportAsync(await packages.InspectAsync(brokenPackage));
            var catalogPath = Path.Combine(root, "target", "maps.json");
            var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!;
            var importedMap = catalog["Maps"]!.AsArray().Single(map =>
                map!["Id"]!.GetValue<Guid>() == second.ImportedMaps.Single().Id)!;
            if (failureKind is "missing-anchor" or "missing-both-anchors")
            {
                var anchors = importedMap["Recognition"]!["Floors"]!["1f"]!["Anchors"]!.AsArray();
                anchors.Single(anchor => anchor!["Key"]!.GetValue<string>() == "side-entrance")!
                    .AsObject()["Bounds"] = null;
                if (failureKind == "missing-both-anchors")
                    anchors.Single(anchor => anchor!["Key"]!.GetValue<string>() == "main-entrance")!
                        .AsObject()["Bounds"] = null;
            }
            else if (failureKind == "missing-prebuilt")
            {
                importedMap["Floors"]!.AsArray()[0]!.AsObject()["PrebuiltStructureLine"] = null;
            }
            else
            {
                var feature = importedMap["Recognition"]!["Floors"]!["1f"]!;
                var featurePath = target.GetSideEntranceFeaturePath(second.ImportedMaps.Single(), "1f");
                var invalidPng = new byte[] { 1, 2, 3, 4, 5 };
                await File.WriteAllBytesAsync(featurePath, invalidPng);
                feature["SideEntranceFeatureSha256"] = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(invalidPng)).ToLowerInvariant();
            }
            await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                target.PromoteSubscriptionImportAsync(
                    [new MapSubscriptionImportedClass("S1", second.CreatedClasses.Single())],
                    firstPromotion.ClassBindings, firstPromotion.InstalledMapIds,
                    Guid.NewGuid(), "@mapper", new string('A', 64), "v2"));

            installed = Assert.Single(await target.GetMapsAsync(), map => map.Id == installedId);
            Assert.Equal("v1", installed.SubscriptionVersion);
            Assert.True(target.TryGetValidSideEntranceFeaturePath(
                installed, "1f", out _, out var failure), failure);
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
