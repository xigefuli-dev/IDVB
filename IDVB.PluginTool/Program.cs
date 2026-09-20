using System.Text.Json;
using IdentityVisionBridge.PluginPackaging;

return await PluginTool.RunAsync(args);

internal static class PluginTool
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "keygen" => await KeygenAsync(args[1..]),
                "validate" => await ValidateAsync(args[1..]),
                "inspect" => await InspectAsync(args[1..]),
                "pack" => await PackAsync(args[1..]),
                "sign" => await SignAsync(args[1..]),
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> KeygenAsync(string[] args)
    {
        RequireCount(args, 1, "keygen <output-directory>");
        var directory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(directory);
        var key = IdvpCrypto.CreatePublisherKey();
        await File.WriteAllTextAsync(Path.Combine(directory, "publisher-private.pem"), key.PrivateKeyPem);
        await File.WriteAllTextAsync(Path.Combine(directory, "publisher-public.pem"), key.PublicKeyPem);
        await File.WriteAllTextAsync(Path.Combine(directory, "key-id.txt"), key.KeyId + Environment.NewLine);
        Console.WriteLine($"Created ECDSA P-256 publisher key: {key.KeyId}");
        Console.WriteLine("Keep publisher-private.pem outside source control and package output.");
        return 0;
    }

    private static async Task<int> ValidateAsync(string[] args)
    {
        RequireCount(args, 1, "validate <package.idvp> [--allow-unsigned]");
        var allowUnsigned = args.Contains("--allow-unsigned", StringComparer.Ordinal);
        var package = await new IdvpPackageReader().ValidateAsync(
            args[0],
            options: new IdvpValidationOptions { AllowUnsigned = allowUnsigned, ExtractFiles = false });
        Console.WriteLine($"valid: {package.Manifest.Id} {package.Manifest.Version}");
        Console.WriteLine($"publisher-key: {package.Signature.KeyId ?? "unsigned"}");
        return 0;
    }

    private static async Task<int> InspectAsync(string[] args)
    {
        RequireCount(args, 1, "inspect <package.idvp> [--allow-unsigned]");
        var allowUnsigned = args.Contains("--allow-unsigned", StringComparer.Ordinal);
        var package = await new IdvpPackageReader().ValidateAsync(
            args[0],
            options: new IdvpValidationOptions { AllowUnsigned = allowUnsigned, ExtractFiles = false });
        Console.WriteLine(JsonSerializer.Serialize(package.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
        Console.WriteLine($"signature: {package.Signature.Algorithm} / {package.Signature.KeyId ?? "none"}");
        return 0;
    }

    private static async Task<int> PackAsync(string[] args)
    {
        RequireCount(args, 3, "pack <source-directory> <manifest.json> <output.idvp> [--key private.pem]");
        var key = GetOption(args, "--key");
        var package = await new IdvpPackageWriter().PackAsync(new IdvpPackOptions
        {
            SourceDirectory = args[0],
            ManifestPath = args[1],
            OutputPath = args[2],
            PrivateKeyPemPath = key
        });
        Console.WriteLine($"packed: {package.PackagePath}");
        Console.WriteLine($"publisher-key: {package.Signature.KeyId ?? "unsigned developer package"}");
        return 0;
    }

    private static async Task<int> SignAsync(string[] args)
    {
        RequireCount(args, 3, "sign <unsigned.idvp> <signed.idvp> <private.pem>");
        var package = await new IdvpPackageWriter().SignAsync(args[0], args[1], args[2]);
        Console.WriteLine($"signed: {package.PackagePath}");
        Console.WriteLine($"publisher-key: {package.Signature.KeyId}");
        return 0;
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}.");
        return args[index + 1];
    }

    private static void RequireCount(string[] args, int count, string usage)
    {
        if (args.Length < count) throw new ArgumentException($"Usage: idvb-plugin {usage}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Identity Vision Bridge plugin developer tool");
        Console.WriteLine("  idvb-plugin keygen <output-directory>");
        Console.WriteLine("  idvb-plugin validate <package.idvp> [--allow-unsigned]");
        Console.WriteLine("  idvb-plugin inspect <package.idvp> [--allow-unsigned]");
        Console.WriteLine("  idvb-plugin pack <source-directory> <manifest.json> <output.idvp> [--key private.pem]");
        Console.WriteLine("  idvb-plugin sign <unsigned.idvp> <signed.idvp> <private.pem>");
    }
}
