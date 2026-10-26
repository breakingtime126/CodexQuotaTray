using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CodexQuota.Core;
using Microsoft.Win32.SafeHandles;

internal static class DesktopNativeReadTests
{
    private const string ImagePath = @"C:\Program Files\WindowsApps\OpenAI.Codex_fixture\app\resources\codex.exe";

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("Desktop native contract: one metadata root and full relative target, no ancestor opens", () =>
        {
            var api = new FakeWindowsApi();
            WithHandle(api, false, handle =>
            {
                Check.False(handle.IsClosed);
                Check.Equal(2, api.Opens.Count);
                var root = api.Opens[0]; var image = api.Opens[1];
                Check.Equal(@"C:\", root.Name);
                Check.Equal(0u, root.Access);
                Check.Equal(3u, root.Sharing);
                Check.Equal(3u, root.Disposition); // OPEN_EXISTING
                Check.Equal(0x02200000u, root.Options);
                Check.Equal(ImagePath.Substring(3), image.Name);
                Check.Equal(0x00100081u, image.Access); // DATA | ATTRIBUTES | SYNCHRONIZE
                Check.Equal(0x1040u, image.ObjectAttributes); // DONT_REPARSE | CASE_INSENSITIVE
                Check.Equal(1u, image.Sharing); // deny write/delete
                Check.Equal(1u, image.Disposition); // FILE_OPEN
                Check.Equal(0x00200060u, image.Options); // OPEN_REPARSE_POINT | NONALERT | NON_DIRECTORY
                Check.True(api.Calls.SequenceEqual(new[] { "volume", "root-open", "root-attrs", "root-path", "target-open", "target-attrs", "target-path" }));
            });
            Check.True(api.Handles.All(handle => handle.IsClosed));
        });
        test("Desktop native contract: mock ACL denies old ancestor and generic read masks but admits minimal reads", () =>
        {
            var api = new FakeWindowsApi { EnforceMinimalAcl = true };
            Check.Throws<Win32Exception>(() => api.OpenRoot(@"C:\", 0x80, 3, 3, 0x02200000).Dispose());
            using var parent = new SafeFileHandle(new IntPtr(999), false);
            Check.Throws<Win32Exception>(() => api.OpenRelative(parent, ImagePath.Substring(3), 0x80100000, 0x1040, 1, 1, 0x00200060).Dispose());
            api.Opens.Clear(); api.Calls.Clear();
            WithHandle(api, false, _ => Check.Equal(2, api.Opens.Count));
            Check.True(api.Handles.All(handle => handle.IsClosed));
        });
        test("Desktop native contract: target directory requests only listing attributes and sync", () =>
        {
            var api = new FakeWindowsApi { DirectoryTarget = true };
            WithHandle(api, true, _ =>
            {
                var target = api.Opens[1];
                Check.Equal(0x00100081u, target.Access);
                Check.Equal(3u, target.Sharing);
                Check.Equal(0x00200021u, target.Options);
                Check.Equal(2, api.Opens.Count);
            });
        });
        foreach (var segment in Enumerable.Range(0, ImagePath.Substring(3).Split('\\').Length))
            test("Desktop native contract: reparse in relative component " + segment + " rejects before reading", () =>
            {
                var api = new FakeWindowsApi { ReparseComponent = segment };
                var error = Failure(() => WithHandle(api, false, _ => throw new Exception("Must not read target")));
                Check.Equal(SourceBackendExclusion.DesktopUnsafePath, error.Exclusion);
                Check.Equal(DesktopEvidenceStep.TargetOpen, error.Desktop!.Step);
                Check.Equal(unchecked((int)0xC000050B), error.Desktop.NtStatus);
                Check.Equal(4395, error.Desktop.NativeCode);
                Check.False(api.Calls.Contains("target-attrs"));
                Check.True(api.Handles.All(handle => handle.IsClosed));
            });
        foreach (var (call, step, location) in new[]
        {
            ("volume", DesktopEvidenceStep.VolumeTypeQuery, DesktopEvidenceLocation.LocalVolumeRoot),
            ("root-open", DesktopEvidenceStep.RootOpen, DesktopEvidenceLocation.LocalVolumeRoot),
            ("root-attrs", DesktopEvidenceStep.RootAttributes, DesktopEvidenceLocation.LocalVolumeRoot),
            ("root-path", DesktopEvidenceStep.RootCanonicalPath, DesktopEvidenceLocation.LocalVolumeRoot),
            ("target-open", DesktopEvidenceStep.TargetOpen, DesktopEvidenceLocation.PackageImage),
            ("target-attrs", DesktopEvidenceStep.TargetAttributes, DesktopEvidenceLocation.PackageImage),
            ("target-path", DesktopEvidenceStep.TargetCanonicalPath, DesktopEvidenceLocation.PackageImage)
        })
            test("Desktop native contract: exact failure operation survives " + call, () =>
            {
                var api = new FakeWindowsApi { FailAt = call };
                var error = Failure(() => WithHandle(api, false, _ => throw new Exception("Must not read target")));
                Check.Equal(5, error.Code);
                Check.Equal(step, error.Desktop!.Step);
                Check.Equal(location, error.Desktop.Location);
                Check.Equal(5, error.Desktop.NativeCode);
                Check.Equal(SourceBackendExclusion.Inaccessible, error.Exclusion);
                Check.False(error.Desktop.FormatSafe().Contains("secret", StringComparison.Ordinal));
                Check.True(api.Handles.All(handle => handle.IsClosed));
            });
        test("Desktop native contract: NTSTATUS is retained alongside translated Win32 code", () =>
        {
            var api = new FakeWindowsApi { FailAt = "target-open", NtStatus = unchecked((int)0xC0000022) };
            var error = Failure(() => WithHandle(api, false, _ => { }));
            Check.Equal(unchecked((int)0xC0000022), error.Desktop!.NtStatus);
            Check.Equal(5, error.Desktop.NativeCode);
            Check.Contains("ntStatus=0xC0000022", error.Desktop.FormatSafe());
        });
        foreach (var mutation in new[] { "root-reparse", "root-not-directory", "root-canonical", "target-reparse", "target-directory", "target-canonical", "remote-volume" })
            test("Desktop native contract: retained object validation rejects " + mutation, () =>
            {
                var api = new FakeWindowsApi { Mutation = mutation };
                var error = Failure(() => WithHandle(api, false, _ => throw new Exception("Must not accept mutated target")));
                Check.Equal(SourceBackendExclusion.DesktopUnsafePath, error.Exclusion);
                Check.True(error.Desktop is not null);
                Check.True(api.Handles.All(handle => handle.IsClosed));
            });
        var invalidPathIndex = 0;
        foreach (var path in new[] { @"\\server\share\codex.exe", @"C:relative\codex.exe", @"C:\a\..\codex.exe", @"C:\a\\codex.exe", @"C:\a\codex.exe:stream", @"C:\a\codex.exe.", @"C:/a/codex.exe", "C:\\a\\bad\0file" })
            test("Desktop native contract: malformed path is rejected before native calls " + invalidPathIndex++, () =>
            {
                var api = new FakeWindowsApi();
                var guards = new List<IDisposable>();
                var error = Failure(() => DesktopReadOnlyFileLease.OpenWindowsTarget(path, false, guards, DesktopEvidenceLocation.PackageImage, api).Dispose());
                Check.Equal(DesktopEvidenceStep.LocalPathValidation, error.Desktop!.Step);
                Check.Equal(0, api.Calls.Count);
                Check.Equal(0, guards.Count);
            });
        foreach (var code in new[] { 2, 3 })
            test("Desktop native contract: missing target keeps absence classification " + code, () =>
            {
                var api = new FakeWindowsApi { FailAt = "target-open", ErrorCode = code };
                var error = Failure(() => WithHandle(api, false, _ => { }));
                Check.Equal(SourceBackendExclusion.Missing, error.Exclusion);
                Check.Equal(code, error.Desktop!.NativeCode);
            });
        test("Desktop scanner: only matching target-open absence can become empty or false", () =>
        {
            foreach (var location in new[] { DesktopEvidenceLocation.RuntimeRoot, DesktopEvidenceLocation.RuntimeImage })
                foreach (DesktopEvidenceStep step in Enum.GetValues(typeof(DesktopEvidenceStep)))
                {
                    var error = DesktopEvidenceOperation.NativeFailure(step, location, 2);
                    Check.Equal(step == DesktopEvidenceStep.TargetOpen,
                        DesktopReadOnlyFileLease.IsMissingTargetOpen(error, location, windows: true));
                    Check.False(DesktopReadOnlyFileLease.IsMissingTargetOpen(error,
                        location == DesktopEvidenceLocation.RuntimeRoot ? DesktopEvidenceLocation.RuntimeImage : DesktopEvidenceLocation.RuntimeRoot,
                        windows: true));
                }
            Check.False(DesktopReadOnlyFileLease.IsMissingTargetOpen(new Win32Exception(2), DesktopEvidenceLocation.RuntimeRoot, windows: true));
            Check.False(DesktopReadOnlyFileLease.IsMissingTargetOpen(new FileNotFoundException(), DesktopEvidenceLocation.RuntimeRoot, windows: true));
            Check.True(DesktopReadOnlyFileLease.IsMissingTargetOpen(new FileNotFoundException(), DesktopEvidenceLocation.RuntimeRoot, windows: false));
            Check.True(DesktopReadOnlyFileLease.IsMissingTargetOpen(new DirectoryNotFoundException(), DesktopEvidenceLocation.RuntimeRoot, windows: false));
            var fixtureMetadata = new DesktopRuntimeValidationException(SourceBackendExclusion.Missing,
                desktop: new(DesktopEvidenceStep.TargetAttributes, DesktopEvidenceLocation.RuntimeRoot));
            Check.True(DesktopReadOnlyFileLease.IsMissingTargetOpen(fixtureMetadata, DesktopEvidenceLocation.RuntimeRoot, windows: false));
            Check.False(DesktopReadOnlyFileLease.IsMissingTargetOpen(fixtureMetadata, DesktopEvidenceLocation.RuntimeRoot, windows: true));
        });
        foreach (var readCall in new[] { false, true })
        {
            test("Desktop package API: family sizing/read failure labelled exactly " + readCall, () =>
            {
                var calls = 0;
                int Query(ref uint count, IntPtr names, ref uint length, IntPtr buffer)
                { calls++; count = 1; length = 64; return readCall && calls == 1 ? 122 : 5; }
                var error = Failure(() => CliLocator.GetDesktopPackageNames(Query));
                Check.Equal(readCall ? 2 : 1, calls);
                Check.Equal(DesktopEvidenceStep.PackageFamilyQuery, error.Desktop!.Step);
                Check.Equal(5, error.Desktop.NativeCode);
            });
            test("Desktop package API: path sizing/read failure labelled exactly " + readCall, () =>
            {
                var calls = 0;
                int Query(string fullName, ref uint length, StringBuilder? path)
                { calls++; length = 64; return readCall && calls == 1 ? 122 : 5; }
                var error = Failure(() => CliLocator.GetRegisteredPackagePath("private package", Query));
                Check.Equal(readCall ? 2 : 1, calls);
                Check.Equal(DesktopEvidenceStep.PackagePathQuery, error.Desktop!.Step);
                Check.Equal(5, error.Desktop.NativeCode);
            });
        }
        test("Desktop package API: current registration stays lazy when older package path would fail", () =>
        {
            var reads = 0;
            var package = new DesktopPackageRegistration("OpenAI.Codex_26.928.2636.0_x64__2p2nqsd0c76g0", "fixture");
            IEnumerable<DesktopPackageRegistration> Registered()
            {
                reads++; yield return package;
                reads++; throw new Win32Exception(5, "older registration path must not be read");
            }
            var evidence = new WindowsDesktopRuntimeEvidence(registeredPackages: Registered);
            var registrations = evidence.RegisteredPackages();
            Check.Equal(0, reads);
            Check.Equal(package, registrations.Take(64).FirstOrDefault());
            Check.Equal(1, reads);
        });
        test("Desktop package API: origin failure and manifest parse failure stay distinct", () =>
        {
            using var files = new TempDirectory();
            var package = new DesktopPackageRegistration("OpenAI.Codex_26.928.2636.0_x64__2p2nqsd0c76g0", files.Path);
            var origin = Failure(() => new WindowsDesktopRuntimeEvidence(_ => throw new Win32Exception(5, "secret origin"))
                .VerifyPackageIdentity(package));
            Check.Equal(DesktopEvidenceStep.PackageOriginQuery, origin.Desktop!.Step);
            Check.Equal(DesktopEvidenceLocation.PackageRegistration, origin.Desktop.Location);
            Check.Equal(5, origin.Desktop.NativeCode);
            File.WriteAllText(Path.Combine(files.Path, "AppxManifest.xml"), "<invalid");
            var manifest = Failure(() => new WindowsDesktopRuntimeEvidence(_ => 3).VerifyPackageIdentity(package));
            Check.Equal(DesktopEvidenceStep.ManifestRead, manifest.Desktop!.Step);
            Check.Equal(DesktopEvidenceLocation.PackageManifest, manifest.Desktop.Location);
            Check.Null(manifest.Desktop.NativeCode);
        });
        test("Desktop operation: nested API diagnostic is never overwritten by enclosing stages", () =>
        {
            var original = DesktopEvidenceOperation.NativeFailure(DesktopEvidenceStep.TargetOpen, DesktopEvidenceLocation.PackageManifest, 5, unchecked((int)0xC0000022));
            var error = Failure(() => DesktopEvidenceOperation.Run(DesktopEvidenceStep.ManifestRead, DesktopEvidenceLocation.PackageManifest, () => throw original));
            Check.True(ReferenceEquals(original, error));
            Check.True(error.EvidenceReadFailure);
            var mismatch = new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopSignatureUnverified,
                unchecked((int)0x800B0100));
            var labelledMismatch = Failure(() => DesktopEvidenceOperation.Run(DesktopEvidenceStep.SignatureVerify,
                DesktopEvidenceLocation.RuntimeImage, () => throw mismatch));
            Check.False(labelledMismatch.EvidenceReadFailure);
            Check.Equal(mismatch.Code, labelledMismatch.Code);
        });
        asyncTest("Desktop diagnostics: discovery and prelaunch preserve safe operation and both codes", async () =>
        {
            var failure = DesktopEvidenceOperation.NativeFailure(DesktopEvidenceStep.TargetOpen, DesktopEvidenceLocation.PackageManifest, 5, unchecked((int)0xC0000022));
            var traces = new List<SourceBackendDiagnostic>(); var diagnostics = new List<CliDiagnostic>();
            var discovery = DesktopRuntimeDiscovery.Discover(new FailingEvidence(failure));
            Check.Equal(0, discovery.Examined);
            Check.Equal(failure.Desktop, discovery.Desktop);
            var result = await new CodexQuotaClient(QuotaDataSource.Desktop, () => discovery, diagnostics.Add, traces.Add).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            Check.Equal(failure.Desktop, diagnostics.Single().Desktop);
            Check.Equal(failure.Desktop, traces.Single().Desktop);
            Check.Contains("desktopStep=TargetOpen", traces.Single().FormatSafe());
            Check.Contains("nativeCode=5 ntStatus=0xC0000022", diagnostics.Single().ToSafeLogCode());
            Check.False(traces.Single().FormatSafe().Contains(ImagePath, StringComparison.Ordinal));
            traces.Clear(); diagnostics.Clear();
            var candidate = new CliCandidate(CliSource.Desktop, new ProcessStartInfo("must-never-launch"))
            { AcquireLaunchLease = () => throw failure };
            result = await new CodexQuotaClient(QuotaDataSource.Desktop, () => new[] { candidate }, diagnostics.Add, traces.Add).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            Check.Equal(failure.Desktop, diagnostics.Single().Desktop);
            Check.Equal(failure.Desktop, traces.Single(item => item.Stage == SourceBackendStage.Start).Desktop);
            Check.True(traces.All(item => item.Exit == SourceBackendExit.NotStarted));
        });
        test("Desktop diagnostic formatting: undefined labels cannot inject text", () =>
        {
            var diagnostic = new DesktopEvidenceDiagnostic((DesktopEvidenceStep)999, (DesktopEvidenceLocation)999, 5);
            Check.Contains("desktopStep=Unknown desktopLocation=Unknown", diagnostic.FormatSafe());
        });
    }

    private static void WithHandle(FakeWindowsApi api, bool directory, Action<SafeFileHandle> inspect)
    {
        var guards = new List<IDisposable>();
        try
        {
            using var handle = DesktopReadOnlyFileLease.OpenWindowsTarget(ImagePath, directory, guards, DesktopEvidenceLocation.PackageImage, api);
            inspect(handle);
        }
        finally { foreach (var guard in guards.AsEnumerable().Reverse()) guard.Dispose(); }
    }
    private static DesktopRuntimeValidationException Failure(Action action)
    {
        try { action(); }
        catch (DesktopRuntimeValidationException error) { return error; }
        throw new Exception("Expected a safe Desktop validation failure.");
    }
    private sealed record OpenRequest(string Name, uint Access, uint Sharing, uint Disposition, uint Options, uint ObjectAttributes = 0);
    private sealed class FakeWindowsApi : DesktopReadOnlyFileLease.IWindowsFileApi
    {
        internal List<OpenRequest> Opens { get; } = new();
        internal List<string> Calls { get; } = new();
        internal List<SafeFileHandle> Handles { get; } = new();
        internal bool DirectoryTarget { get; init; }
        internal bool EnforceMinimalAcl { get; init; }
        internal int ReparseComponent { get; init; } = -1;
        internal string? FailAt { get; init; }
        internal string? Mutation { get; init; }
        internal int ErrorCode { get; init; } = 5;
        internal int? NtStatus { get; init; }
        public uint DriveType(string root) { Call("volume"); return Mutation == "remote-volume" ? 4u : 3u; }
        public SafeFileHandle OpenRoot(string root, uint access, uint sharing, uint disposition, uint flags)
        {
            Call("root-open");
            if (EnforceMinimalAcl && access != 0) throw new Win32Exception(5, "mock ACL rejects ancestor rights");
            Opens.Add(new(root, access, sharing, disposition, flags));
            return Handle();
        }
        public SafeFileHandle OpenRelative(SafeFileHandle root, string relativePath, uint access, uint objectAttributes,
            uint sharing, uint disposition, uint options)
        {
            Call("target-open");
            Check.False(root.IsClosed);
            if (EnforceMinimalAcl && (access != 0x00100081 || relativePath != ImagePath.Substring(3)))
                throw new Win32Exception(5, "mock ACL rejects extra generic/ancestor rights");
            Opens.Add(new(relativePath, access, sharing, disposition, options, objectAttributes));
            if (ReparseComponent >= 0 && ReparseComponent < relativePath.Split('\\').Length && (objectAttributes & 0x1000) != 0)
                throw new DesktopReadOnlyFileLease.NativeOpenException(4395, unchecked((int)0xC000050B));
            return Handle();
        }
        public FileAttributes Attributes(SafeFileHandle handle)
        {
            var root = handle == Handles[0]; Call(root ? "root-attrs" : "target-attrs");
            if (Mutation == (root ? "root-reparse" : "target-reparse")) return FileAttributes.ReparsePoint;
            if (root) return Mutation == "root-not-directory" ? FileAttributes.Normal : FileAttributes.Directory;
            return DirectoryTarget || Mutation == "target-directory" ? FileAttributes.Directory : FileAttributes.Normal;
        }
        public string CanonicalPath(SafeFileHandle handle)
        {
            var root = handle == Handles[0]; Call(root ? "root-path" : "target-path");
            if (Mutation == (root ? "root-canonical" : "target-canonical")) return @"C:\private-other-object";
            return @"\\?\" + (root ? @"C:\" : ImagePath);
        }
        private SafeFileHandle Handle()
        {
            var handle = new SafeFileHandle(new IntPtr(1000 + Handles.Count), ownsHandle: false);
            Handles.Add(handle); return handle;
        }
        private void Call(string name)
        {
            Calls.Add(name);
            if (FailAt != name) return;
            if (NtStatus is { } status) throw new DesktopReadOnlyFileLease.NativeOpenException(ErrorCode, status);
            throw new Win32Exception(ErrorCode, "secret path/account/proxy/token must not escape");
        }
    }
    private sealed class FailingEvidence(Exception error) : IDesktopRuntimeEvidence
    {
        public IEnumerable<DesktopPackageRegistration> RegisteredPackages() => throw error;
        public string LocalApplicationData => throw new Exception("Must not scan");
        public bool VerifyPackageIdentity(DesktopPackageRegistration package) => throw new Exception("Must not verify");
        public IEnumerable<string> ExistingRuntimePaths(string localApplicationData) => throw new Exception("Must not scan");
        public IDesktopImageLease OpenImage(string path, DesktopImageIdentity? expected = null) => throw new Exception("Must not open");
    }
}
