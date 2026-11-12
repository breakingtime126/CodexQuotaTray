using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Microsoft.Win32.SafeHandles;

namespace CodexQuota.Core;

/// <summary>Read-only OS evidence. No PowerShell, shell execution, credential access or disk-wide search.</summary>
internal sealed class WindowsDesktopRuntimeEvidence : IDesktopRuntimeEvidence, IDesktopRuntimeEvidencePhase
{
    private readonly Func<string, int> _packageOrigin;
    private readonly Func<IEnumerable<DesktopPackageRegistration>> _registeredPackages;
    private readonly DesktopEvidenceBudget _budget;
    internal WindowsDesktopRuntimeEvidence(Func<string, int>? packageOrigin = null,
        Func<IEnumerable<DesktopPackageRegistration>>? registeredPackages = null,
        CancellationToken cancellationToken = default, DesktopEvidenceBudget? budget = null)
    {
        _packageOrigin = packageOrigin ?? GetPackageOrigin;
        _registeredPackages = registeredPackages ?? CliLocator.GetDesktopPackages;
        _budget = budget ?? new DesktopEvidenceBudget(cancellationToken: cancellationToken);
    }
    private readonly HashSet<string> _packageImages = new(StringComparer.OrdinalIgnoreCase);
    public void BeginEvidencePhase() => _budget.BeginPhase();
    public IEnumerable<DesktopPackageRegistration> RegisteredPackages()
    {
        _budget.Check(DesktopEvidenceStep.PackageFamilyQuery, DesktopEvidenceLocation.PackageRegistration);
        _packageImages.Clear();
        return ReadRegistrations();
    }
    private IEnumerable<DesktopPackageRegistration> ReadRegistrations()
    {
        // Keep registration paths lazy: only the newest registration is validated. Bracket
        // MoveNext as well as the factory, since path queries execute during enumeration.
        using var iterator = DesktopEvidenceOperation.Run(DesktopEvidenceStep.PackageFamilyQuery,
            DesktopEvidenceLocation.PackageRegistration, () => _registeredPackages().GetEnumerator(), _budget);
        while (DesktopEvidenceOperation.Run(DesktopEvidenceStep.PackageFamilyQuery,
            DesktopEvidenceLocation.PackageRegistration, iterator.MoveNext, _budget))
            yield return iterator.Current;
    }
    public string LocalApplicationData => DesktopEvidenceOperation.Run(DesktopEvidenceStep.LocalPathValidation,
        DesktopEvidenceLocation.RuntimeRoot, () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _budget);

    public bool VerifyPackageIdentity(DesktopPackageRegistration package)
    {
        _budget.Check(DesktopEvidenceStep.PackageIdentityValidation, DesktopEvidenceLocation.PackageRegistration);
        if (!DesktopRuntimeDiscovery.ValidRegistration(package)) return false;
        if (DesktopEvidenceOperation.Run(DesktopEvidenceStep.PackageOriginQuery, DesktopEvidenceLocation.PackageRegistration,
                () => _packageOrigin(package.FullName), _budget) != 3) return false;
        _packageImages.Add(DesktopRuntimeDiscovery.ResourcePath(package));
        // Store origin excludes a same-family loose/developer registration. Other origins
        // may be legitimate distributions but are not verified by this Store-only adapter.
        using var manifest = DesktopReadOnlyFileLease.Open(Path.Combine(package.Root, "AppxManifest.xml"), DesktopEvidenceLocation.PackageManifest, _budget);
        return DesktopEvidenceOperation.Run(DesktopEvidenceStep.ManifestRead, DesktopEvidenceLocation.PackageManifest,
            () => ReadManifest(package, manifest.Stream), _budget);
    }

    private bool ReadManifest(DesktopPackageRegistration package, Stream stream)
    {
        if (stream.Length > 1024 * 1024) return false;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024,
            IgnoreComments = true, IgnoreProcessingInstructions = true
        });
        string? name = null, publisher = null, version = null, architecture = null;
        var identities = 0;
        var packageElements = 0;
        const string ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        while (DesktopEvidenceOperation.Run(DesktopEvidenceStep.ManifestRead,
            DesktopEvidenceLocation.PackageManifest, reader.Read, _budget))
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Depth == 0)
            {
                if (++packageElements != 1 || reader.LocalName != "Package" || reader.NamespaceURI != ns) return false;
                continue;
            }
            if (reader.LocalName != "Identity") continue;
            if (reader.Depth != 1 || reader.NamespaceURI != ns || ++identities != 1) return false;
            name = reader.GetAttribute("Name"); publisher = reader.GetAttribute("Publisher");
            version = reader.GetAttribute("Version"); architecture = reader.GetAttribute("ProcessorArchitecture");
        }
        return packageElements == 1 && identities == 1 && DesktopRuntimeDiscovery.ManifestMatches(package, name, publisher, version, architecture);
    }

    public IEnumerable<string> ExistingRuntimePaths(string localApplicationData)
    {
        _budget.Check(DesktopEvidenceStep.DirectoryEnumeration, DesktopEvidenceLocation.RuntimeRoot);
        if (!DesktopRuntimeDiscovery.IsCanonicalLocalPath(localApplicationData))
            throw DesktopRuntimeDiscovery.Reject(SourceBackendExclusion.DesktopUnsafePath);
        var root = Path.Combine(localApplicationData, "OpenAI", "Codex", "bin");
        DesktopReadOnlyFileLease.DirectoryLease directory;
        try { directory = DesktopReadOnlyFileLease.PinDirectory(root, _budget); }
        catch (Exception error) when (DesktopReadOnlyFileLease.IsMissingTargetOpen(error,
            DesktopEvidenceLocation.RuntimeRoot, System.IO.Path.DirectorySeparatorChar == '\\'))
        {
            _budget.Check(DesktopEvidenceStep.DirectoryEnumeration, DesktopEvidenceLocation.RuntimeRoot);
            return Array.Empty<string>();
        }
        using (directory)
        {
            var candidates = new List<string>();
            if (directory.HasFile("codex.exe")) candidates.Add(Path.Combine(root, "codex.exe"));
            // Enumerate the retained directory object, not its possibly replaced pathname.
            // Both file probes and child opens are relative and forbid reparse traversal.
            foreach (var child in directory.ChildDirectoryNames().OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                _budget.Check(DesktopEvidenceStep.DirectoryEnumeration, DesktopEvidenceLocation.RuntimeRoot);
                var path = Path.Combine(root, child, "codex.exe");
                if (!DesktopRuntimeDiscovery.IsRuntimePath(localApplicationData, path)) continue;
                try
                {
                    using var childDirectory = directory.OpenChild(child);
                    if (childDirectory.HasFile("codex.exe")) candidates.Add(path);
                }
                catch (Exception error) when (DesktopRuntimeDiscovery.FailureReason(error) == SourceBackendExclusion.Missing)
                { throw new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopFileChanged,
                    DesktopRuntimeDiscovery.FailureCode(error), (error as DesktopRuntimeValidationException)?.Desktop,
                    (error as DesktopRuntimeValidationException)?.EvidenceReadFailure ?? true); }
            }
            _budget.Check(DesktopEvidenceStep.DirectoryEnumeration, DesktopEvidenceLocation.RuntimeRoot);
            return candidates;
        }
    }

    private static int GetPackageOrigin(string fullName)
    {
        var status = GetStagedPackageOrigin(fullName, out var origin);
        if (status != 0) throw new Win32Exception(status);
        return origin;
    }
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetStagedPackageOrigin(string packageFullName, out int origin);

    public IDesktopImageLease OpenImage(string path, DesktopImageIdentity? expected = null)
    {
        var location = _packageImages.Contains(path) ? DesktopEvidenceLocation.PackageImage : DesktopEvidenceLocation.RuntimeImage;
        return DesktopEvidenceOperation.Run(DesktopEvidenceStep.ImageRead, location,
            () => OpenVerifiedImage(path, expected, location), _budget);
    }

    private IDesktopImageLease OpenVerifiedImage(string path, DesktopImageIdentity? expected, DesktopEvidenceLocation location)
    {
        var lease = DesktopReadOnlyFileLease.Open(path, location, _budget);
        try
        {
            var identity = ReadImageIdentity(lease.Stream, expected, _budget, location,
                () =>
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(lease.Path);
                    return string.IsNullOrWhiteSpace(versionInfo.FileVersion) ? null :
                        new Version(versionInfo.FileMajorPart, versionInfo.FileMinorPart,
                            versionInfo.FileBuildPart, versionInfo.FilePrivatePart);
                }, lease.RevalidatePath);
            return new ImageLease(lease, identity);
        }
        catch { lease.Dispose(); throw; }
    }

    // Production and synthetic tests share this metadata-only path. No application-level
    // image hashing or Authenticode verification is performed for quota queries.
    internal static DesktopImageIdentity ReadImageIdentity(Stream stream, DesktopImageIdentity? expected,
        DesktopEvidenceBudget budget, DesktopEvidenceLocation location,
        Func<Version?> readVersion, Action revalidatePath)
    {
        budget.CheckImageSize(stream.Length, location);
        if ((expected is not null && stream.Length != expected.Length) ||
            !DesktopEvidenceOperation.Run(DesktopEvidenceStep.ImageRead, location, () => IsX64Executable(stream), budget))
            throw DesktopRuntimeDiscovery.Reject(SourceBackendExclusion.DesktopRuntimeUnverified);
        revalidatePath();
        var version = DesktopEvidenceOperation.Run(DesktopEvidenceStep.VersionRead, location, readVersion, budget);
        revalidatePath();
        budget.Check(DesktopEvidenceStep.VersionRead, location);
        return new(stream.Length, version);
    }

    private static bool IsX64Executable(Stream stream)
    {
        if (stream.Length < 128) return false;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        if (reader.ReadUInt16() != 0x5a4d) return false;
        stream.Position = 0x3c;
        var offset = reader.ReadInt32();
        if (offset < 0x40 || offset > stream.Length - 26) return false;
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) return false;
        stream.Position = offset + 22;
        if ((reader.ReadUInt16() & 0x2002) != 0x0002) return false; // executable, not a DLL
        return reader.ReadUInt16() == 0x20b;
    }

    private sealed class ImageLease(DesktopReadOnlyFileLease lease, DesktopImageIdentity identity) : IDesktopImageLease
    {
        public string Path => lease.Path;
        public DesktopImageIdentity Identity { get; } = identity;
        public void Dispose() => lease.Dispose();
    }
}

/// <summary>
/// Opens existing targets relative to a local volume root. OBJ_DONT_REPARSE rejects a
/// reparse anywhere in the complete relative path; no ancestor metadata/list handle is needed.
/// Retained file handles deny write/delete through validation and the owned process lifetime.
/// Windows rejects renaming a directory with open descendants (MS-FSA 2.1.4.2/2.1.5.15.12).
/// </summary>
internal sealed class DesktopReadOnlyFileLease : IDisposable
{
    private readonly List<IDisposable> _guards;
    private readonly DesktopEvidenceLocation _location;
    private readonly DesktopEvidenceBudget? _budget;
    public string Path { get; }
    public FileStream Stream { get; }
    private DesktopReadOnlyFileLease(string path, FileStream stream, List<IDisposable> guards, DesktopEvidenceLocation location, DesktopEvidenceBudget? budget)
    { Path = path; Stream = stream; _guards = guards; _location = location; _budget = budget; }

    internal static DesktopReadOnlyFileLease Open(string path, DesktopEvidenceLocation location = DesktopEvidenceLocation.RuntimeImage,
        DesktopEvidenceBudget? budget = null)
    {
        var guards = new List<IDisposable>();
        FileStream? stream = null;
        try
        {
            ValidateLocalPath(path, location, budget);
            if (System.IO.Path.DirectorySeparatorChar == '\\')
            {
                var handle = OpenWindowsTarget(path, directory: false, guards, location, WindowsApi.Instance, budget);
                try
                {
                    stream = DesktopEvidenceOperation.Run(DesktopEvidenceStep.OpenReadStream, location,
                        () => new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false), budget);
                }
                catch { handle.Dispose(); throw; }
            }
            else
            {
                ValidateFixtureAncestors(System.IO.Path.GetDirectoryName(path)!, location, budget);
                DesktopEvidenceOperation.Run(DesktopEvidenceStep.TargetAttributes, location,
                    () => EnsureAttributes(File.GetAttributes(path), directory: false), budget);
                stream = DesktopEvidenceOperation.Run(DesktopEvidenceStep.OpenReadStream, location,
                    () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), budget);
            }
            return new(path, stream, guards, location, budget);
        }
        catch { stream?.Dispose(); DisposeGuards(guards); throw; }
    }

    internal static bool IsMissingTargetOpen(Exception error, DesktopEvidenceLocation location, bool windows)
    {
        if (DesktopRuntimeDiscovery.FailureReason(error) != SourceBackendExclusion.Missing) return false;
        if (error is DesktopRuntimeValidationException { Desktop: { } detail })
            return detail.Location == location && (detail.Step == DesktopEvidenceStep.TargetOpen ||
                (!windows && detail.Step == DesktopEvidenceStep.TargetAttributes));
        // Unix fixtures use File.GetAttributes before opening; they are never a Windows
        // provider. Windows handle metadata/name-query errors are not proof of absence.
        return !windows && error is FileNotFoundException or DirectoryNotFoundException;
    }

    internal void RevalidatePath()
    {
        if (System.IO.Path.DirectorySeparatorChar != '\\') return;
        EnsureAttributes(Stream.SafeFileHandle, directory: false, _location, budget: _budget);
        EnsureCanonical(Stream.SafeFileHandle, Path, _location, budget: _budget);
    }

    internal static DirectoryLease PinDirectory(string path, DesktopEvidenceBudget? budget = null)
    {
        var guards = new List<IDisposable>();
        const DesktopEvidenceLocation location = DesktopEvidenceLocation.RuntimeRoot;
        try
        {
            ValidateLocalPath(path, location, budget);
            SafeFileHandle? handle = null;
            if (System.IO.Path.DirectorySeparatorChar == '\\')
            {
                handle = OpenWindowsTarget(path, directory: true, guards, location, WindowsApi.Instance, budget);
                guards.Add(handle);
            }
            else ValidateFixtureAncestors(path, location, budget);
            return new DirectoryLease(path, handle, guards, location, budget);
        }
        catch { DisposeGuards(guards); throw; }
    }

    private static void ValidateLocalPath(string path, DesktopEvidenceLocation location, DesktopEvidenceBudget? budget = null) =>
        DesktopEvidenceOperation.Run(DesktopEvidenceStep.LocalPathValidation, location, () =>
        {
            if (!DesktopRuntimeDiscovery.IsCanonicalLocalPath(path)) throw UnsafePath();
        }, budget);

    private static void ValidateFixtureAncestors(string path, DesktopEvidenceLocation location, DesktopEvidenceBudget? budget = null)
    {
        // Non-Windows is only an isolated fixture implementation, never Desktop discovery.
        var ancestors = new Stack<DirectoryInfo>();
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent) ancestors.Push(item);
        while (ancestors.Count > 0)
            DesktopEvidenceOperation.Run(DesktopEvidenceStep.TargetAttributes, location,
                () => EnsureAttributes(File.GetAttributes(ancestors.Pop().FullName), directory: true), budget);
    }

    internal interface IWindowsFileApi
    {
        uint DriveType(string root);
        SafeFileHandle OpenRoot(string root, uint access, uint sharing, uint disposition, uint flags);
        SafeFileHandle OpenRelative(SafeFileHandle root, string relativePath, uint access, uint objectAttributes,
            uint sharing, uint disposition, uint options);
        FileAttributes Attributes(SafeFileHandle handle);
        string CanonicalPath(SafeFileHandle handle);
    }

    // This is the production open sequence; injected APIs exercise these exact access masks,
    // flags, names, validation calls and disposal rules without invoking Windows on Linux.
    internal static SafeFileHandle OpenWindowsTarget(string path, bool directory, List<IDisposable> guards,
        DesktopEvidenceLocation location, IWindowsFileApi api, DesktopEvidenceBudget? budget = null)
    {
        DesktopEvidenceOperation.Run(DesktopEvidenceStep.LocalPathValidation, location, () =>
        {
            if (path.Length < 4 || !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) ||
                path[1] != ':' || path[2] != '\\' || path.Length > 32760 ||
                path.Substring(3).IndexOfAny(new[] { '/', ':', '\0' }) >= 0 ||
                path.Substring(3).Split('\\').Any(part => part.Length == 0 || part is "." or ".." ||
                    part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))) throw UnsafePath();
        }, budget);
        var rootPath = path.Substring(0, 3);
        DesktopEvidenceOperation.Run(DesktopEvidenceStep.VolumeTypeQuery, DesktopEvidenceLocation.LocalVolumeRoot, () =>
        {
            if (api.DriveType(rootPath) is not (2 or 3 or 6)) throw UnsafePath();
        }, budget);
        // CreateFile explicitly permits zero desired access for metadata. Do not request
        // FILE_READ_ATTRIBUTES, FILE_LIST_DIRECTORY, FILE_TRAVERSE, READ_CONTROL or EA here.
        var root = DesktopEvidenceOperation.Run(DesktopEvidenceStep.RootOpen, DesktopEvidenceLocation.LocalVolumeRoot,
            () => api.OpenRoot(rootPath, 0, 3, 3, 0x00200000 | 0x02000000), budget);
        guards.Add(root);
        EnsureAttributes(root, true, DesktopEvidenceLocation.LocalVolumeRoot, api, root: true, budget: budget);
        EnsureCanonical(root, rootPath, DesktopEvidenceLocation.LocalVolumeRoot, api, root: true, budget: budget);
        var handle = OpenRelative(root, path.Substring(3), directory, location, api, fullRelativePath: true, budget: budget);
        try
        {
            EnsureCanonical(handle, path, location, api, budget: budget);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory,
        DesktopEvidenceLocation location, IWindowsFileApi? api = null, bool fullRelativePath = false, DesktopEvidenceBudget? budget = null)
    {
        api ??= WindowsApi.Instance;
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', ':', '\0' }) >= 0 ||
            (!fullRelativePath && name.IndexOf('\\') >= 0) ||
            name.Split('\\').Any(part => part.Length == 0 || part is "." or "..")) throw UnsafePath();
        // FILE_READ_DATA (or LIST_DIRECTORY), READ_ATTRIBUTES, SYNCHRONIZE only.
        // FILE_SYNCHRONOUS_IO_NONALERT requires SYNCHRONIZE; GENERIC_READ adds EA and READ_CONTROL.
        var handle = DesktopEvidenceOperation.Run(DesktopEvidenceStep.TargetOpen, location,
            () => api.OpenRelative(parent, name, 0x00100081, 0x40 | 0x1000,
                directory ? 3u : 1u, 1, 0x00200000u | 0x20u | (directory ? 1u : 0x40u)), budget);
        try { EnsureAttributes(handle, directory, location, api, budget: budget); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static void EnsureAttributes(SafeFileHandle handle, bool directory, DesktopEvidenceLocation location,
        IWindowsFileApi? api = null, bool root = false, DesktopEvidenceBudget? budget = null) =>
        DesktopEvidenceOperation.Run(root ? DesktopEvidenceStep.RootAttributes : DesktopEvidenceStep.TargetAttributes, location,
            () => EnsureAttributes((api ?? WindowsApi.Instance).Attributes(handle), directory), budget);
    private static void EnsureAttributes(FileAttributes attributes, bool directory)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw UnsafePath();
    }
    private static void EnsureCanonical(SafeFileHandle handle, string expected, DesktopEvidenceLocation location,
        IWindowsFileApi? api = null, bool root = false, DesktopEvidenceBudget? budget = null) =>
        DesktopEvidenceOperation.Run(root ? DesktopEvidenceStep.RootCanonicalPath : DesktopEvidenceStep.TargetCanonicalPath, location, () =>
        {
            var actual = (api ?? WindowsApi.Instance).CanonicalPath(handle);
            if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual.Substring(4);
            if (!string.Equals(actual.TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw UnsafePath();
        }, budget);
    private static DesktopRuntimeValidationException UnsafePath() => DesktopRuntimeDiscovery.Reject(SourceBackendExclusion.DesktopUnsafePath);
    private static void DisposeGuards(List<IDisposable> guards)
    { for (var index = guards.Count - 1; index >= 0; index--) guards[index].Dispose(); }
    public void Dispose() { Stream.Dispose(); DisposeGuards(_guards); }
    internal sealed class DirectoryLease(string path, SafeFileHandle? handle, List<IDisposable> guards,
        DesktopEvidenceLocation location, DesktopEvidenceBudget? budget = null) : IDisposable
    {
        internal bool HasFile(string name)
        {
            budget?.Check(DesktopEvidenceStep.TargetOpen, DesktopEvidenceLocation.RuntimeImage);
            if (handle is null)
            {
                // Non-Windows execution exists only for isolated path fixtures; production
                // Desktop discovery never uses this branch on a non-Windows platform.
                try
                {
                    DesktopEvidenceOperation.Run(DesktopEvidenceStep.TargetAttributes, DesktopEvidenceLocation.RuntimeImage,
                        () => EnsureAttributes(File.GetAttributes(System.IO.Path.Combine(path, name)), directory: false), budget);
                    return true;
                }
                catch (Exception error) when (IsMissingTargetOpen(error, DesktopEvidenceLocation.RuntimeImage, windows: false))
                {
                    budget?.Check(DesktopEvidenceStep.TargetOpen, DesktopEvidenceLocation.RuntimeImage);
                    return false;
                }
            }
            EnsureAttributes(handle, directory: true, location, budget: budget);
            try
            {
                using var file = OpenRelative(handle, name, directory: false, DesktopEvidenceLocation.RuntimeImage, budget: budget);
                EnsureAttributes(handle, directory: true, location, budget: budget);
                EnsureCanonical(file, System.IO.Path.Combine(path, name), DesktopEvidenceLocation.RuntimeImage, budget: budget);
                return true;
            }
            catch (Exception error) when (IsMissingTargetOpen(error, DesktopEvidenceLocation.RuntimeImage, windows: true))
            {
                EnsureAttributes(handle, directory: true, location, budget: budget);
                return false;
            }
        }
        internal DirectoryLease OpenChild(string name)
        {
            var childPath = System.IO.Path.Combine(path, name);
            if (handle is null) return PinDirectory(childPath, budget);
            EnsureAttributes(handle, directory: true, location, budget: budget);
            var child = OpenRelative(handle, name, directory: true, DesktopEvidenceLocation.RuntimeDirectory, budget: budget);
            try
            {
                EnsureAttributes(handle, directory: true, location, budget: budget);
                EnsureCanonical(child, childPath, DesktopEvidenceLocation.RuntimeDirectory, budget: budget);
                return new DirectoryLease(childPath, child, [child], DesktopEvidenceLocation.RuntimeDirectory, budget);
            }
            catch { child.Dispose(); throw; }
        }
        internal string[] ChildDirectoryNames() => DesktopEvidenceOperation.Run(
            DesktopEvidenceStep.DirectoryEnumeration, location, ReadChildDirectoryNames, budget);
        private string[] ReadChildDirectoryNames()
        {
            if (handle is null)
            {
                var paths = new List<string>();
                using var iterator = Directory.EnumerateDirectories(path).GetEnumerator();
                while (DesktopEvidenceOperation.Run(DesktopEvidenceStep.DirectoryEnumeration, location, iterator.MoveNext, budget))
                {
                    paths.Add(System.IO.Path.GetFileName(iterator.Current)!);
                    if (paths.Count > DesktopRuntimeDiscovery.MaximumRuntimeDirectories) throw EvidenceLimit();
                }
                return paths.ToArray();
            }
            EnsureAttributes(handle, directory: true, location, budget: budget);
            const int bufferSize = 65536;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var directories = new List<string>();
                var entries = 0;
                var restart = true;
                while (true)
                {
                    budget?.Check(DesktopEvidenceStep.DirectoryEnumeration, location);
                    // FILE_ID_BOTH_DIR_INFO, Windows Vista+. No names are resolved here.
                    if (!Native.GetFileInformationByHandleEx(handle, restart ? 11 : 10, buffer, bufferSize))
                    {
                        var code = Marshal.GetLastWin32Error();
                        if (code == 18) break; // ERROR_NO_MORE_FILES
                        throw DesktopEvidenceOperation.NativeFailure(DesktopEvidenceStep.DirectoryEnumeration, location, code);
                    }
                    budget?.Check(DesktopEvidenceStep.DirectoryEnumeration, location);
                    restart = false;
                    var offset = 0;
                    while (true)
                    {
                        budget?.Check(DesktopEvidenceStep.DirectoryEnumeration, location);
                        if (offset < 0 || offset > bufferSize - 104) throw UnsafePath();
                        var item = IntPtr.Add(buffer, offset);
                        var next = Marshal.ReadInt32(item, 0);
                        var attributes = (FileAttributes)Marshal.ReadInt32(item, 56);
                        var nameBytes = Marshal.ReadInt32(item, 60);
                        if (nameBytes <= 0 || (nameBytes & 1) != 0 || nameBytes > bufferSize - offset - 104 ||
                            (next != 0 && ((next & 7) != 0 || next < 104 + nameBytes || next > bufferSize - offset))) throw UnsafePath();
                        var name = Marshal.PtrToStringUni(IntPtr.Add(item, 104), nameBytes / 2)!;
                        if (name is not ("." or ".."))
                        {
                            if (++entries > 256) throw EvidenceLimit();
                            if (name.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0) throw UnsafePath();
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                directories.Add(name);
                                if (directories.Count > DesktopRuntimeDiscovery.MaximumRuntimeDirectories) throw EvidenceLimit();
                            }
                        }
                        if (next == 0) break;
                        offset += next;
                    }
                }
                EnsureAttributes(handle, directory: true, location, budget: budget);
                return directories.ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private DesktopRuntimeValidationException EvidenceLimit() => budget?.Limit(DesktopEvidenceLimit.DirectoryCount,
            DesktopEvidenceStep.DirectoryEnumeration, location) ?? new(SourceBackendExclusion.DesktopEvidenceLimit,
                desktop: new(DesktopEvidenceStep.DirectoryEnumeration, location) { Limit = DesktopEvidenceLimit.DirectoryCount });
        public void Dispose() => DisposeGuards(guards);
    }

    private sealed class WindowsApi : IWindowsFileApi
    {
        internal static readonly WindowsApi Instance = new();
        public uint DriveType(string root) => Native.GetDriveType(root);
        public SafeFileHandle OpenRoot(string root, uint access, uint sharing, uint disposition, uint flags)
        {
            var handle = Native.CreateFile(root, access, sharing, IntPtr.Zero, disposition, flags, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var code = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(code);
        }
        public SafeFileHandle OpenRelative(SafeFileHandle root, string relativePath, uint access, uint objectAttributes,
            uint sharing, uint disposition, uint options)
        {
            var text = Marshal.StringToHGlobalUni(relativePath);
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.UnicodeString)));
            try
            {
                var unicode = new Native.UnicodeString
                {
                    Length = checked((ushort)(relativePath.Length * 2)),
                    MaximumLength = checked((ushort)((relativePath.Length + 1) * 2)), Buffer = text
                };
                Marshal.StructureToPtr(unicode, pointer, false);
                var attributes = new Native.ObjectAttributes
                {
                    Length = Marshal.SizeOf(typeof(Native.ObjectAttributes)), RootDirectory = root.DangerousGetHandle(),
                    ObjectName = pointer, Attributes = objectAttributes
                };
                var status = Native.NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0x80,
                    sharing, disposition, options, IntPtr.Zero, 0);
                if (status < 0)
                {
                    handle?.Dispose();
                    throw new NativeOpenException(unchecked((int)Native.RtlNtStatusToDosError(status)), status);
                }
                if (handle is null || handle.IsInvalid) { handle?.Dispose(); throw UnsafePath(); }
                return handle;
            }
            finally { Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(text); }
        }
        public FileAttributes Attributes(SafeFileHandle handle)
        {
            if (!Native.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (FileAttributes)info.Attributes;
        }
        public string CanonicalPath(SafeFileHandle handle)
        {
            var text = new StringBuilder(32768);
            var count = Native.GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
            if (count == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count >= text.Capacity) throw UnsafePath();
            return text.ToString();
        }
    }

    internal sealed class NativeOpenException(int nativeCode, int ntStatus) : Win32Exception(nativeCode)
    {
        internal int NtStatus { get; } = ntStatus;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ObjectAttributes
        {
            public int Length; public IntPtr RootDirectory, ObjectName; public uint Attributes;
            public IntPtr SecurityDescriptor, SecurityQualityOfService;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [DllImport("ntdll.dll")]
        internal static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
            out IoStatusBlock status, IntPtr allocationSize, uint attributesMask, uint sharing, uint disposition,
            uint options, IntPtr extendedAttributes, uint extendedAttributesLength);
        [DllImport("ntdll.dll")]
        internal static extern uint RtlNtStatusToDosError(int status);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint GetDriveType(string rootPath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    }
}
