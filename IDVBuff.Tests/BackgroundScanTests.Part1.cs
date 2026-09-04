namespace IDVBuff.Tests;

public sealed partial class BackgroundScanTests
{
    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "IDVBuff.csproj")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
