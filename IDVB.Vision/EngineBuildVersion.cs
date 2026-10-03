namespace IDVBuff;

internal static class BuildVersionInfo
{
    public static string BuildVersion => typeof(IdentityVisionBridge.Vision.IdvbVisionEngine).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
}
