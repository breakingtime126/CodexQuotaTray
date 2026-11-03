using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>No credentials or raw account/card identifiers. An exclusive OS file handle
/// spans validation, durable intent, mutation and reconciliation across tray processes.</summary>
internal sealed class AutoResetCreditStore
{
    private readonly string _directory;
    internal AutoResetCreditStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray", "reset-attempts");

    internal IDisposable Acquire()
    {
        Directory.CreateDirectory(_directory);
        return new FileStream(Path.Combine(_directory, "operation.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    internal static string Identity(string account, string credit)
    {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
            account.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + account + credit)))
            .Replace("-", "").ToLowerInvariant();
    }

    internal ResetCreditAttempt? Read(string identity)
    {
        var path = Path.Combine(_directory, identity + ".json");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4096) throw new InvalidDataException("Invalid reset attempt.");
            var attempt = JsonSerializer.Deserialize<ResetCreditAttempt>(file);
            if (attempt is null || attempt.Version != 1 || !Guid.TryParseExact(attempt.Key, "D", out _) ||
                attempt.Sends is < 1 or > 2 || attempt.ExpiresAt <= attempt.LastAttempt ||
                (attempt.LastAttemptMilliseconds != 0 && attempt.LastAttemptMilliseconds / 1000 != attempt.LastAttempt) ||
                (attempt.RetryNotBeforeMilliseconds != 0 && attempt.RetryNotBeforeMilliseconds < attempt.LastAttemptMilliseconds) ||
                attempt.State is not ("pending" or "reset" or "alreadyRedeemed" or "nothingToReset" or "noCredit" or "unsupported"))
                throw new InvalidDataException("Invalid reset attempt.");
            return attempt;
        }
        catch (FileNotFoundException) { return null; }
    }

    internal string? ReadPending(string account)
    {
        var path = Path.Combine(_directory, "account-" + Identity(account, "") + ".json");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 1024) throw new InvalidDataException("Invalid pending operation.");
            var identity = JsonSerializer.Deserialize<string>(file);
            if (identity is null || identity.Length != 64 || identity.Any(ch => !(ch is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Invalid pending operation.");
            return identity;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal void SavePending(string account, string identity) => WriteAtomic(
        Path.Combine(_directory, "account-" + Identity(account, "") + ".json"), JsonSerializer.SerializeToUtf8Bytes(identity));
    internal void ClearPending(string account) => File.Delete(
        Path.Combine(_directory, "account-" + Identity(account, "") + ".json"));

    internal void Save(string identity, ResetCreditAttempt attempt) => WriteAtomic(
        Path.Combine(_directory, identity + ".json"), JsonSerializer.SerializeToUtf8Bytes(attempt));

    private void WriteAtomic(string target, byte[] bytes)
    {
        var temporary = Path.Combine(_directory, ".pending-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed record ResetCreditAttempt(int Version, string Key, long ExpiresAt, long LastAttempt, int Sends, string State)
{
    public long LastAttemptMilliseconds { get; init; }
    public long RetryNotBeforeMilliseconds { get; init; }
}
