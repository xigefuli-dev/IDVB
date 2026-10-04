using IDVBuff.Lifecycle;

namespace IDVBuff.Tests;

public sealed class LegacyLaunchDirectoryTests
{
    [Fact]
    public void LooseBuildIsNotLegacyInstallationEvenWithAnInstalledVelopackTarget()
    {
        var testBase = Path.Combine(Path.GetTempPath(), "IDVB-Tests");
        var root = Path.Combine(testBase, Guid.NewGuid().ToString("N"));
        var build = Path.Combine(root, "build");
        var install = Path.Combine(root, "installed");
        var current = Path.Combine(install, "current");
        try
        {
            Directory.CreateDirectory(build);
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(build, "IDVB.exe"), string.Empty);
            File.WriteAllText(Path.Combine(install, "IDVB.exe"), string.Empty);
            File.WriteAllText(Path.Combine(install, "Update.exe"), string.Empty);
            File.WriteAllText(Path.Combine(current, "IDVB.exe"), string.Empty);
            File.WriteAllText(Path.Combine(current, "sq.version"), "1.6.6");

            Assert.True(VelopackInstallLayout.IsValidLauncherPath(Path.Combine(install, "IDVB.exe")));
            Assert.False(VelopackInstallLayout.IsLegacyInnoInstallDirectory(build));
            Assert.False(VelopackInstallLayout.IsLegacyInnoInstallDirectory(current));
            Assert.False(VelopackInstallLayout.IsLegacyInnoInstallDirectory(Path.Combine(root, "missing")));

            File.WriteAllText(Path.Combine(current, "unins000.exe"), string.Empty);
            Assert.False(VelopackInstallLayout.IsLegacyInnoInstallDirectory(install));
            File.WriteAllText(Path.Combine(build, "unins000.dat"), string.Empty);
            Assert.False(VelopackInstallLayout.IsLegacyInnoInstallDirectory(build));
            File.WriteAllText(Path.Combine(build, "unins000.exe"), string.Empty);
            Assert.True(VelopackInstallLayout.IsLegacyInnoInstallDirectory(build));
        }
        finally
        {
            var absoluteRoot = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(testBase) + Path.DirectorySeparatorChar,
                absoluteRoot, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(absoluteRoot))
                Directory.Delete(absoluteRoot, recursive: true);
        }
    }

    [Fact]
    public void StartupRejectsLooseSourceBeforeReadingTheInstalledMigrationMarker()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IDVBuff.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "Lifecycle", "UpdateLifecycleState.cs"));
        var sourceGuard = source.IndexOf(
            "if (!VelopackInstallLayout.IsLegacyInnoInstallDirectory(AppContext.BaseDirectory))",
            StringComparison.Ordinal);
        var markerRead = source.IndexOf("var markerPath =", StringComparison.Ordinal);
        Assert.True(sourceGuard >= 0 && markerRead > sourceGuard);
        Assert.Contains("if (VelopackLocator.Current.CurrentlyInstalledVersion is not null)", source);
        Assert.Contains("--isolated-dev-instance", source);
    }
}
