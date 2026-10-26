using System.Diagnostics;
using System.Text;
using CodexQuota.Core;

internal static class CliDiscoveryTests
{
    public static void Register(Action<string, Action> test)
    {
        test("CLI discovery: explicit override precedes PATH without desktop access", () =>
        {
            using var files = new TempDirectory();
            var explicitPath = files.Touch("selected & official.exe");
            files.Touch(Path.Combine("path", "codex.exe"));
            var start = Find(files, explicitPath, Path.Combine(files.Path, "path"));
            Check.Equal(explicitPath, start!.FileName);
        });
        test("CLI discovery: missing explicit override never selects PATH", () =>
        {
            using var files = new TempDirectory();
            files.Touch("codex.exe");
            Check.Null(Find(files, Path.Combine(files.Path, "missing.exe"), files.Path));
        });
        test("CLI discovery: quoted absolute PATH selects only first standalone executable", () =>
        {
            using var files = new TempDirectory();
            var cli = files.Touch("codex.exe");
            Check.Equal(cli, Find(files, pathValue: '"' + files.Path + '"')!.FileName);
        });
        test("CLI discovery: npm fallback never invokes a shell", () =>
        {
            using var files = new TempDirectory();
            files.Touch(Path.Combine("npm", "codex.cmd"));
            var script = files.Touch(Path.Combine("npm", "node_modules", "@openai", "codex", "bin", "codex.js"));
            var node = files.Touch(Path.Combine("npm", "node.exe"));
            var start = Find(files)!;
            Check.Equal(node, start.FileName);
            Check.False(start.UseShellExecute);
            Check.True(start.ArgumentList.SequenceEqual(new[] { script, "app-server" }));
        });
        test("Desktop discovery: registered native resource is selected, never Electron", () =>
        {
            using var files = new TempDirectory();
            files.Touch(Path.Combine("app", "Codex.exe"));
            files.Touch(Path.Combine("app", "ChatGPT.exe"));
            var native = files.Touch(Path.Combine("app", "resources", "codex.exe"));
            var start = Desktop(files, new[] { files.Path })!;
            Check.Equal(native, start.FileName);
            Check.True(start.ArgumentList.SequenceEqual(new[] { "app-server" }));
            Check.Equal("", start.Arguments);
            Check.False(start.UseShellExecute);
            Check.True(start.CreateNoWindow);
            Check.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
            Check.Equal(Encoding.UTF8.WebName, start.StandardOutputEncoding!.WebName);
            Check.Equal(Encoding.UTF8.WebName, start.StandardErrorEncoding!.WebName);
            Check.Equal(0, start.StandardInputEncoding!.GetPreamble().Length);
            Check.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), start.WorkingDirectory);
        });
        test("Desktop discovery: missing native resource does not launch UI or standalone CLI", () =>
        {
            using var files = new TempDirectory();
            files.Touch(Path.Combine("app", "Codex.exe"));
            files.Touch(Path.Combine("app", "ChatGPT.exe"));
            files.Touch("codex.exe");
            Check.Null(Desktop(files, new[] { files.Path }));
        });
        test("Desktop discovery: provider order and non-default package volumes are preserved", () =>
        {
            using var files = new TempDirectory();
            var root = Path.Combine(files.Path, "另一磁盘 & apps", "registered codex");
            var native = files.Touch(Path.Combine("另一磁盘 & apps", "registered codex", "app", "resources", "codex.exe"));
            files.Touch(Path.Combine("older", "app", "resources", "codex.exe"));
            Check.Equal(native, Desktop(files, new[] { Path.Combine(files.Path, "removed"), root, Path.Combine(files.Path, "older") })!.FileName);
        });
        test("CLI discovery: invalid PATH and incomplete npm never trigger another source", () =>
        {
            using var files = new TempDirectory();
            files.Touch(Path.Combine("npm", "codex.cmd"));
            files.Touch(Path.Combine("app", "resources", "codex.exe"));
            var invalid = string.Join(Path.PathSeparator, new[] { "", ".", "relative", files.Path + '\0' });
            Check.Null(Find(files, pathValue: invalid));
        });
        test("CLI discovery: Node is selected only from an absolute PATH entry", () =>
        {
            using var files = new TempDirectory();
            var shim = files.Touch(Path.Combine("npm", "codex.cmd"));
            files.Touch(Path.Combine("npm", "node_modules", "@openai", "codex", "bin", "codex.js"));
            var node = files.Touch(Path.Combine("node installation", "node.exe"));
            var pathValue = "." + Path.PathSeparator + Path.Combine(files.Path, "node installation");
            Check.Equal(node, Find(files, shim, pathValue)!.FileName);
            Check.Null(Find(files, shim, "."));
        });
        test("Desktop discovery: package candidates are narrow, absolute, deduplicated and bounded", () =>
        {
            using var files = new TempDirectory();
            var candidates = CliLocator.DesktopCandidates(new[] { "", ".", "relative", "bad\0path", files.Path, files.Path }).ToArray();
            Check.Equal(1, candidates.Length);
            Check.Equal(Path.Combine(files.Path, "app", "resources", "codex.exe"), candidates[0]);
            Check.Equal(64, CliLocator.DesktopCandidates(Enumerable.Range(0, 1000)
                .Select(index => Path.Combine(files.Path, index.ToString()))).Count());
        });
        test("CLI discovery: non-Windows never queries Windows npm or foreign providers", () =>
        {
            using var files = new TempDirectory();
            Check.Null(CliLocator.Discover(QuotaDataSource.Cli, null, () => "", NeverString, false,
                NeverDiscover, NeverDiscover).Candidate);
        });
    }
    private static ProcessStartInfo? Find(TempDirectory files, string? explicitPath = null, string? pathValue = null) =>
        CliLocator.Discover(QuotaDataSource.Cli, explicitPath, () => pathValue, () => files.Path, true,
            NeverDiscover, NeverDiscover).Candidate?.StartInfo;
    private static ProcessStartInfo? Desktop(TempDirectory files, IEnumerable<string> roots) =>
        CliLocator.Discover(QuotaDataSource.Desktop, null, NeverString, NeverString, true,
            () => roots, NeverDiscover).Candidate?.StartInfo;
    private static string NeverString() => throw new TestFailure("Unselected source provider was read.");
    private static IEnumerable<string> NeverDiscover() => throw new TestFailure("Unselected source was searched.");
}
