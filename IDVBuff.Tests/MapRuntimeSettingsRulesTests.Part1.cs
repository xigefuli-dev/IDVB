using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;
public sealed partial class MapRuntimeSettingsRulesTests
{

    [Theory]
    [InlineData(0xC0u, "`")]
    [InlineData(0x60u, "小键盘 0")]
    [InlineData(0xBAu, ";")]
    [InlineData(0xA6u, "浏览器后退")]
    public void MapInputBinding_UsesReadableNamesForNonAlphanumericKeys(
        uint virtualKey,
        string expectedName)
    {
        var binding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = virtualKey
        };

        Assert.Equal(expectedName, binding.DisplayName);
        Assert.DoesNotContain("VK", binding.DisplayName,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapInputBinding_DisplaysAndClonesOrdinaryKeyCombinations()
    {
        var binding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = 0x72,
            CompanionVirtualKeys = [0x51]
        };

        var clone = binding.Clone();

        Assert.Equal("Q + F3", binding.DisplayName);
        Assert.Equal(binding, clone);
        Assert.Equal([0x51u], clone.CompanionVirtualKeys);
    }

    [Fact]
    public void ControlPanelBindingRoundTripsWithCurrentSchema()
    {
        var json = JsonSerializer.Serialize(new MapRuntimeSettings
        {
            ControlPanelToggleBinding = new MapInputBinding
            {
                Kind = MapInputBindingKind.Keyboard,
                VirtualKey = 116
            }
        });

        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        restored.Normalize();

        Assert.Equal(
            MapRuntimeSettings.CurrentSchemaVersion,
            restored.SchemaVersion);
        Assert.Equal(116u, restored.ControlPanelToggleBinding.VirtualKey);
    }

    [Fact]
    public void BackgroundScanEnabledRoundTripsWithCurrentSchema()
    {
        var settings = new MapRuntimeSettings
        {
            BackgroundScanEnabled = true
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        restored.Normalize();

        Assert.Equal(
            MapRuntimeSettings.CurrentSchemaVersion,
            restored.SchemaVersion);
        Assert.True(restored.BackgroundScanEnabled);
    }

    [Fact]
    public void StrictScanStructureRegistrationDefaultsOnAndRoundTrips()
    {
        var legacy = JsonSerializer.Deserialize<MapRuntimeSettings>("{}")!;
        var settings = new MapRuntimeSettings
        {
            RequireStrictStructureRegistrationDuringScan = false
        };

        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(
            JsonSerializer.Serialize(settings))!;

        Assert.True(legacy.RequireStrictStructureRegistrationDuringScan);
        Assert.False(restored.RequireStrictStructureRegistrationDuringScan);
        Assert.False(settings.Clone()
            .RequireStrictStructureRegistrationDuringScan);
    }

    [Fact]
    public void ContinuousAlignmentIsDisabledWhenSettingsAreNormalized()
    {
        var settings = new MapRuntimeSettings
        {
            EnableContinuousAlignment = true
        };

        settings.Normalize();

        Assert.False(settings.EnableContinuousAlignment);
    }

    [Fact]
    public void LegacySettingsWithoutBackgroundScanDefaultToDisabled()
    {
        var json = """
        {
          "SchemaVersion": 12,
          "FirstScanStrategy": 0
        }
        """;

        var settings = JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        settings.Normalize();

        Assert.False(settings.BackgroundScanEnabled);
        Assert.Null(settings.LastSelectedMapClass);
        Assert.Equal(
            MapRuntimeSettings.CurrentSchemaVersion,
            settings.SchemaVersion);
    }

    [Fact]
    public async Task SchemaTwelveSettingsMigrateAndPersistNewMapClassPreference()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.Settings.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 12,
                  "FirstScanStrategy": 0
                }
                """);

            var settings = await new MapRuntimeSettingsRepository(root).LoadAsync();

            Assert.Equal(MapRuntimeSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Null(settings.LastSelectedMapClass);
            using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(
                MapRuntimeSettings.CurrentSchemaVersion,
                stored.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.True(
                stored.RootElement.TryGetProperty(
                    "LastSelectedMapClass",
                    out var lastSelectedMapClass));
            Assert.Equal(JsonValueKind.Null, lastSelectedMapClass.ValueKind);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LastSelectedMapClassNormalizesAndRoundTrips()
    {
        var settings = new MapRuntimeSettings
        {
            LastSelectedMapClass = "  Ranked  "
        };

        settings.Normalize();
        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(
            JsonSerializer.Serialize(settings))!;
        restored.Normalize();

        Assert.Equal("Ranked", settings.LastSelectedMapClass);
        Assert.Equal("Ranked", restored.LastSelectedMapClass);
    }

    [Fact]
    public void LastSelectedMapClassClonesWithSettings()
    {
        var settings = new MapRuntimeSettings
        {
            LastSelectedMapClass = "Ranked"
        };

        var clone = settings.Clone();
        clone.LastSelectedMapClass = "Quick";

        Assert.Equal("Ranked", settings.LastSelectedMapClass);
        Assert.Equal("Quick", clone.LastSelectedMapClass);
    }

    [Fact]
    public void ResolveMapClassUsesCanonicalCaseInsensitiveMatchAndFirstItemFallback()
    {
        var classes = new[] { "S1", " Ranked ", "S1" };

        Assert.Equal(
            "Ranked",
            MapRuntimeSettingsRules.ResolveMapClass(classes, " ranked "));
        Assert.Equal(
            "S1",
            MapRuntimeSettingsRules.ResolveMapClass(classes, "Deleted"));
        Assert.Equal(
            "S1",
            MapRuntimeSettingsRules.ResolveMapClass(classes, null));
        Assert.Null(MapRuntimeSettingsRules.ResolveMapClass([], "S1"));
    }

    [Fact]
    public void BackgroundScanEnabledCloneIsIndependent()
    {
        var settings = new MapRuntimeSettings
        {
            BackgroundScanEnabled = true
        };

        var clone = settings.Clone();
        clone.BackgroundScanEnabled = false;

        Assert.True(settings.BackgroundScanEnabled);
        Assert.False(clone.BackgroundScanEnabled);
    }

    [Fact]
    public async Task CurrentSchemaSettingsWithoutBackgroundScanAreNotRewritten()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.Settings.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            var json = """
            {
              "SchemaVersion": 16,
              "OverlayAlignmentMode": 1,
              "ShowOverlayStatus": true
            }
            """;
            await File.WriteAllTextAsync(path, json);
            var repository = new MapRuntimeSettingsRepository(root);

            var settings = await repository.LoadAsync();

            Assert.Equal(
                MapRuntimeSettings.CurrentSchemaVersion,
                settings.SchemaVersion);
            Assert.False(settings.BackgroundScanEnabled);
            // schema 最新且无缺省字段触发迁移 → 文件保持原样，不重写。
            using var stored = JsonDocument.Parse(
                await File.ReadAllTextAsync(path));
            Assert.False(
                stored.RootElement.TryGetProperty(
                    "BackgroundScanEnabled",
                    out _));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

}
