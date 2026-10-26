using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>An authenticated update offer. Never execute an offer without IsNewerThan and VerifyFile.</summary>
public sealed record UpdateManifest(Version Version, Uri AssetUri, long Size, string Sha256, DateTimeOffset PublishedAt)
{
    public const string Repository = "breakingtime126/CodexQuotaTray";
    public const string ManifestUrl = "https://github.com/" + Repository + "/releases/latest/download/update.json";
    public const int MaximumEnvelopeBytes = 16384;
    public const long MaximumAssetBytes = 30L * 1024 * 1024;
    private static readonly DateTimeOffset EarliestRelease = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public bool IsNewerThan(Version currentVersion) => Version > currentVersion;

    public static string AssetUrlFor(Version version) => "https://github.com/" + Repository +
        "/releases/download/v" + version.ToString(4) + "/CodexQuotaTray.exe";

    public static UpdateManifest ParseAndVerify(byte[] envelope, RSAParameters publicKey, DateTimeOffset now)
    {
        if (envelope is null || envelope.Length == 0 || envelope.Length > MaximumEnvelopeBytes)
            throw Invalid("Update manifest size is invalid.");
        if (publicKey.Modulus is null || publicKey.Modulus.Length < 256 || publicKey.Modulus.Length > 512 ||
            publicKey.Exponent is null || publicKey.Exponent.Length != 3 ||
            publicKey.Exponent[0] != 1 || publicKey.Exponent[1] != 0 || publicKey.Exponent[2] != 1)
            throw new CryptographicException("A trusted RSA update key is required.");
        try
        {
            using var outer = JsonDocument.Parse(StrictUtf8.GetString(envelope), Options);
            ExactFields(outer.RootElement, "payload", "signature");
            var payload = CanonicalBase64(String(outer.RootElement, "payload", 8192));
            var signature = CanonicalBase64(String(outer.RootElement, "signature", 1024));
            if (payload.Length == 0 || payload.Length > 4096 || signature.Length != publicKey.Modulus.Length)
                throw Invalid("Invalid signed update payload size.");
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = publicKey.Modulus, Exponent = publicKey.Exponent });
            if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("Update signature verification failed.");
            using var parsed = JsonDocument.Parse(StrictUtf8.GetString(payload), Options);
            var root = parsed.RootElement;
            ExactFields(root, "schemaVersion", "app", "channel", "version", "assetUrl", "size", "sha256", "publishedAt");
            if (!root.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != 1 ||
                String(root, "app", 32) != "CodexQuotaTray" || String(root, "channel", 16) != "stable")
                throw Invalid("Unsupported update identity or schema.");
            var versionText = String(root, "version", 48);
            if (!Version.TryParse(versionText, out var version) || version.Revision < 0 ||
                version.ToString(4) != versionText || version <= new Version(0, 0, 0, 0))
                throw Invalid("Invalid update version.");
            var assetUrl = String(root, "assetUrl", 512);
            if (assetUrl != AssetUrlFor(version)) throw Invalid("Update asset is not in the trusted release.");
            if (!root.GetProperty("size").TryGetInt64(out var size) || size < 1 || size > MaximumAssetBytes)
                throw Invalid("Invalid update asset size.");
            var sha256 = String(root, "sha256", 64);
            if (sha256.Length != 64 || sha256.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                throw Invalid("Invalid update SHA-256.");
            var publishedText = String(root, "publishedAt", 40);
            if (!DateTimeOffset.TryParseExact(publishedText, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var published) ||
                published < EarliestRelease || published > now.AddMinutes(10))
                throw Invalid("Invalid update publication time.");
            return new(version, new Uri(assetUrl, UriKind.Absolute), size, sha256, published);
        }
        catch (Exception error) when (error is JsonException or FormatException or DecoderFallbackException or InvalidOperationException)
        {
            throw new InvalidDataException("Malformed update manifest.", error);
        }
    }

    /// <summary>Locks against writers/deletion while hashing. Caller must preserve file ownership until replacement.</summary>
    public void VerifyFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != Size) throw Invalid("Update asset size mismatch.");
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(file);
        if (file.Length != Size) throw Invalid("Update asset changed during validation.");
        var expected = new byte[32];
        for (var i = 0; i < expected.Length; i++) expected[i] = byte.Parse(Sha256.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var difference = 0;
        for (var i = 0; i < hash.Length; i++) difference |= hash[i] ^ expected[i];
        if (difference != 0) throw Invalid("Update asset hash mismatch.");
    }

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 3, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };
    private static InvalidDataException Invalid(string message) => new(message);
    private static string String(JsonElement root, string name, int max)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length == 0 || text.Length > max)
            throw Invalid("Invalid update field: " + name);
        return text;
    }
    private static byte[] CanonicalBase64(string text)
    {
        var bytes = Convert.FromBase64String(text);
        if (Convert.ToBase64String(bytes) != text) throw Invalid("Noncanonical base64.");
        return bytes;
    }
    private static void ExactFields(JsonElement root, params string[] expected)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("Expected update object.");
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!remaining.Remove(field.Name)) throw Invalid("Duplicate or unknown update field.");
        if (remaining.Count != 0) throw Invalid("Missing update field.");
    }
}
