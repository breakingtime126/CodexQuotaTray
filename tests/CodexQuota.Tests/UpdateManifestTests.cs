using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuota.Core;

static class UpdateManifestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 6, 0, 0, TimeSpan.Zero);
    // Ephemeral test key, never persisted or used by a distributed application.
    private static readonly RSA Key = RSA.Create(2048);
    private static RSAParameters PublicKey => Key.ExportParameters(false);
    private static readonly byte[] Asset = Encoding.UTF8.GetBytes("test asset, not executable");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Asset)).ToLowerInvariant();
    private static string Payload => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, app = "CodexQuotaTray", channel = "stable", version = "0.2.34.0",
        assetUrl = UpdateManifest.AssetUrlFor(new Version(0, 2, 34, 0)), size = Asset.Length,
        sha256 = Hash, publishedAt = "2026-10-26T05:00:00Z"
    });
    private static byte[] Signed(string payload) => Envelope(Encoding.UTF8.GetBytes(payload));
    private static byte[] Envelope(byte[] payload) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        payload = Convert.ToBase64String(payload),
        signature = Convert.ToBase64String(Key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
    });
    private static UpdateManifest Parse(string payload) => UpdateManifest.ParseAndVerify(Signed(payload), PublicKey, Now);

    public static void Register(Action<string, Action> test)
    {
        test("update manifest: authentic fixed release and version ordering", () =>
        {
            var parsed = Parse(Payload);
            Check.Equal(new Version(0, 2, 34, 0), parsed.Version);
            Check.True(parsed.IsNewerThan(new Version(0, 2, 33, 0)));
            Check.False(parsed.IsNewerThan(new Version(0, 2, 34, 0)));
            Check.False(parsed.IsNewerThan(new Version(0, 2, 35, 0)));
            Check.Equal(Hash, parsed.Sha256);
            Check.Equal((long)Asset.Length, parsed.Size);
        });
        test("update manifest: missing, small, or different trust keys fail closed", () =>
        {
            Check.Throws<CryptographicException>(() => UpdateManifest.ParseAndVerify(Signed(Payload), default, Now));
            using var other = RSA.Create(2048);
            Check.Throws<CryptographicException>(() => UpdateManifest.ParseAndVerify(Signed(Payload), other.ExportParameters(false), Now));
            var weak = PublicKey; weak.Modulus = new byte[128];
            Check.Throws<CryptographicException>(() => UpdateManifest.ParseAndVerify(Signed(Payload), weak, Now));
        });
        test("update manifest: modified payload and signature are rejected", () =>
        {
            var original = Signed(Payload);
            using var doc = JsonDocument.Parse(original);
            var signature = Convert.FromBase64String(doc.RootElement.GetProperty("signature").GetString()!);
            signature[0] ^= 1;
            var badSignature = JsonSerializer.SerializeToUtf8Bytes(new { payload = doc.RootElement.GetProperty("payload").GetString(), signature = Convert.ToBase64String(signature) });
            Check.Throws<CryptographicException>(() => UpdateManifest.ParseAndVerify(badSignature, PublicKey, Now));
            var badPayload = JsonSerializer.SerializeToUtf8Bytes(new { payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Payload.Replace("0.2.34.0", "0.2.35.0"))), signature = doc.RootElement.GetProperty("signature").GetString() });
            Check.Throws<CryptographicException>(() => UpdateManifest.ParseAndVerify(badPayload, PublicKey, Now));
        });
        test("update manifest: strict identity, types, duplicate and unknown fields", () =>
        {
            foreach (var bad in new[] {
                Payload.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
                Payload.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""),
                Payload.Replace("\"app\":\"CodexQuotaTray\"", "\"app\":\"Other\""),
                Payload.Replace("\"stable\"", "\"beta\""),
                Payload.Insert(1, "\"schemaVersion\":1,"), Payload.Insert(1, "\"extra\":true,"),
                Payload.Replace("\"channel\":\"stable\",", "") })
                Check.Throws<InvalidDataException>(() => Parse(bad));
        });
        test("update manifest: reject noncanonical and malformed versions", () =>
        {
            foreach (var version in new[] { "0.2.34", "0.02.34.0", "0.2.34.0-beta", "-1.2.34.0", "0.0.0.0", "1.2.3.4.5" })
                Check.Throws<InvalidDataException>(() => Parse(Payload.Replace("0.2.34.0", version)));
        });
        test("update manifest: reject any alternate URL or traversal", () =>
        {
            foreach (var url in new[] {
                "http://github.com/breakingtime126/CodexQuotaTray/releases/download/v0.2.34.0/CodexQuotaTray.exe",
                "https://github.com/other/CodexQuotaTray/releases/download/v0.2.34.0/CodexQuotaTray.exe",
                "https://github.com/breakingtime126/CodexQuotaTray/releases/download/v0.2.34.0/../CodexQuotaTray.exe",
                UpdateManifest.AssetUrlFor(new Version(0, 2, 34, 0)) + "?x=1",
                UpdateManifest.AssetUrlFor(new Version(0, 2, 35, 0)) })
                Check.Throws<InvalidDataException>(() => Parse(Payload.Replace(UpdateManifest.AssetUrlFor(new Version(0, 2, 34, 0)), url)));
        });
        test("update manifest: bounded sizes hashes and release times", () =>
        {
            foreach (var bad in new[] {
                Payload.Replace("\"size\":" + Asset.Length, "\"size\":0"),
                Payload.Replace("\"size\":" + Asset.Length, "\"size\":31457281"),
                Payload.Replace(Hash, "deadbeef"), Payload.Replace(Hash, Hash.ToUpperInvariant()),
                Payload.Replace("2026-10-26T05:00:00Z", "2026-10-26T07:00:00Z"),
                Payload.Replace("2026-10-26T05:00:00Z", "2025-12-31T23:59:59Z"),
                Payload.Replace("2026-10-26T05:00:00Z", "2026-10-26T05:00:00+00:00") })
                Check.Throws<InvalidDataException>(() => Parse(bad));
        });
        test("update manifest: bounded strict UTF8 and canonical envelope", () =>
        {
            foreach (var bytes in new[] { Array.Empty<byte>(), new byte[UpdateManifest.MaximumEnvelopeBytes + 1], Encoding.UTF8.GetBytes("null"), new byte[] { 0xff } })
                Check.Throws<InvalidDataException>(() => UpdateManifest.ParseAndVerify(bytes, PublicKey, Now));
            var envelope = Encoding.UTF8.GetString(Signed(Payload));
            foreach (var bad in new[] { envelope.Insert(1, "\"unknown\":1,"), envelope.Insert(1, "\"payload\":\"AA==\","), envelope.Replace("\"payload\":\"", "\"payload\":\" ") })
                Check.Throws<InvalidDataException>(() => UpdateManifest.ParseAndVerify(Encoding.UTF8.GetBytes(bad), PublicKey, Now));
            Check.Throws<InvalidDataException>(() => UpdateManifest.ParseAndVerify(Envelope(new byte[] { 0xff }), PublicKey, Now));
        });
        test("update manifest: exact downloaded asset size and digest", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "cqt-update-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(path, Asset);
                var parsed = Parse(Payload);
                parsed.VerifyFile(path);
                var changed = (byte[])Asset.Clone(); changed[0] ^= 1;
                File.WriteAllBytes(path, changed);
                Check.Throws<InvalidDataException>(() => parsed.VerifyFile(path));
                File.WriteAllBytes(path, Asset.Concat(new byte[] { 0 }).ToArray());
                Check.Throws<InvalidDataException>(() => parsed.VerifyFile(path));
            }
            finally { File.Delete(path); }
        });
    }
}
