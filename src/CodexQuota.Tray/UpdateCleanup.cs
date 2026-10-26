using System.Text.Json;

namespace CodexQuota.Tray;

internal static class UpdateCleanup
{
    // Fixed filenames only. Unknown contents and reparse points are retained, never recursively removed.
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    { "request.json", "ready", "healthy", "status.txt", "update.json", "CodexQuotaTray.exe", "CodexQuotaTray.Update.exe", "previous.exe", "failed.exe" };

    internal static void KeepLatestRollback(string target)
    {
        try
        {
            var root = Path.GetDirectoryName(target)!; UpdateTransaction.NoLinks(root);
            var candidates = new List<string>();
            foreach (var directory in Directory.EnumerateDirectories(root, UpdateTransaction.Prefix + "*").Take(100))
            {
                try
                {
                    UpdateTransaction.NoLinks(directory);
                    if (!Guid.TryParseExact(Path.GetFileName(directory).Substring(UpdateTransaction.Prefix.Length), "N", out _)) continue;
                    var request = JsonSerializer.Deserialize<UpdateRequest>(UpdateTransaction.ReadBounded(Path.Combine(directory, "request.json"), 8192));
                    if (request?.TargetName != Path.GetFileName(target) || !File.Exists(Path.Combine(directory, "status.txt"))) continue;
                    candidates.Add(directory);
                }
                catch { }
            }
            var latest = candidates.Where(d => File.Exists(Path.Combine(d, "previous.exe")))
                .OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault();
            foreach (var directory in candidates.Where(d => d != latest)) DeleteKnown(directory);
        }
        catch { }
    }

    internal static void DeleteKnown(string directory)
    {
        try
        {
            UpdateTransaction.NoLinks(directory);
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                UpdateTransaction.NoLinks(child);
                var name = Path.GetFileName(child);
                if (!name.StartsWith("fetch-", StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(6), "N", out _)) continue;
                foreach (var file in Directory.EnumerateFiles(child))
                    if (new[] { "download.bin", "download.partial", "Fetch-Update.ps1" }.Contains(Path.GetFileName(file)))
                    { UpdateTransaction.NoLinks(file); File.Delete(file); }
                if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
                if (Files.Contains(Path.GetFileName(file))) { UpdateTransaction.NoLinks(file); File.Delete(file); }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch { /* Locked running helper and unknown files are retained. */ }
    }
}
