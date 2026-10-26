using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodexQuota.Core;

namespace CodexQuota.Tray;

internal static class UpdateTrust
{
    public static RSAParameters PublicKey
    {
        get
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Environment.CurrentDirectory, "public-key.json")));
            return new RSAParameters { Modulus = Convert.FromBase64String(json.RootElement.GetProperty("modulus").GetString()!),
                Exponent = Convert.FromBase64String(json.RootElement.GetProperty("exponent").GetString()!) };
        }
    }
}
internal static class UpdateFailureStore
{
    public static void Record(string version) => File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "fixture-failed-version"), version);
}
internal static class Program
{
    internal static void Run(string[] args)
    {
        if (UpdateTransaction.TryRun(args)) return;
        var root = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
        if (args.Length >= 3 && args[0] == "--update-health")
        {
            var stage = args[1];
            if (File.Exists(Path.Combine(stage, "fail-health"))) return;
            if (File.Exists(Path.Combine(stage, "lock-health")))
            {
                using var marker = new FileStream(Path.Combine(stage, "healthy"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var bytes = System.Text.Encoding.UTF8.GetBytes(args[2]); marker.Write(bytes, 0, bytes.Length); marker.Flush(true);
                Thread.Sleep(1000);
            }
            else File.WriteAllText(Path.Combine(stage, "healthy"), args[2]);
            File.WriteAllText(Path.Combine(root, "new-pid"), Process.GetCurrentProcess().Id.ToString());
        }
        else if (args.Length == 1 && args[0] == "--fixture-old")
        {
            File.WriteAllText(Path.Combine(root, "old-started"), "yes");
            while (!File.Exists(Path.Combine(root, "exit-old")) && !File.Exists(Path.Combine(root, "stop"))) Thread.Sleep(20);
            if (File.Exists(Path.Combine(root, "delay-old")) && !File.Exists(Path.Combine(root, "stop"))) Thread.Sleep(65000);
            return;
        }
        else File.WriteAllText(Path.Combine(root, "restarted-old"), Process.GetCurrentProcess().Id.ToString());
        while (!File.Exists(Path.Combine(root, "stop"))) Thread.Sleep(20);
    }
}
