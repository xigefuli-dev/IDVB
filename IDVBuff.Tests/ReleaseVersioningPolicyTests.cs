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
            "Import-Module '" + modulePath + "' -Force; "
            + "$public = ConvertTo-IDVBPublicVersion -ReleaseLine 'b01.1' "
            + "-UtcNow ([DateTimeOffset]'2026-08-04T03:29:00+00:00'); "
            + "$numeric = ConvertTo-IDVBNumericVersion -PublicVersion $public; "
            + "Write-Output \"$public|$numeric\"");

        Assert.Equal("b01.1-26.8.4.1129|26.8.4.1129", output.Trim());
    }

    [Fact]
    public void InvalidCalendarTimestampIsRejected()
    {
        var modulePath = Path.Combine(RepositoryRoot, "installer", "ReleaseVersion.psm1")
            .Replace("'", "''");
        var result = RunPowerShellAllowFailure(
            "Import-Module '" + modulePath + "' -Force; "
            + "ConvertTo-IDVBNumericVersion -PublicVersion 'b01.1-26.2.30.1129'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid IDVB release timestamp", result.StandardError);
    }

    [Fact]
    public void InstallerBuildAboutPageAndWorkflowUseTheSamePublicVersionContract()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot, "IDVBuff.csproj"));
        var installer = File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "IDVB.iss"));
        var build = File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "Build-Release.ps1"));
        var about = File.ReadAllText(Path.Combine(RepositoryRoot, "Views", "SettingsPage.cs"));
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot, ".github", "workflows", "release.yml"));

        Assert.Contains("b01.1-26.8.4.1129", project);
        Assert.Contains("26.8.4.1129", installer);
        Assert.Contains("ConvertTo-IDVBNumericVersion", build);
        Assert.Contains("AssemblyInformationalVersionAttribute", about);
        Assert.Contains("contents: write", workflow);
        Assert.Contains("IDVB_SIGNING_CERTIFICATE_PFX_BASE64", workflow);
        Assert.Contains("-RequireSignedRelease", workflow);
        Assert.Contains("actions/upload-artifact@v4", workflow);
        Assert.Contains("'release', 'create'", workflow);
        Assert.DoesNotContain("--verify-tag", workflow);
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
