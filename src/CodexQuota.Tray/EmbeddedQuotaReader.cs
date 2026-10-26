using System.Text.Json;
using CodexQuota.Core;

namespace CodexQuota.Tray;

/// <summary>Only the build-embedded, hashed reader can supply a quota process.</summary>
internal static class EmbeddedQuotaReader
{
    internal static HelperExecutableLease Open()
    {
        var assembly = typeof(EmbeddedQuotaReader).Assembly;
        using var stream = assembly.GetManifestResourceStream("QuotaReader.Manifest.json")
            ?? throw new FileNotFoundException("Embedded reader manifest is missing.");
        if (stream.Length is <= 0 or > 4096) throw Invalid();
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (field.Name is not ("helperVersion" or "sha256" or "executableLength" or "compressedLength") || !fields.Add(field.Name)) throw Invalid();
        if (fields.Count != 4 || root.GetProperty("helperVersion").ValueKind != JsonValueKind.String ||
            root.GetProperty("helperVersion").GetString() != ReadOnlyQuotaProtocol.HelperVersion ||
            root.GetProperty("sha256").ValueKind != JsonValueKind.String ||
            root.GetProperty("executableLength").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("executableLength").TryGetInt64(out var executableLength) ||
            root.GetProperty("compressedLength").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("compressedLength").TryGetInt64(out var compressedLength)) throw Invalid();
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) throw Invalid();
        return HelperExecutableLease.Extract(localData,
            () => assembly.GetManifestResourceStream("QuotaReader.Payload.gz")
                ?? throw new FileNotFoundException("Embedded reader payload is missing."),
            root.GetProperty("sha256").GetString() ?? throw Invalid(), executableLength, compressedLength);
    }

    private static InvalidDataException Invalid() => new("Embedded reader manifest is invalid.");
}
