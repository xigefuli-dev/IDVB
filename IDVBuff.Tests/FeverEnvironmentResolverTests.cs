using IDVBuff.Features.GameLaunch;

namespace IDVBuff.Tests;

public sealed class FeverEnvironmentResolverTests
{
    [Theory]
    [InlineData("\"D:\\FeverGames\\FeverGamesLauncher.exe\" \"%1\"", @"D:\FeverGames\FeverGamesLauncher.exe")]
    [InlineData("D:\\FeverGames\\FeverGamesLauncher.exe %1", @"D:\FeverGames\FeverGamesLauncher.exe")]
    [InlineData("\"C:\\Program Files\\FeverGames\\app.exe\"", @"C:\Program Files\FeverGames\app.exe")]
    public void ExtractExecutablePath_ExtractsCorrectPath(string input, string expected)
    {
        var result = FeverEnvironmentResolver.ExtractExecutablePath(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryResolveFromFeverRoot_PrefersHigherVersionDirectory()
    {
        const string feverRoot = @"D:\FeverGames";
        string[] dirs =
        [
            @"D:\FeverGames\1.9.5.0",
            @"D:\FeverGames\1.18.44.2",
            @"D:\FeverGames\1.2.0.0"
        ];

        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\FeverGames\1.9.5.0\mpay.dll",
            @"D:\FeverGames\1.9.5.0\skin.zip",
            @"D:\FeverGames\1.18.44.2\mpay.dll",
            @"D:\FeverGames\1.18.44.2\skin.zip",
            @"D:\FeverGames\1.2.0.0\mpay.dll",
            @"D:\FeverGames\1.2.0.0\skin.zip"
        };

        var success = FeverEnvironmentResolver.TryResolveFromFeverRoot(
            feverRoot,
            _ => dirs,
            existingFiles.Contains,
            out var mpayDllPath,
            out var skinZipPath,
            out var failureReason);

        Assert.True(success, failureReason);
        Assert.Equal(@"D:\FeverGames\1.18.44.2\mpay.dll", mpayDllPath);
        Assert.Equal(@"D:\FeverGames\1.18.44.2\skin.zip", skinZipPath);
    }

    [Fact]
    public void TryResolveFromFeverRoot_FallsBackToRoot_WhenSubdirIncomplete()
    {
        const string feverRoot = @"D:\FeverGames";
        string[] dirs =
        [
            @"D:\FeverGames\1.18.44.2"
        ];

        // Subdirectory only has mpay.dll, missing skin.zip
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\FeverGames\1.18.44.2\mpay.dll",
            @"D:\FeverGames\mpay.dll",
            @"D:\FeverGames\skin.zip"
        };

        var success = FeverEnvironmentResolver.TryResolveFromFeverRoot(
            feverRoot,
            _ => dirs,
            existingFiles.Contains,
            out var mpayDllPath,
            out var skinZipPath,
            out var failureReason);

        Assert.True(success, failureReason);
        Assert.Equal(@"D:\FeverGames\mpay.dll", mpayDllPath);
        Assert.Equal(@"D:\FeverGames\skin.zip", skinZipPath);
    }

    [Fact]
    public void TryResolveFromFeverRoot_FailsGracefully_WhenComponentsNotFound()
    {
        const string feverRoot = @"D:\FeverGames";
        string[] dirs =
        [
            @"D:\FeverGames\1.18.44.2"
        ];

        var success = FeverEnvironmentResolver.TryResolveFromFeverRoot(
            feverRoot,
            _ => dirs,
            _ => false,
            out var mpayDllPath,
            out var skinZipPath,
            out var failureReason);

        Assert.False(success);
        Assert.Empty(mpayDllPath);
        Assert.Empty(skinZipPath);
        Assert.NotNull(failureReason);
        Assert.Contains("未找到匹配", failureReason);
    }
}
