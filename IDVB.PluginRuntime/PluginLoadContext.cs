using System.Reflection;
using System.Runtime.Loader;
using IdentityVisionBridge.PluginSdk;

namespace IdentityVisionBridge.PluginRuntime;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string SharedSdkAssemblyName = typeof(IIdvbPlugin).Assembly.GetName().Name!;
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string entryAssemblyPath)
        : base($"IDVB.Plugin:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == SharedSdkAssemblyName)
        {
            return typeof(IIdvbPlugin).Assembly;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
