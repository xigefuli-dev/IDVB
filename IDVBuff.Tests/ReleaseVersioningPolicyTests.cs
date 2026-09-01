using System.Diagnostics;

namespace IDVBuff.Tests;

public sealed class ReleaseVersioningPolicyTests
{
    [Fact]
    public void ChinaTimeVersionUsesThePublicAndWindowsFormats()
    {
        var modulePath = Path.Combine(RepositoryRoot, "installer", "ReleaseVersion.psm1")
            .Replace("'", "''");
        var output = RunPowerShell(
            "Import-Module '" + modulePath + "' -Force -DisableNameChecking; "
            + "$public = ConvertTo-IDVBPublicVersion -ReleaseLine 'b01.2' "
            + "-UtcNow ([DateTimeOffset]'2026-08-04T03:29:00+00:00'); "
            + "$numeric = ConvertTo-IDVBNumericVersion -PublicVersion $public; "
            + "Write-Output \"$public|$numeric\"");

        Assert.Equal("b01.2-26.08.04.0000|1.2.0.0", output.Trim());
    }

    [Fact]
    public void BuildCounterIncrementsForEachReservedBuild()
    {
        var modulePath = Path.Combine(RepositoryRoot, "installer", "ReleaseVersion.psm1")
            .Replace("'", "''");
        var counterPath = Path.Combine(
                Path.GetTempPath(),
                "idvb-counter-" + Guid.NewGuid().ToString("N") + ".txt")
            .Replace("'", "''");
        try
        {
            var output = RunPowerShell(
                "Import-Module '" + modulePath + "' -Force -DisableNameChecking; "
                + "$first = New-IDVBBuildVersion -ReleaseLine 'b01.2' "
                + "-CounterPath '" + counterPath + "' "
                + "-UtcNow ([DateTimeOffset]'2026-08-09T00:00:00+00:00'); "
                + "$second = New-IDVBBuildVersion -ReleaseLine 'b01.2' "
                + "-CounterPath '" + counterPath + "' "
                + "-UtcNow ([DateTimeOffset]'2026-08-09T00:00:00+00:00'); "
                + "Write-Output \"$first|$second\"");

            Assert.Equal(
                "b01.2-26.08.09.0001|b01.2-26.08.09.0002",
                output.Trim());
        }
        finally
        {
            if (File.Exists(counterPath)) File.Delete(counterPath);
            if (File.Exists(counterPath + ".lock")) File.Delete(counterPath + ".lock");
        }
    }

    [Fact]
    public void NumericVersionHonorsProductPatch()
    {
        var modulePath = Path.Combine(RepositoryRoot, "installer", "ReleaseVersion.psm1")
            .Replace("'", "''");
        var output = RunPowerShell(
            "Import-Module '" + modulePath + "' -Force -DisableNameChecking; "
            + "$numeric = ConvertTo-IDVBNumericVersion "
            + "-PublicVersion 'b01.4-26.08.12.0001' -Patch 1; "
            + "Write-Output $numeric");

        Assert.Equal("1.4.1.1", output.Trim());
    }

    [Fact]
    public void InvalidCalendarTimestampIsRejected()
    {
        var modulePath = Path.Combine(RepositoryRoot, "installer", "ReleaseVersion.psm1")
            .Replace("'", "''");
        var result = RunPowerShellAllowFailure(
            "Import-Module '" + modulePath + "' -Force -DisableNameChecking; "
            + "ConvertTo-IDVBNumericVersion -PublicVersion 'b01.2-26.02.30.0000'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid IDVB release timestamp", result.StandardError);
    }

    [Fact]
    public void InstallerBuildAndAboutPageUseTheSamePublicVersionContract()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot, "IDVBuff.csproj"));
        var installer = File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "IDVB.iss"));
        var build = File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "Build-Release.ps1"));
        var buildTargets = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.targets"));
        var about = File.ReadAllText(Path.Combine(RepositoryRoot, "Views", "SettingsPage.cs"));

        Assert.Contains("<IDVBProductVersion>1.6.0-unstable</IDVBProductVersion>", project);
        Assert.Contains("<IDVBReleaseLine>b01.6</IDVBReleaseLine>", project);
        Assert.Contains("b01.6", installer);
        Assert.Contains("1.6.0.0", installer);
        Assert.Contains("ConvertTo-IDVBNumericVersion", build);
        Assert.Contains("BuildVersionInfo.ProductVersion", about);
        Assert.Contains("BuildVersionInfo.BuildVersion", about);
        Assert.Contains("Generate-IDVBBuildVersion.ps1", buildTargets);
        Assert.Contains("PublishGitHubRelease", build);
        Assert.Contains("Get-GitHubCliPath", build);
        Assert.Contains("Invoke-GitHubRelease", build);
        Assert.Contains("'release', 'create'", build);
        Assert.DoesNotContain("Publishing to GitHub Release requires -RequireSignedRelease", build);
        Assert.DoesNotContain("--verify-tag", build);
    }

    [Fact]
    public void PreviewProductVersionUsesNumericAssemblyVersion()
    {
        var modulePath = Path.Combine(RepositoryRoot, "installer", "ReleaseVersion.psm1")
            .Replace("'", "''");
        var output = RunPowerShell(
            "Import-Module '" + modulePath + "' -Force -DisableNameChecking; "
            + "$parts = ConvertFrom-IDVBProductVersion -ProductVersion '1.5.0-preview'; "
            + "Write-Output \"$($parts.BaseVersion)|$($parts.Major).$($parts.Minor).$($parts.Patch).0|$($parts.Prerelease)\"");

        Assert.Equal("1.5.0|1.5.0.0|preview", output.Trim());
    }

    private static string RunPowerShell(string command)
    {
        var result = RunPowerShellAllowFailure(command);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput;
    }

    private static (int ExitCode, string StandardOutput, string StandardError)
        RunPowerShellAllowFailure(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cannot start Windows PowerShell.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static string RepositoryRoot
    {
        get
        {
            foreach (var candidate in new[]
                     {
                         new DirectoryInfo(Directory.GetCurrentDirectory()),
                         new DirectoryInfo(AppContext.BaseDirectory)
                     })
            {
                for (var current = candidate; current is not null; current = current.Parent)
                {
                    if (File.Exists(Path.Combine(current.FullName, "IDVBuff.slnx")))
                        return current.FullName;
                }
            }

            throw new DirectoryNotFoundException("Cannot find the repository root.");
        }
    }
}
