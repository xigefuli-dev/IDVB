using IDVBuff.Diagnostics;

namespace IDVBuff.Tests;

public sealed class SavedDiagnosticDataRetentionTests
{
    [Fact]
    public void CleanupExpiresOnlyOldSavedDiagnosticsAndSkipsActiveData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVB-retention-{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        var old = now.AddDays(-8);
        var recent = now.AddDays(-6);
        var logs = Path.Combine(root, "Logs");
        var research = Path.Combine(root, "AlignmentResearch", "sessions");
        var diagnostics = Path.Combine(root, "诊断模式");

        try
        {
            Directory.CreateDirectory(logs);
            Directory.CreateDirectory(research);
            Directory.CreateDirectory(diagnostics);

            var oldLog = CreateFile(logs, "output-log-old.log", old);
            var recentLog = CreateFile(logs, "startup-recent.log", recent);
            var activeLog = CreateFile(logs, "scan-log-active.json", old);
            var unrelatedLog = CreateFile(logs, "custom-user-file.log", old);
            var legacyLog = CreateFile(logs, "startup.log", old);
            var oldResearch = CreateDirectory(research, "old-session", old);
            var activeResearch = CreateDirectory(research, "active-session", old);
            var recentResearch = CreateDirectory(research, "recent-session", recent);
            var oldMatch = CreateDirectory(diagnostics, "对局 1", old);
            var activeMatch = CreateDirectory(diagnostics, "对局 2", old);
            var recentMatch = CreateDirectory(diagnostics, "对局 3", recent);
            var unrelatedDirectory = CreateDirectory(diagnostics, "用户资料", old);

            SavedDiagnosticDataRetention.Cleanup(root, now, activeLog, activeMatch, activeResearch);

            Assert.False(File.Exists(oldLog));
            Assert.False(File.Exists(legacyLog));
            Assert.True(File.Exists(recentLog));
            Assert.True(File.Exists(activeLog));
            Assert.True(File.Exists(unrelatedLog));
            Assert.False(Directory.Exists(oldResearch));
            Assert.True(Directory.Exists(activeResearch));
            Assert.True(Directory.Exists(recentResearch));
            Assert.False(Directory.Exists(oldMatch));
            Assert.True(Directory.Exists(activeMatch));
            Assert.True(Directory.Exists(recentMatch));
            Assert.True(Directory.Exists(unrelatedDirectory));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFile(string root, string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, "diagnostic data");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static string CreateDirectory(string root, string name, DateTime createdUtc)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data.txt"), "diagnostic data");
        Directory.SetCreationTimeUtc(path, createdUtc);
        return path;
    }
}
