using IDVBuff.Features.GameLaunch;

namespace IDVBuff.Tests;

public sealed class FeverGamesLaunchPlanTests
{
    [Fact]
    public void AutoStartGameUri_RequestsFeverToAutoRunIdentityV()
    {
        Assert.Equal(
            "fevergames://mygame/?gameId=73&autoRun=1",
            FeverGamesLaunchPlan.AutoStartGameUri);
        Assert.DoesNotContain("state=startgame", FeverGamesLaunchPlan.AutoStartGameUri);
    }

    [Fact]
    public void TryCreate_UsesTheRegisteredFeverGamesProtocolLauncher()
    {
        const string launcher = @"D:\FeverGames\FeverGamesLauncher.exe";

        var created = FeverGamesLaunchPlan.TryCreate(
            $"\"{launcher}\" \"%1\"",
            path => string.Equals(path, launcher, StringComparison.OrdinalIgnoreCase),
            out var plan,
            out var failure);

        Assert.True(created, failure);
        Assert.Equal(launcher, plan.LauncherPath);
    }

    [Fact]
    public void TryCreate_RejectsAMissingProtocolLauncher()
    {
        var created = FeverGamesLaunchPlan.TryCreate(
            "\"D:\\FeverGames\\FeverGamesLauncher.exe\" \"%1\"",
            _ => false,
            out _,
            out var failure);

        Assert.False(created);
        Assert.Contains("启动器不存在", failure);
    }

    [Fact]
    public void GameStartPlan_UsesFeverRegisteredGameCommand()
    {
        var created = FeverGamesGameStartPlan.TryCreate(
            @"D:\FeverApps\dwrg2",
            "dwrg.exe",
            "--start_from_launcher=1",
            path => string.Equals(path, @"D:\FeverApps\dwrg2\dwrg.exe", StringComparison.OrdinalIgnoreCase),
            out var plan,
            out var failure);

        Assert.True(created, failure);
        Assert.Equal(@"D:\FeverApps\dwrg2\dwrg.exe", plan.ExecutablePath);
        Assert.Equal("--start_from_launcher=1", plan.Arguments);
    }
}
