using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapGpuTrainingSidecarTests
{
    [Fact]
    public void ResolveExecutable_FindsIgnoredRuntimeAboveBuildDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"idvb-gpu-resolve-{Guid.NewGuid():N}");
        try
        {
            var buildDirectory = Path.Combine(root, "bin", "Debug", "net10.0",
                "win-x64");
            var executable = Path.Combine(root, ".idvb-gpu", "runtime",
                "IDVB.RealCLI.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllBytes(executable, []);

            var resolved = MapGpuTrainingSidecar.ResolveExecutable(
                buildDirectory, buildDirectory, Path.Combine(root, "appdata"));

            Assert.Equal(executable, resolved,
                ignoreCase: true, ignoreLineEndingDifferences: false,
                ignoreWhiteSpaceDifferences: false);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
