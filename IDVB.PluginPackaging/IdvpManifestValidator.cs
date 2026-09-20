using IdentityVisionBridge.PluginSdk;

namespace IdentityVisionBridge.PluginPackaging;

internal static class IdvpManifestValidator
{
    private static readonly HashSet<string> SettingTypes = new(StringComparer.Ordinal)
    {
        "toggle", "slider", "choice", "keyBinding"
    };

    public static void Validate(IdvpManifest manifest, bool isSigned)
    {
        if (manifest.Format != "idvb-plugin" || manifest.FormatVersion != IdvpConstants.FormatVersion)
        {
            throw new IdvpPackageException("The manifest format is not IDVP v1.");
        }

        RequireIdentifier(manifest.Id, "plugin ID");
        RequireIdentifier(manifest.Publisher.Id, "publisher ID");
        RequireText(manifest.DisplayName, 128, "display name");
        RequireText(manifest.Description, 4096, "description");
        RequireText(manifest.Publisher.Name, 128, "publisher name");
        if (!IdvpPathRules.IsSemanticVersion(manifest.Version))
        {
            throw new IdvpPackageException("The plugin version must be SemVer 2.0.");
        }

        if (isSigned && (manifest.Publisher.KeyId is null || manifest.Publisher.KeyId.Length != 64))
        {
            throw new IdvpPackageException("A signed manifest must contain the publisher key ID.");
        }

        if (manifest.Compatibility.TargetFramework != "net10.0" ||
            manifest.Compatibility.RuntimeIdentifier != "win-x64")
        {
            throw new IdvpPackageException("IDVP v1 only supports net10.0 and win-x64.");
        }

        RequireText(manifest.Compatibility.PluginApi, 128, "Plugin API range");
        RequireText(manifest.Compatibility.Host, 128, "host version range");
        ValidateEntryPoint(manifest.EntryPoint);
        ValidateUnique(manifest.Capabilities, "capability");
        foreach (var capability in manifest.Capabilities)
        {
            if (!PluginCapabilityIds.PublicV1.Contains(capability))
            {
                throw new IdvpPackageException($"Unknown or non-public capability: {capability}");
            }
        }

        ValidateSettings(manifest.Settings);
        ValidateCommands(manifest.Commands);
        ValidateFiles(manifest.Files);

        var hasNativeCode = manifest.Files.Any(static file =>
            file.Path.StartsWith("runtimes/win-x64/native/", StringComparison.Ordinal));
        if (hasNativeCode != manifest.Risks.NativeCode)
        {
            throw new IdvpPackageException("The nativeCode risk declaration does not match the package contents.");
        }
    }

    private static void ValidateEntryPoint(IdvpEntryPoint entryPoint)
    {
        var assemblyPath = IdvpPathRules.ValidateArchivePath(entryPoint.Assembly);
        if (!assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            assemblyPath.StartsWith("runtimes/", StringComparison.Ordinal))
        {
            throw new IdvpPackageException("The entry assembly must be a managed DLL outside runtimes/.");
        }

        RequireText(entryPoint.Type, 512, "entry type");
    }

    private static void ValidateSettings(IReadOnlyList<IdvpSettingDefinition> settings)
    {
        ValidateUnique(settings.Select(static setting => setting.Key), "setting key");
        foreach (var setting in settings)
        {
            RequireIdentifier(setting.Key, "setting key");
            RequireText(setting.DisplayName, 128, "setting display name");
            if (!SettingTypes.Contains(setting.Type))
            {
                throw new IdvpPackageException($"Unsupported setting type: {setting.Type}");
            }

            if (setting.Default.ValueKind is System.Text.Json.JsonValueKind.Undefined or
                System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Object or
                System.Text.Json.JsonValueKind.Array)
            {
                throw new IdvpPackageException($"Setting {setting.Key} has a non-primitive default value.");
            }

            if (setting.Type == "toggle" &&
                setting.Default.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            {
                throw new IdvpPackageException($"Toggle setting {setting.Key} must have a boolean default.");
            }

            if (setting.Type == "slider")
            {
                if (setting.Default.ValueKind != System.Text.Json.JsonValueKind.Number ||
                    !setting.Default.TryGetDouble(out var defaultNumber) || !double.IsFinite(defaultNumber) ||
                    setting.Minimum is null || setting.Maximum is null || setting.Step is null ||
                    !double.IsFinite(setting.Minimum.Value) || !double.IsFinite(setting.Maximum.Value) ||
                    !double.IsFinite(setting.Step.Value) || setting.Minimum > setting.Maximum || setting.Step <= 0 ||
                    defaultNumber < setting.Minimum || defaultNumber > setting.Maximum)
                {
                    throw new IdvpPackageException($"Slider setting {setting.Key} has an invalid range.");
                }
            }

            if (setting.Type == "choice")
            {
                ValidateUnique(setting.Options.Select(static option => option.Value), $"choice value for {setting.Key}");
                if (setting.Options.Count == 0 || setting.Default.ValueKind != System.Text.Json.JsonValueKind.String ||
                    !setting.Options.Any(option => option.Value == setting.Default.GetString()))
                {
                    throw new IdvpPackageException($"Choice setting {setting.Key} has an invalid default or no options.");
                }

                foreach (var option in setting.Options)
                {
                    RequireText(option.Value, 256, $"choice value for {setting.Key}");
                    RequireText(option.DisplayName, 128, $"choice display name for {setting.Key}");
                }
            }

            if (setting.Type == "keyBinding" &&
                (setting.Default.ValueKind != System.Text.Json.JsonValueKind.String ||
                 setting.Default.GetString() is not { Length: > 0 and <= 128 }))
            {
                throw new IdvpPackageException($"Key binding setting {setting.Key} must have a short string default.");
            }
        }
    }

    private static void ValidateCommands(IReadOnlyList<IdvpCommandDefinition> commands)
    {
        ValidateUnique(commands.Select(static command => command.Id), "command ID");
        foreach (var command in commands)
        {
            RequireIdentifier(command.Id, "command ID");
            RequireText(command.DisplayName, 128, "command display name");
        }
    }

    private static void ValidateFiles(IReadOnlyList<IdvpFileEntry> files)
    {
        if (files.Count == 0 || files.Count > IdvpConstants.MaxEntries - 2)
        {
            throw new IdvpPackageException("The manifest file list is empty or exceeds the entry limit.");
        }

        ValidateUnique(files.Select(static file => IdvpPathRules.ValidateArchivePath(file.Path)), "file path", StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (var file in files)
        {
            if (file.Path is "manifest.json" or "signature.json" ||
                file.Length < 0 || file.Length > IdvpConstants.MaxSingleFileBytes || file.Sha256.Length != 64)
            {
                throw new IdvpPackageException($"Invalid manifest file entry: {file.Path}");
            }

            try
            {
                _ = Convert.FromHexString(file.Sha256);
            }
            catch (FormatException exception)
            {
                throw new IdvpPackageException($"Invalid SHA-256 for {file.Path}.", exception);
            }

            totalLength = checked(totalLength + file.Length);
        }

        if (totalLength > IdvpConstants.MaxExpandedBytes)
        {
            throw new IdvpPackageException("The manifest expanded-size total exceeds the package limit.");
        }
    }

    private static void ValidateUnique(IEnumerable<string> values, string label, IEqualityComparer<string>? comparer = null)
    {
        var seen = new HashSet<string>(comparer ?? StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new IdvpPackageException($"Duplicate {label}: {value}");
            }
        }
    }

    private static void RequireIdentifier(string value, string label)
    {
        if (!IdvpPathRules.IsIdentifier(value))
        {
            throw new IdvpPackageException($"Invalid {label}: {value}");
        }
    }

    private static void RequireText(string value, int maxLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new IdvpPackageException($"The manifest {label} is empty or too long.");
        }
    }
}
