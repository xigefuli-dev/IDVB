using System.Text.Json;
using IDVBuff.Survey.Domain;
using IDVBuff.Survey.Persistence.Sqlite;

namespace IDVBuff.Tests;

public sealed class SurveyTemplateStoreTests
{
    [Fact]
    public async Task TemplatesSurviveASecondStoreInstanceInDedicatedJsonFile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-SurveyTemplateTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "color-templates.json");
        var template = new SurveyColorTemplate(
            Guid.NewGuid(),
            "地图配色",
            [
                new SurveyColorTemplateEntry(220, 120, 35, SurveyTemplateColorType.Fill),
                new SurveyColorTemplateEntry(32, 64, 96, SurveyTemplateColorType.Outline)
            ]);

        try
        {
            var first = new JsonSurveyTemplateStore(path);
            await first.SaveAsync([template]);

            Assert.True(File.Exists(path));
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32());
                Assert.Equal(
                    "Fill",
                    json.RootElement
                        .GetProperty("Templates")[0]
                        .GetProperty("Entries")[0]
                        .GetProperty("Type")
                        .GetString());
            }

            var restored = await new JsonSurveyTemplateStore(path).LoadAsync();
            var actual = Assert.Single(restored);
            Assert.Equal(template.Id, actual.Id);
            Assert.Equal(template.Name, actual.Name);
            Assert.Equal(template.Entries, actual.Entries);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DamagedTemplateFileFallsBackToEmptyConfiguration()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-SurveyTemplateTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "color-templates.json");

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "{ not valid json");

            var templates = await new JsonSurveyTemplateStore(path).LoadAsync();

            Assert.Empty(templates);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingAnExistingTemplateIdReplacesTheSavedTemplate()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-SurveyTemplateTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "color-templates.json");
        var id = Guid.NewGuid();
        var original = new SurveyColorTemplate(
            id,
            "原始模板",
            [new SurveyColorTemplateEntry(10, 20, 30, SurveyTemplateColorType.Fill)]);
        var updated = new SurveyColorTemplate(
            id,
            "修改后的模板",
            [new SurveyColorTemplateEntry(200, 180, 160, SurveyTemplateColorType.Icon)]);

        try
        {
            var store = new JsonSurveyTemplateStore(path);
            await store.SaveAsync([original]);
            await store.SaveAsync([updated]);

            var restored = Assert.Single(await store.LoadAsync());
            Assert.Equal(updated.Id, restored.Id);
            Assert.Equal(updated.Name, restored.Name);
            Assert.Equal(updated.Entries, restored.Entries);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
