using System.Diagnostics;
using System.Xml.Linq;

namespace IDVBuff.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WinUIRegressionCollection
{
    public const string Name = "Native WinUI issue regressions";
}

[Collection(WinUIRegressionCollection.Name)]
public sealed class WinUIIssueRegressionTests
{
    [Fact]
    [Trait("Issue", "8")]
    [Trait("Issue", "9")]
    [Trait("Category", "NativeWinUI")]
    [Trait("Category", "IssueRegression")]
    public async Task ProductionThemeAndStartupPassTheNativeWindowRegressionSuite()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "winui-regression-host-path.txt");
        Assert.True(File.Exists(manifest), "Build the test project to generate the native regression host path.");
        var executable = Path.ChangeExtension((await File.ReadAllTextAsync(manifest)).Trim(), ".exe");
        Assert.True(File.Exists(executable), $"Native regression executable is missing: {executable}");
        var root = FindRepositoryRoot(executable);
        var results = Path.Combine(root, ".verify", "issue-regressions", "test-results", $"winui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(results);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--results-directory");
        start.ArgumentList.Add(results);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Only request a normal close of this test's own host; never kill a
            // process or discard evidence to conceal a native lifecycle failure.
            process.CloseMainWindow();
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(closeTimeout.Token); }
            catch (OperationCanceledException) { }
            Assert.Fail($"Native regression host exceeded its timeout. Evidence: {results}");
        }

        var textPath = Path.Combine(results, "results.txt");
        var xmlPath = Path.Combine(results, "results.xml");
        var diagnostic = File.Exists(textPath) ? await File.ReadAllTextAsync(textPath) : "No native result was produced.";
        diagnostic += $"\nstdout: {await output}\nstderr: {await error}\nEvidence: {results}";
        Assert.True(process.ExitCode == 0, diagnostic);
        Assert.True(File.Exists(xmlPath), diagnostic);
        var suite = XDocument.Load(xmlPath).Root;
        Assert.NotNull(suite);
        Assert.True((bool?)suite.Attribute("completed") == true, diagnostic);
        var cases = suite.Descendants("testcase").ToArray();
        Assert.NotEmpty(cases);
        Assert.True(cases.Count(test => ((string?)test.Attribute("name"))?.StartsWith("Theme/", StringComparison.Ordinal) == true) >= 12,
            "The native theme regression cases did not all run. " + diagnostic);
        Assert.True(cases.Count(test => ((string?)test.Attribute("name"))?.StartsWith("Startup/", StringComparison.Ordinal) == true) >= 12,
            "The native startup regression cases did not all run. " + diagnostic);
        Assert.Equal(cases.Length, (int?)suite.Attribute("tests"));
        Assert.Equal(0, (int?)suite.Attribute("failures"));
        Assert.All(cases, test =>
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)test.Attribute("name")), diagnostic);
            Assert.Empty(test.Elements("failure"));
            Assert.Empty(test.Elements("error"));
            Assert.Empty(test.Elements("skipped"));
        });
        Assert.Equal(cases.Length, cases.Select(test => (string?)test.Attribute("name")).Distinct().Count());
    }

    private static string FindRepositoryRoot(string executable)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "IDVBuff.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The native regression host is outside the repository.");
    }
}
