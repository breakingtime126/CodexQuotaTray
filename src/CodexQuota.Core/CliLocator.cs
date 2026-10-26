using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

[assembly: InternalsVisibleTo("CodexQuota.Tests")]

namespace CodexQuota.Core;

public static class CliLocator
{
    // Query only this publisher's registered package, never enumerate WindowsApps.
    // Official deployment docs identify the MSIX package as OpenAI.Codex:
    // https://learn.chatgpt.com/docs/enterprise/windows-deployment
    internal const string DesktopPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";

    // Compatibility entry points now mean the standalone CLI source only.
    public static ProcessStartInfo? Find(string? explicitPath = null) =>
        Discover(QuotaDataSource.Cli, explicitPath).Candidate?.StartInfo;

    public static ProcessStartInfo? Find(QuotaDataSource source, string? explicitPath = null) =>
        Discover(source, explicitPath).Candidate?.StartInfo;

    internal static IEnumerable<CliCandidate> FindCandidates(string? explicitPath = null) =>
        SingleCandidate(Discover(QuotaDataSource.Cli, explicitPath));

    internal static ProcessStartInfo? Find(string? explicitPath, string? pathValue,
        string applicationData, bool windows, Func<IEnumerable<string>> desktopPackageRoots) =>
        Discover(QuotaDataSource.Cli, explicitPath, () => pathValue, () => applicationData, windows,
            desktopPackageRoots, () => Array.Empty<string>()).Candidate?.StartInfo;

    internal static IEnumerable<CliCandidate> FindCandidates(string? explicitPath, string? pathValue,
        string applicationData, bool windows, Func<IEnumerable<string>> desktopPackageRoots,
        Func<IEnumerable<string>> vsCodeExtensionRoots) =>
        SingleCandidate(Discover(QuotaDataSource.Cli, explicitPath, () => pathValue, () => applicationData,
            windows, desktopPackageRoots, vsCodeExtensionRoots));

    private static IEnumerable<CliCandidate> SingleCandidate(SourceBackendDiscovery discovery)
    {
        if (discovery.Candidate is { } candidate) yield return candidate;
    }

    internal static SourceBackendDiscovery Discover(QuotaDataSource source, string? explicitPath = null,
        CancellationToken cancellationToken = default)
    {
        // Desktop uses a separate, read-only evidence boundary. The other source providers,
        // their ordering and their candidate selection are unchanged.
        if (source == QuotaDataSource.Desktop && IsWindows && string.IsNullOrWhiteSpace(explicitPath))
            return DesktopRuntimeDiscovery.Discover(new WindowsDesktopRuntimeEvidence(cancellationToken: cancellationToken));
        return Discover(source, explicitPath, () => Environment.GetEnvironmentVariable("PATH"),
            () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), IsWindows,
            GetDesktopPackageRoots, GetVsCodeExtensionRoots);
    }

    // Providers are lazy by category. Selecting Desktop or VS Code never even reads PATH/npm;
    // selecting CLI never invokes package or extension providers, including on failure.
    internal static SourceBackendDiscovery Discover(QuotaDataSource source, string? explicitPath,
        Func<string?> pathValue, Func<string> applicationData, bool windows,
        Func<IEnumerable<string>> desktopPackageRoots, Func<IEnumerable<string>> vsCodeExtensionRoots)
    {
        ValidateSource(source);
        var examined = 0;
        var excluded = 0;
        var reasons = SourceBackendExclusion.None;
        SourceBackendDiscovery Finish(CliCandidate? candidate = null, int? code = null) =>
            new(candidate, examined, excluded, reasons, code);
        void Exclude(SourceBackendExclusion reason) { excluded++; reasons |= reason; }
        CliCandidate? Inspect(string? path, CliSource origin, string[] directories)
        {
            examined++;
            if (path is null || !IsFullyQualified(path)) { Exclude(SourceBackendExclusion.InvalidPath); return null; }
            if (source == QuotaDataSource.Cli && ForeignCliLocation(path) is { } foreign)
            { Exclude(foreign); return null; }
            // File.Exists hides access-denied and malformed file errors. Preserve a closed
            // reason/code from an explicit metadata probe instead of reporting false absence.
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                { Exclude(SourceBackendExclusion.UnsupportedLayout); return null; }
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { Exclude(SourceBackendExclusion.Missing); return null; }
            var start = Resolve(path, windows, directories);
            if (start is null) { Exclude(SourceBackendExclusion.UnsupportedLayout); return null; }
            return new(origin, start)
            {
                Location = Location(origin),
                Component = origin switch
                {
                    CliSource.Desktop => SourceBackendComponent.DesktopBundledCodex,
                    CliSource.VsCode => SourceBackendComponent.VsCodeBundledCodex,
                    _ => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                        ? SourceBackendComponent.CodexNpmEntry : SourceBackendComponent.CodexExecutable
                }
            };
        }
        try
        {
            if (source != QuotaDataSource.Cli && !string.IsNullOrWhiteSpace(explicitPath))
            { Exclude(SourceBackendExclusion.SourceMismatch); return Finish(); }
            switch (source)
            {
                case QuotaDataSource.Cli:
                {
                    var directories = PathDirectories(pathValue()).Take(128)
                        .Distinct(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
                    if (!string.IsNullOrWhiteSpace(explicitPath))
                        return Finish(Inspect(explicitPath, CliSource.Explicit, directories));
                    foreach (var directory in directories)
                    {
                        foreach (var name in windows ? new[] { "codex.exe", "codex.cmd" } : new[] { "codex" })
                        {
                            var candidate = Inspect(CombineAbsolute(directory, name), CliSource.Path, directories);
                            if (candidate is not null) return Finish(candidate);
                        }
                    }
                    if (windows)
                    {
                        var candidate = Inspect(CombineAbsolute(applicationData(), "npm", "codex.cmd"),
                            CliSource.Npm, directories);
                        if (candidate is not null) return Finish(candidate);
                    }
                    break;
                }
                case QuotaDataSource.Desktop:
                    if (!windows) { Exclude(SourceBackendExclusion.UnsupportedPlatform); break; }
                    foreach (var path in DesktopCandidates(desktopPackageRoots()))
                    {
                        var candidate = Inspect(path, CliSource.Desktop, Array.Empty<string>());
                        if (candidate is not null) return Finish(candidate);
                    }
                    break;
                case QuotaDataSource.VsCode:
                    if (!windows) { Exclude(SourceBackendExclusion.UnsupportedPlatform); break; }
                    foreach (var path in VsCodeCandidates(vsCodeExtensionRoots()))
                    {
                        var candidate = Inspect(path, CliSource.VsCode, Array.Empty<string>());
                        if (candidate is not null) return Finish(candidate);
                    }
                    break;
            }
            return Finish();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException or Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            Exclude(error is DllNotFoundException or EntryPointNotFoundException
                ? SourceBackendExclusion.ProviderUnavailable : error is ArgumentException or NotSupportedException
                ? SourceBackendExclusion.InvalidPath : SourceBackendExclusion.Inaccessible);
            return Finish(code: error is Win32Exception native ? native.NativeErrorCode : error.HResult);
        }
    }

    internal static void ValidateSource(QuotaDataSource source)
    {
        if (source is not (QuotaDataSource.Cli or QuotaDataSource.Desktop or QuotaDataSource.VsCode))
            throw new ArgumentOutOfRangeException(nameof(source));
    }

    internal static SourceBackendLocation Location(CliSource source) => source switch
    {
        CliSource.Explicit => SourceBackendLocation.CliExplicit,
        CliSource.Path => SourceBackendLocation.CliPath,
        CliSource.Npm => SourceBackendLocation.CliNpm,
        CliSource.Desktop => SourceBackendLocation.DesktopPackageResource,
        CliSource.VsCode => SourceBackendLocation.VsCodeExtensionBinary,
        _ => SourceBackendLocation.None
    };

    private static SourceBackendExclusion? ForeignCliLocation(string path)
    {
        var normalized = path.Replace('\\', '/');
        // A PATH alias can resolve to another app and is not proof of a standalone CLI.
        // Refuse it rather than launching it to inspect its identity or following its target.
        if (normalized.EndsWith("/app/resources/codex.exe", StringComparison.OrdinalIgnoreCase) ||
            normalized.Split('/').Any(part => part.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)))
            return SourceBackendExclusion.DesktopComponent;
        if (normalized.Split('/').Any(part => part.StartsWith("openai.chatgpt-", StringComparison.OrdinalIgnoreCase)) ||
            normalized.IndexOf("/.vscode/extensions/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("/.vscode-insiders/extensions/", StringComparison.OrdinalIgnoreCase) >= 0)
            return SourceBackendExclusion.VsCodeComponent;
        if (normalized.IndexOf("/WindowsApps/", StringComparison.OrdinalIgnoreCase) >= 0)
            return SourceBackendExclusion.WindowsAppsAlias;
        return null;
    }

    internal static IEnumerable<string> VsCodeCandidates(IEnumerable<string> extensionRoots)
    {
        // Local official extension only. No WSL/remote stores, forks, or recursive disk search.
        // Observed Windows backend layout: https://github.com/openai/codex/issues/28982
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in extensionRoots.Take(4))
        {
            foreach (var directory in ExtensionDirectories(root).OrderByDescending(ExtensionVersion).ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (ExtensionVersion(directory) is null) continue;
                var candidate = CombineAbsolute(directory, "bin", "windows-x86_64", "codex.exe");
                if (candidate is not null && seen.Add(candidate)) yield return candidate;
            }
        }
    }

    private static string[] ExtensionDirectories(string root)
    {
        if (!IsFullyQualified(root)) return Array.Empty<string>();
        try { return Directory.EnumerateDirectories(root, "openai.chatgpt-*").Take(256).ToArray(); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        // Access and provider errors propagate to the selected-source diagnostic boundary.
    }

    private static Version? ExtensionVersion(string directory)
    {
        const string prefix = "openai.chatgpt-";
        var name = Path.GetFileName(directory);
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var version = name.Substring(prefix.Length);
        const string platform = "-win32-x64";
        if (version.EndsWith(platform, StringComparison.OrdinalIgnoreCase))
            version = version.Substring(0, version.Length - platform.Length);
        // Reject lookalike publishers, other platforms, and arbitrary suffixes.
        return Version.TryParse(version, out var parsed) ? parsed : null;
    }

    private static IEnumerable<string> GetVsCodeExtensionRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var folder in new[] { ".vscode", ".vscode-insiders" })
        {
            var root = CombineAbsolute(home, folder, "extensions");
            if (root is not null) yield return root;
        }
    }

    internal static IEnumerable<string> DesktopCandidates(IEnumerable<string> registeredPackageRoots)
    {
        // The native CLI is a bundled resource, not app\Codex.exe / app\ChatGPT.exe
        // (the Electron UI). Reported installed layout in OpenAI's issue tracker:
        // https://github.com/openai/codex/issues/41125
        // https://github.com/openai/codex/issues/43162
        // This is best-effort discovery, not a guarantee of a stable desktop API.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in registeredPackageRoots.Take(64))
        {
            var candidate = CombineAbsolute(root, "app", "resources", "codex.exe");
            if (candidate is not null && seen.Add(candidate)) yield return candidate;
        }
    }

    internal static ProcessStartInfo ResolveDesktopExecutable(string path) =>
        Resolve(path, windows: true, Array.Empty<string>()) ??
        throw DesktopRuntimeDiscovery.Reject(SourceBackendExclusion.DesktopFileChanged);

    private static ProcessStartInfo? Resolve(string path, bool windows, string[] directories)
    {
        if (!File.Exists(path)) return null;
        string executable = path;
        string? script = null;
        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // npm's official shim has a known neighboring JS entry point. Never execute shell text.
            script = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "@openai", "codex", "bin", "codex.js");
            if (!File.Exists(script)) return null;
            executable = FindNode(Path.GetDirectoryName(path)!, windows, directories) ?? "";
        }
        else if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            script = path;
            executable = FindNode(Path.GetDirectoryName(path)!, windows, directories) ?? "";
        }
        else if (windows && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrEmpty(executable)) return null;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
#if !NETFRAMEWORK
            StandardInputEncoding = new UTF8Encoding(false),
#endif
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
#if NETFRAMEWORK
        // .NET Framework lacks ArgumentList. CRT quoting keeps JS paths (including
        // spaces and shell metacharacters) a single argument; no shell is involved.
        start.Arguments = (script is null ? "" : StartupCommand.QuoteArgument(script) + " ") + "app-server";
#else
        if (script is not null) start.ArgumentList.Add(script);
        start.ArgumentList.Add("app-server"); // Default transport is stdio; supports older CLI versions.
#endif
        return start;
    }

    private static string? FindNode(string siblingDirectory, bool windows, IEnumerable<string> directories)
    {
        var filename = windows ? "node.exe" : "node";
        var sibling = Path.Combine(siblingDirectory, filename);
        if (File.Exists(sibling)) return sibling;
        foreach (var directory in directories)
        {
            var candidate = CombineAbsolute(directory, filename);
            if (candidate is not null && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> PathDirectories(string? pathValue)
    {
        foreach (var directory in (pathValue ?? "").Split(Path.PathSeparator))
        {
            var clean = directory.Trim().Trim('"');
            // Never search a relative entry, empty entry, or the working directory implicitly.
            if (IsFullyQualified(clean)) yield return clean;
        }
    }

    private static string? CombineAbsolute(string root, params string[] children)
    {
        if (!IsFullyQualified(root)) return null;
        try
        {
            var path = root;
            foreach (var child in children) path = Path.Combine(path, child);
            return path;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
        catch (SecurityException) { return null; }
    }

    private static bool IsFullyQualified(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0) return false;
#if NETFRAMEWORK
        // Path.IsPathRooted accepts C:relative and \root-relative on Windows.
        // Neither is safe for automatic executable lookup.
        var driveRooted = path.Length >= 3 &&
            ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) &&
            path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        var uncRooted = path.Length > 2 && path[0] == '\\' && path[1] == '\\' &&
            path.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2;
        return driveRooted || uncRooted;
#else
        return Path.IsPathFullyQualified(path);
#endif
    }

    private static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

    internal static IEnumerable<DesktopPackageRegistration> GetDesktopPackages()
    {
        if (!IsWindows) return Array.Empty<DesktopPackageRegistration>();
        return GetDesktopPackageNames().OrderByDescending(PackageVersion).ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new DesktopPackageRegistration(name, GetRegisteredPackagePath(name)));
    }

    private static IEnumerable<string> GetDesktopPackageRoots()
    {
        if (!IsWindows) return Array.Empty<string>();
        // These supported APIs scope discovery to the current user's exact package family.
        // https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getpackagesbypackagefamily
        // https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getpackagepathbyfullname
        return GetDesktopPackageNames().OrderByDescending(PackageVersion).ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => GetRegisteredPackagePath(name));
    }

    internal delegate int PackageFamilyQuery(ref uint count, IntPtr names, ref uint length, IntPtr buffer);
    internal delegate int PackagePathQuery(string fullName, ref uint length, StringBuilder? path);

    internal static string[] GetDesktopPackageNames(PackageFamilyQuery? query = null) =>
        DesktopEvidenceOperation.Run(DesktopEvidenceStep.PackageFamilyQuery, DesktopEvidenceLocation.PackageRegistration,
            () => ReadDesktopPackageNames(query ?? QueryPackageFamily));

    private static int QueryPackageFamily(ref uint count, IntPtr names, ref uint length, IntPtr buffer) =>
        GetPackagesByPackageFamily(DesktopPackageFamily, ref count, names, ref length, buffer);

    private static string[] ReadDesktopPackageNames(PackageFamilyQuery query)
    {
        const int insufficientBuffer = 122;
        // An installation can change between the sizing and read calls. One bounded retry.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            uint count = 0, length = 0;
            var status = query(ref count, IntPtr.Zero, ref length, IntPtr.Zero);
            if (status == 0 && count == 0) return Array.Empty<string>();
            if (status != insufficientBuffer) throw new Win32Exception(status);
            if (count == 0 || count > 64 || length == 0 || length > 65536)
                throw new InvalidDataException("Package registration result exceeded bounds.");
            var names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
            var buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
                status = query(ref count, names, ref length, buffer);
                if (status == insufficientBuffer) continue;
                if (status != 0) throw new Win32Exception(status);
                var results = new List<string>();
                for (var index = 0; index < count; index++)
                {
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * IntPtr.Size));
                    if (name is not null) results.Add(name);
                }
                return results.ToArray();
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(names);
            }
        }
        throw new Win32Exception(insufficientBuffer);
    }

    private static Version PackageVersion(string packageFullName)
    {
        var fields = packageFullName.Split('_');
        return fields.Length == 5 && Version.TryParse(fields[1], out var version) ? version : new Version(0, 0);
    }

    internal static string GetRegisteredPackagePath(string packageFullName, PackagePathQuery? query = null) =>
        DesktopEvidenceOperation.Run(DesktopEvidenceStep.PackagePathQuery, DesktopEvidenceLocation.PackageRegistration,
            () => ReadRegisteredPackagePath(packageFullName, query ?? GetPackagePathByFullName));

    private static string ReadRegisteredPackagePath(string packageFullName, PackagePathQuery query)
    {
        uint length = 0;
        var status = query(packageFullName, ref length, null);
        if (status != 122) throw new Win32Exception(status);
        if (length == 0 || length > 32768) throw new InvalidDataException("Package path exceeded bounds.");
        var path = new StringBuilder((int)length);
        status = query(packageFullName, ref length, path);
        if (status != 0) throw new Win32Exception(status);
        return path.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count,
        IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength,
        StringBuilder? path);
}
