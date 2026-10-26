using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>Only bounded event hashes are persisted; no API data, source URLs or account information.</summary>
public sealed class ResetNotificationState
{
    public const int MaximumAcknowledgedEvents = 32;
    private readonly string _path;
    private readonly List<string> _acknowledged = new();

    public ResetNotificationState(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexQuotaTray", "reset-notifications.json");
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4096) return;
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var entry in document.RootElement.EnumerateArray().Take(MaximumAcknowledgedEvents))
            {
                var value = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
                if (value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                    !_acknowledged.Contains(value)) _acknowledged.Add(value);
            }
        }
        catch (Exception error) when (IsFileError(error) || error is JsonException) { }
    }

    public bool IsUnread(string? eventIdentity) => !string.IsNullOrEmpty(eventIdentity) &&
        !_acknowledged.Contains(Hash(eventIdentity!));

    public void Acknowledge(string? eventIdentity)
    {
        if (string.IsNullOrEmpty(eventIdentity)) return;
        var hash = Hash(eventIdentity!);
        if (_acknowledged.Contains(hash)) return;
        _acknowledged.Add(hash);
        if (_acknowledged.Count > MaximumAcknowledgedEvents) _acknowledged.RemoveAt(0);
        // A failed save does not re-notify in this process or interrupt the original quota UI.
        string? temporary = null;
        try
        {
            var path = Path.GetFullPath(_path);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".reset-notifications-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_acknowledged));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            temporary = null;
        }
        catch (Exception error) when (IsFileError(error)) { }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (IsFileError(error)) { }
        }
    }

    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
    }
    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException;
}
