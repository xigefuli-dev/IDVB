using IDVBuff.Features.Maps;
using IDVBuff.UpdateCore;

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
            var firstSource = await source.SaveAsync(CreateDraft(
                root, "subscription-v1.png", "S1", "订阅地图 v1"));
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
                "v1");
            var localClass = firstPromotion.ClassBindings["S1"];
            var oldLocalMap = Assert.Single(
                await target.GetMapsAsync(),
                map => firstPromotion.InstalledMapIds.Contains(map.Id));

            var updatedDraft = await source.CreateDraftAsync(firstSource.Id);
            Assert.NotNull(updatedDraft);
            updatedDraft!.Title = "订阅地图 v2";
            await source.SaveAsync(updatedDraft);
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
                "v2");

            var current = Assert.Single(
                await target.GetMapsAsync(),
                map => secondPromotion.InstalledMapIds.Contains(map.Id));
            Assert.Equal("订阅地图 v2", current.Title);
            Assert.Equal(localClass, current.Class);
            Assert.Equal(MapAcquisitionKind.Subscription, current.AcquisitionKind);
            Assert.Equal("@mapper", current.SubscriptionPublisherHandle);
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
}
