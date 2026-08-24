using System.Text.Json;
using IDVB.Sample.MatchNotifier;
using IdentityVisionBridge.PluginPackaging;

namespace IDVB.PluginSystem.Tests;

internal sealed class PluginPackageTestFixture : IDisposable
{
    public PluginPackageTestFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "idvb-plugin-tests", Guid.NewGuid().ToString("N"));
        Source = Path.Combine(Root, "source");
        Directory.CreateDirectory(Source);
        var assemblyPath = typeof(MatchNotifierPlugin).Assembly.Location;
        File.Copy(assemblyPath, Path.Combine(Source, Path.GetFileName(assemblyPath)));
        var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
        var destinationDepsPath = Path.Combine(Source, Path.GetFileName(depsPath));
        if (File.Exists(depsPath))
            File.Copy(depsPath, destinationDepsPath);
        else
            File.WriteAllText(destinationDepsPath, """
                {
                  "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0/win-x64", "signature": "" },
                  "compilationOptions": {},
                  "targets": {
                    ".NETCoreApp,Version=v10.0": {},
                    ".NETCoreApp,Version=v10.0/win-x64": {
                      "IDVB.Sample.MatchNotifier/1.0.0": {
                        "runtime": { "IDVB.Sample.MatchNotifier.dll": {} }
                      }
                    }
                  },
                  "libraries": {
                    "IDVB.Sample.MatchNotifier/1.0.0": {
                      "type": "project", "serviceable": false, "sha512": ""
                    }
                  }
                }
                """);
        ManifestPath = Path.Combine(Source, "manifest.json");
        PublisherPrivateKeyPath = Path.Combine(Root, "publisher-private.pem");
        File.WriteAllText(PublisherPrivateKeyPath, IdvpCrypto.CreatePublisherKey().PrivateKeyPem);
    }

    public string Root { get; }

    public string Source { get; }

    public string ManifestPath { get; }

    public string PublisherPrivateKeyPath { get; }

    public IdvpManifest CreateManifest(
        string version = "1.0.0",
        IReadOnlyList<string>? capabilities = null,
        string publisherId = "tests.publisher") => new()
    {
        Id = "tests.match-notifier",
        DisplayName = "Package Test Plugin",
        Version = version,
        Description = "IDVP package test plugin.",
        Publisher = new IdvpPublisher { Id = publisherId, Name = "Test Publisher" },
        EntryPoint = new IdvpEntryPoint
        {
            Assembly = "IDVB.Sample.MatchNotifier.dll",
            Type = typeof(MatchNotifierPlugin).FullName!
        },
        Compatibility = new IdvpCompatibility
        {
            PluginApi = ">=2.0.0 <3.0.0",
            Host = ">=1.5.0 <2.0.0"
        },
        Capabilities = capabilities ?? [],
        Settings =
        [
            new IdvpSettingDefinition
            {
                Key = "notify",
                Type = "toggle",
                DisplayName = "Notify",
                Default = JsonSerializer.SerializeToElement(true)
            }
        ],
        Commands = [new IdvpCommandDefinition { Id = "test-notification", DisplayName = "Test" }]
    };

    public async Task<string> PackAsync(
        IdvpManifest? manifest = null,
        bool signed = true,
        string fileName = "plugin.idvp",
        string? privateKeyPath = null)
    {
        manifest ??= CreateManifest();
        await File.WriteAllTextAsync(
            ManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var keyPath = signed ? privateKeyPath ?? PublisherPrivateKeyPath : null;

        var output = Path.Combine(Root, fileName);
        await new IdvpPackageWriter().PackAsync(new IdvpPackOptions
        {
            SourceDirectory = Source,
            ManifestPath = ManifestPath,
            OutputPath = output,
            PrivateKeyPemPath = keyPath
        });
        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
