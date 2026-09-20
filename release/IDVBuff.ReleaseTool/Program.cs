using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.UpdateCore;

namespace IDVBuff.ReleaseTool;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                throw new ArgumentException("Expected generate-key, sign, or verify.");
            var options = ParseOptions(args.Skip(1));
            return args[0] switch
            {
                "generate-key" => GenerateKey(options),
                "sign" => Sign(options),
                "verify" => Verify(options),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int GenerateKey(IReadOnlyDictionary<string, string> options)
    {
        var privatePath = Required(options, "private");
        var publicPath = Required(options, "public");
        if (File.Exists(privatePath) || File.Exists(publicPath))
            throw new IOException("Refusing to overwrite an existing signing key.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privatePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicPath))!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine(publicPath);
        return 0;
    }

    private static int Sign(IReadOnlyDictionary<string, string> options)
    {
        var payloadPath = Required(options, "payload");
        var privatePath = Required(options, "private");
        var outputPath = Required(options, "output");
        var keyId = Required(options, "key-id");
        var payload = File.ReadAllBytes(payloadPath);
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(privatePath));
        var signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var envelope = new UpdateFeedEnvelope(
            UpdateProtocol.EnvelopeSchemaVersion,
            keyId,
            Convert.ToBase64String(payload),
            Convert.ToBase64String(signature));
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(envelope, UpdateProtocol.JsonOptions));
        return 0;
    }

    private static int Verify(IReadOnlyDictionary<string, string> options)
    {
        var envelopePath = Required(options, "envelope");
        var publicPath = Required(options, "public");
        var keyId = Required(options, "key-id");
        var channel = Required(options, "channel");
        var payloadOutput = options.GetValueOrDefault("payload-output");
        var verifier = new EcdsaUpdateFeedVerifier(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [keyId] = File.ReadAllText(publicPath)
            });
        var verified = verifier.Verify(File.ReadAllText(envelopePath), channel);
        if (!string.IsNullOrWhiteSpace(payloadOutput))
            File.WriteAllBytes(payloadOutput, verified.CanonicalPayload);
        Console.WriteLine(verified.Payload.PublicVersion);
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var enumerator = args.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var name = enumerator.Current;
            if (name is null || !name.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid option '{name}'.");
            if (!enumerator.MoveNext() || enumerator.Current is null)
                throw new ArgumentException($"Missing value for '{name}'.");
            result.Add(name[2..], enumerator.Current);
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing --{name}.");
}
