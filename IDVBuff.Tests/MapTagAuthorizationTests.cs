using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapTagAuthorizationTests
{
    [Fact]
    public void ExistingTagUsagePreservesClassAuthorization()
    {
        var groupId = Guid.NewGuid();
        var group = new MapTagGroup { Id = groupId, Name = "门方位" };
        var map = new MapRecord
        {
            Class = "S1",
            Tags = new Dictionary<Guid, string> { [groupId] = "正门" }
        };

        MapTagAuthorizationRules.PreserveUsedClassAuthorizations(
            [group], [map], ["S1", "S2"]);

        Assert.Contains("S1", group.AuthorizedClasses);
        Assert.True(MapTagAuthorizationRules.IsAuthorized(group, "S1", [map]));
        Assert.True(MapTagAuthorizationRules.IsUsedByClass(group, "S1", [map]));
    }

    [Fact]
    public void UnusedClassCanRemainUnauthorized()
    {
        var group = new MapTagGroup { Name = "门形状" };
        var map = new MapRecord { Class = "S1" };

        MapTagAuthorizationRules.PreserveUsedClassAuthorizations(
            [group], [map], ["S1", "S2"]);

        Assert.False(MapTagAuthorizationRules.IsAuthorized(group, "S2", [map]));
        Assert.False(MapTagAuthorizationRules.IsUsedByClass(group, "S2", [map]));
    }
}
