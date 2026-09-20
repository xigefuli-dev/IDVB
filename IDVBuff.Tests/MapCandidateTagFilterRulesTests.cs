using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests;

public sealed class MapCandidateTagFilterRulesTests
{
    [Fact]
    public void FilterChoices_WhenNoTagsSelected_ReturnsAllChoicesWithOriginalIndices()
    {
        var choices = CreateSampleChoices();

        var filtered = MapCandidateTagFilterRules.FilterChoices(choices, null);

        Assert.Equal(3, filtered.Count);
        Assert.Equal(0, filtered[0].OriginalIndex);
        Assert.Equal(1, filtered[1].OriginalIndex);
        Assert.Equal(2, filtered[2].OriginalIndex);
        Assert.Equal("Map A", filtered[0].Choice.Recognition.Map.DisplayName);
    }

    [Fact]
    public void FilterChoices_WhenFilteringBySingleTag_ReturnsOnlyMatchingChoices()
    {
        var choices = CreateSampleChoices();
        var tagGroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var selected = new Dictionary<Guid, string>
        {
            [tagGroupId] = "大门在上"
        };

        var filtered = MapCandidateTagFilterRules.FilterChoices(choices, selected);

        Assert.Equal(2, filtered.Count);
        Assert.Equal(0, filtered[0].OriginalIndex);
        Assert.Equal("Map A", filtered[0].Choice.Recognition.Map.DisplayName);
        Assert.Equal(2, filtered[1].OriginalIndex);
        Assert.Equal("Map C", filtered[1].Choice.Recognition.Map.DisplayName);
    }

    [Fact]
    public void FilterChoices_WhenMultipleTagFiltersApplied_ReturnsIntersection()
    {
        var choices = CreateSampleChoices();
        var group1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var group2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var selected = new Dictionary<Guid, string>
        {
            [group1] = "大门在上",
            [group2] = "地下室在左"
        };

        var filtered = MapCandidateTagFilterRules.FilterChoices(choices, selected);

        Assert.Single(filtered);
        Assert.Equal(0, filtered[0].OriginalIndex);
        Assert.Equal("Map A", filtered[0].Choice.Recognition.Map.DisplayName);
    }

    [Fact]
    public void FilterChoices_WhenNoMapMatches_ReturnsEmpty()
    {
        var choices = CreateSampleChoices();
        var group1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var selected = new Dictionary<Guid, string>
        {
            [group1] = "不存在的标签"
        };

        var filtered = MapCandidateTagFilterRules.FilterChoices(choices, selected);

        Assert.Empty(filtered);
    }

    [Fact]
    public void ResolveAvailableTags_CombinesGroupTagsAndMapRecordTagsWithoutDuplicates()
    {
        var groupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var group = new MapTagGroup
        {
            Id = groupId,
            Name = "门方位",
            Tags = ["上门", "下门"]
        };

        var maps = new List<MapRecord>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Class = "军工厂",
                Tags = { [groupId] = "中门" }
            },
            new()
            {
                Id = Guid.NewGuid(),
                Class = "红教堂",
                Tags = { [groupId] = "侧门" } // 属于其他地图类，不应包含
            }
        };

        var resolved = MapCandidateTagFilterRules.ResolveAvailableTags(group, "军工厂", maps);

        Assert.Contains("上门", resolved);
        Assert.Contains("下门", resolved);
        Assert.Contains("中门", resolved);
        Assert.DoesNotContain("侧门", resolved);
    }

    [Fact]
    public void MapRuntimeSettings_SelectMapByTagsEnabled_DefaultsToFalseAndClonesCorrectly()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        Assert.False(settings.SelectMapByTagsEnabled);

        var clone = settings.Clone();
        Assert.False(clone.SelectMapByTagsEnabled);

        clone.SelectMapByTagsEnabled = true;
        Assert.False(settings.SelectMapByTagsEnabled);
        Assert.True(clone.SelectMapByTagsEnabled);
    }

    [Fact]
    public void MapRuntimeSettings_SelectMapByTagsEnabled_MigratesOnceThenPreservesUserChoice()
    {
        var settings = new MapRuntimeSettings
        {
            SchemaVersion = 16,
            SelectMapByTagsEnabled = true
        };

        settings.Normalize();
        Assert.False(settings.SelectMapByTagsEnabled);

        settings.SelectMapByTagsEnabled = true;
        settings.Normalize();
        Assert.True(settings.SelectMapByTagsEnabled);
    }

    private static List<MapRecognitionChoice> CreateSampleChoices()
    {
        var group1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var group2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var mapA = new MapRecord
        {
            Id = Guid.NewGuid(),
            Title = "Map A",
            Class = "军工厂",
            Tags =
            {
                [group1] = "大门在上",
                [group2] = "地下室在左"
            }
        };

        var mapB = new MapRecord
        {
            Id = Guid.NewGuid(),
            Title = "Map B",
            Class = "军工厂",
            Tags =
            {
                [group1] = "大门在下",
                [group2] = "地下室在左"
            }
        };

        var mapC = new MapRecord
        {
            Id = Guid.NewGuid(),
            Title = "Map C",
            Class = "军工厂",
            Tags =
            {
                [group1] = "大门在上",
                [group2] = "地下室在右"
            }
        };

        return
        [
            CreateChoice(mapA),
            CreateChoice(mapB),
            CreateChoice(mapC)
        ];
    }

    private static MapRecognitionChoice CreateChoice(MapRecord map) =>
        new()
        {
            Recognition = new RuntimeMapRecognition
            {
                Map = map,
                FloorImagePath = string.Empty,
                Result = new MapRecognitionResult
                {
                    MapId = map.Id,
                    Floor = "1f",
                    Confidence = 0.5d
                }
            }
        };
}
