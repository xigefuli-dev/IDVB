using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace IdentityVisionBridge.PluginPackaging;

internal static class IdvpAssemblyInspector
{
    private const string SdkAssemblyName = "IdentityVisionBridge.PluginSdk";

    public static void InspectEntryAssembly(Stream stream, string entryType)
    {
        using var buffered = BufferIfNeeded(stream);
        using var reader = new PEReader(buffered ?? stream, PEStreamOptions.LeaveOpen);
        if (!reader.HasMetadata)
        {
            throw new IdvpPackageException("The configured entry assembly is not a managed assembly.");
        }

        var metadata = reader.GetMetadataReader();
        ValidateAssemblyIdentity(metadata);
        var references = GetReferences(metadata);
        if (!references.Contains(SdkAssemblyName))
        {
            throw new IdvpPackageException("The entry assembly does not reference IdentityVisionBridge.PluginSdk.");
        }

        if (!TryFindType(metadata, entryType, out var entryTypeHandle))
        {
            throw new IdvpPackageException($"The entry type {entryType} was not found in the entry assembly.");
        }

        if (!DirectlyImplementsPlugin(metadata, entryTypeHandle))
        {
            throw new IdvpPackageException($"The entry type {entryType} does not directly implement IIdvbPlugin.");
        }
    }

    public static void InspectManagedDependency(Stream stream, string packagePath)
    {
        using var buffered = BufferIfNeeded(stream);
        using var reader = new PEReader(buffered ?? stream, PEStreamOptions.LeaveOpen);
        if (!reader.HasMetadata)
        {
            if (!packagePath.StartsWith("runtimes/win-x64/native/", StringComparison.Ordinal))
            {
                throw new IdvpPackageException($"Native libraries are only allowed below runtimes/win-x64/native: {packagePath}");
            }

            return;
        }

        var metadata = reader.GetMetadataReader();
        ValidateAssemblyIdentity(metadata);
        _ = GetReferences(metadata);
    }

    private static HashSet<string> GetReferences(MetadataReader metadata)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handle in metadata.AssemblyReferences)
        {
            var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            if (IsForbiddenHostReference(name))
            {
                throw new IdvpPackageException($"Third-party plugins may not reference host assembly {name}.");
            }

            if (IsForbiddenUiReference(name))
            {
                throw new IdvpPackageException($"IDVP v1 does not allow Windows App SDK or WinUI reference {name}.");
            }

            references.Add(name);
        }

        return references;
    }

    private static void ValidateAssemblyIdentity(MetadataReader metadata)
    {
        if (!metadata.IsAssembly)
        {
            throw new IdvpPackageException("A managed package DLL has no assembly identity.");
        }

        var assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        if (assemblyName.Equals(SdkAssemblyName, StringComparison.OrdinalIgnoreCase) ||
            IsForbiddenHostReference(assemblyName) || IsForbiddenUiReference(assemblyName))
        {
            throw new IdvpPackageException($"The package carries a reserved assembly: {assemblyName}");
        }
    }

    private static bool TryFindType(
        MetadataReader metadata,
        string fullName,
        out TypeDefinitionHandle typeHandle)
    {
        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var name = metadata.GetString(definition.Name);
            var typeNamespace = metadata.GetString(definition.Namespace);
            var candidate = string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";
            if (candidate.Equals(fullName, StringComparison.Ordinal))
            {
                typeHandle = handle;
                return true;
            }
        }

        typeHandle = default;
        return false;
    }

    private static bool DirectlyImplementsPlugin(MetadataReader metadata, TypeDefinitionHandle typeHandle)
    {
        var definition = metadata.GetTypeDefinition(typeHandle);
        foreach (var interfaceHandle in definition.GetInterfaceImplementations())
        {
            var implementation = metadata.GetInterfaceImplementation(interfaceHandle);
            if (implementation.Interface.Kind != HandleKind.TypeReference)
                continue;
            var reference = metadata.GetTypeReference((TypeReferenceHandle)implementation.Interface);
            if (metadata.GetString(reference.Namespace) == "IdentityVisionBridge.PluginSdk" &&
                metadata.GetString(reference.Name) == "IIdvbPlugin")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsForbiddenHostReference(string name) =>
        name.Equals("IDVB", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("IDVB.Core", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("IDVB.ModuleContracts", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("IDVB.Survey.", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("IDVB.PluginContracts", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("IDVB.PluginHostMessages", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("IDVBuff", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("IDVBuff.", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("IdentityVisionBridge.Core", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("IdentityVisionBridge.Features", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("IdentityVisionBridge.ModuleContracts", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("IdentityVisionBridge.Host", StringComparison.OrdinalIgnoreCase);

    private static bool IsForbiddenUiReference(string name) =>
        name.Equals("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.WinUI", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.UI", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WinRT.Runtime", StringComparison.OrdinalIgnoreCase);

    private static MemoryStream? BufferIfNeeded(Stream stream)
    {
        if (stream.CanRead && stream.CanSeek)
            return null;
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        return buffer;
    }
}
