using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using CodexQuota.Core;

internal static class DesktopRuntimeDiscoveryTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("Desktop runtime: existing direct image is selected only with current package evidence", () =>
        {
            using var evidence = new FakeEvidence();
            var result = DesktopRuntimeDiscovery.Discover(evidence);
            Check.Equal(evidence.Runtime, result.Candidate!.StartInfo.FileName);
            Check.Equal(CliSource.Desktop, result.Candidate.Source);
            Check.Equal(SourceBackendLocation.DesktopExistingRuntime, result.Candidate.Location);
            Check.Equal(SourceBackendIdentityEvidence.RegisteredPackageBytesAndSignature, result.Candidate.IdentityEvidence);
            Check.True(result.Candidate.StartInfo.ArgumentList.SequenceEqual(new[] { "app-server" }));
            Check.False(result.Candidate.StartInfo.UseShellExecute);
            Check.Equal(0, evidence.ActiveLeases);
            Check.Equal(2, result.Examined);
            Check.Equal(1, evidence.PackageReads);
            using (result.Candidate.AcquireLaunchLease!())
            {
                Check.Equal(2, evidence.ActiveLeases);
                Check.Equal(3, evidence.PackageReads);
            }
            Check.Equal(0, evidence.ActiveLeases);
        });
        test("Desktop runtime: absent current registration never scans a runtime directory", () =>
        {
            using var evidence = new FakeEvidence { Packages = Array.Empty<DesktopPackageRegistration>() };
            var result = DesktopRuntimeDiscovery.Discover(evidence);
            Check.Null(result.Candidate);
            Check.Equal(0, evidence.RuntimeScans);
            Check.Equal(0, evidence.OpenCount);
        });
        test("Desktop runtime: no existing runtime retains one verified package candidate", () =>
        {
            using var evidence = new FakeEvidence { RuntimePaths = Array.Empty<string>() };
            var result = DesktopRuntimeDiscovery.Discover(evidence);
            Check.Equal(evidence.Resource, result.Candidate!.StartInfo.FileName);
            Check.Equal(SourceBackendLocation.DesktopPackageResource, result.Candidate.Location);
            Check.Equal(SourceBackendIdentityEvidence.RegisteredDesktopPackage, result.Candidate.IdentityEvidence);
            using (result.Candidate.AcquireLaunchLease!()) Check.Equal(1, evidence.ActiveLeases);
            Check.Equal(0, evidence.ActiveLeases);
        });
        test("Desktop runtime: missing PE version in both byte-identical images is supported", () =>
        {
            using var evidence = new FakeEvidence();
            evidence.Anchor = evidence.Anchor with { FileVersion = null };
            evidence.RuntimeImage = evidence.Anchor;
            Check.True(DesktopRuntimeDiscovery.Discover(evidence).Candidate is not null);
        });
        test("Desktop runtime: CLI PE and MSIX versions are independent version domains", () =>
        {
            using var evidence = new FakeEvidence();
            Check.Equal(new Version(0, 159, 2, 0), evidence.Anchor.FileVersion);
            Check.Contains("26.928.2636.0", evidence.Packages.Single().FullName);
            Check.True(DesktopRuntimeDiscovery.Discover(evidence).Candidate is not null);
        });
        foreach (var (name, transform, exclusion) in new (string, Func<DesktopImageIdentity, DesktopImageIdentity>, SourceBackendExclusion)[]
        {
            ("hash mismatch", identity => identity with { Sha256 = new string('b', 64) }, SourceBackendExclusion.DesktopRuntimeUnverified),
            ("size mismatch", identity => identity with { Length = identity.Length + 1 }, SourceBackendExclusion.DesktopRuntimeUnverified),
            ("untrusted signature", identity => identity with { TrustedOpenAiSignature = false }, SourceBackendExclusion.DesktopSignatureUnverified),
            ("PE mismatch", identity => identity with { FileVersion = new Version(9, 9, 9, 0) }, SourceBackendExclusion.DesktopVersionMismatch),
            ("one absent PE", identity => identity with { FileVersion = null }, SourceBackendExclusion.DesktopVersionMismatch),
            ("oversized image", identity => identity with { Length = DesktopRuntimeDiscovery.MaximumImageBytes + 1 }, SourceBackendExclusion.DesktopRuntimeUnverified),
            ("malformed hash", identity => identity with { Sha256 = "not-a-hash" }, SourceBackendExclusion.DesktopRuntimeUnverified)
        })
            test("Desktop runtime: " + name + " is rejected without package launch fallback", () =>
            {
                using var evidence = new FakeEvidence();
                evidence.RuntimeImage = transform(evidence.RuntimeImage);
                var result = DesktopRuntimeDiscovery.Discover(evidence);
                Check.Null(result.Candidate);
                Check.True((result.Exclusions & exclusion) != 0);
                Check.Equal(0, evidence.ActiveLeases);
            });
        test("Desktop runtime: an untrusted package anchor prevents runtime reads", () =>
        {
            using var evidence = new FakeEvidence();
            evidence.Anchor = evidence.Anchor with { TrustedOpenAiSignature = false };
            var result = DesktopRuntimeDiscovery.Discover(evidence);
            Check.Null(result.Candidate);
            Check.Equal(SourceBackendExclusion.DesktopSignatureUnverified, result.Exclusions);
            Check.Equal(0, evidence.RuntimeScans);
            Check.Equal(0, evidence.ActiveLeases);
        });
        test("Desktop runtime: stale sibling may be excluded before a verified current sibling", () =>
        {
            using var evidence = new FakeEvidence();
            var current = evidence.AddRuntime("0123456789abcdef");
            evidence.RuntimePaths = new[] { evidence.Runtime, current };
            evidence.RuntimeImage = evidence.Anchor with { Sha256 = new string('b', 64) };
            evidence.Images[current] = evidence.Anchor;
            var result = DesktopRuntimeDiscovery.Discover(evidence);
            Check.Equal(current, result.Candidate!.StartInfo.FileName);
            Check.Equal(1, result.Excluded);
            Check.Equal(SourceBackendExclusion.DesktopRuntimeUnverified, result.Exclusions);
        });
        foreach (var failure in new Exception[]
        {
            new UnauthorizedAccessException("private path"), new Win32Exception(5, "private file"),
            new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopUnsafePath),
            new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopEvidenceLimit)
        })
            test("Desktop runtime: " + failure.GetType().Name + " ends discovery rather than pretending absent", () =>
            {
                using var evidence = new FakeEvidence { RuntimeScanFailure = failure };
                var result = DesktopRuntimeDiscovery.Discover(evidence);
                Check.Null(result.Candidate);
                Check.True(result.Exclusions != SourceBackendExclusion.None);
                Check.Equal(0, evidence.ActiveLeases);
            });
        test("Desktop runtime: cache-only trust HRESULT is retained without certificate text", () =>
        {
            const int trustRevocationFailure = unchecked((int)0x800B010E);
            using var evidence = new FakeEvidence
            { RuntimeOpenFailure = new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopSignatureUnverified, trustRevocationFailure) };
            var result = DesktopRuntimeDiscovery.Discover(evidence);
            Check.Null(result.Candidate);
            Check.Equal(trustRevocationFailure, result.Code);
            Check.Equal(SourceBackendExclusion.DesktopSignatureUnverified, result.Exclusions);
        });
        test("Desktop runtime: registration manifest mismatch fails before any image read", () =>
        {
            using var evidence = new FakeEvidence { ManifestValid = false };
            var result = DesktopRuntimeDiscovery.Discover(evidence);
            Check.Null(result.Candidate);
            Check.Equal(SourceBackendExclusion.DesktopPackageUnverified, result.Exclusions);
            Check.Equal(0, evidence.OpenCount);
        });
        test("Desktop runtime: last rejected candidate keeps its own code and operation atomically", () =>
        {
            foreach (var lastCode in new int?[] { null, 13 })
            {
                using var evidence = new FakeEvidence();
                var sibling = evidence.AddRuntime("0123456789abcdef");
                evidence.RuntimePaths = [evidence.Runtime, sibling];
                var first = new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopSignatureUnverified,
                    unchecked((int)0x800B0100), new(DesktopEvidenceStep.SignatureVerify,
                        DesktopEvidenceLocation.RuntimeImage, unchecked((int)0x800B0100)));
                var last = new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopRuntimeUnverified, lastCode,
                    new(DesktopEvidenceStep.ImageHash, DesktopEvidenceLocation.RuntimeImage, lastCode));
                evidence.RuntimeFailures[evidence.Runtime] = first;
                evidence.RuntimeFailures[sibling] = last;
                var result = DesktopRuntimeDiscovery.Discover(evidence);
                Check.Null(result.Candidate);
                Check.Equal(2, result.Excluded);
                Check.Equal(lastCode, result.Code);
                Check.Equal(last.Desktop, result.Desktop);
                Check.Equal(lastCode, result.Desktop!.NativeCode);
                Check.Null(result.Desktop.NtStatus);
                Check.True((result.Exclusions & SourceBackendExclusion.DesktopSignatureUnverified) != 0);
                Check.True((result.Exclusions & SourceBackendExclusion.DesktopRuntimeUnverified) != 0);
            }
        });
        foreach (var reason in new[] { SourceBackendExclusion.Inaccessible, SourceBackendExclusion.Missing, SourceBackendExclusion.ProviderUnavailable })
            test("Desktop runtime: wrapped read failure stops before any sibling " + reason, () =>
            {
                using var evidence = new FakeEvidence();
                var sibling = evidence.AddRuntime("0123456789abcdef");
                evidence.RuntimePaths = [evidence.Runtime, sibling];
                var code = reason == SourceBackendExclusion.Missing ? 2 : 5;
                var error = new DesktopRuntimeValidationException(reason, code,
                    new(DesktopEvidenceStep.TargetOpen, DesktopEvidenceLocation.RuntimeImage, code));
                evidence.RuntimeFailures[evidence.Runtime] = error;
                var opened = new List<string>();
                evidence.OnImageOpen = opened.Add;
                var result = DesktopRuntimeDiscovery.Discover(evidence);
                Check.Null(result.Candidate);
                Check.Equal(code, result.Code);
                Check.Equal(error.Desktop, result.Desktop);
                Check.Equal(reason, result.Exclusions);
                Check.True(opened.SequenceEqual(new[] { evidence.Resource, evidence.Runtime }));
                Check.False(opened.Contains(sibling));
                Check.Equal(0, evidence.ActiveLeases);
            });
        foreach (var code in new[] { 5, unchecked((int)0x80070005) })
            test("Desktop runtime: explicit signature access denial stops before a sibling " + code, () =>
            {
                using var evidence = new FakeEvidence();
                var sibling = evidence.AddRuntime("0123456789abcdef");
                evidence.RuntimePaths = [evidence.Runtime, sibling];
                var error = new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopSignatureUnverified,
                    code, new(DesktopEvidenceStep.SignatureVerify, DesktopEvidenceLocation.RuntimeImage, code));
                evidence.RuntimeFailures[evidence.Runtime] = error;
                var opened = new List<string>(); evidence.OnImageOpen = opened.Add;
                var result = DesktopRuntimeDiscovery.Discover(evidence);
                Check.Null(result.Candidate);
                Check.Equal(code, result.Code);
                Check.Equal(error.Desktop, result.Desktop);
                Check.True(opened.SequenceEqual(new[] { evidence.Resource, evidence.Runtime }));
                Check.False(opened.Contains(sibling));
            });
        foreach (var (name, rawError) in new (string, Exception)[]
        {
            ("cryptography provider", new CryptographicException("private certificate data")),
            ("native denial", new Win32Exception(5, "private native path")),
            ("read IO", new IOException("private IO path"))
        })
            test("Desktop runtime: operational exception provenance stops siblings for " + name, () =>
            {
                using var evidence = new FakeEvidence();
                var sibling = evidence.AddRuntime("0123456789abcdef");
                evidence.RuntimePaths = [evidence.Runtime, sibling];
                DesktopRuntimeValidationException? wrapped = null;
                try
                {
                    DesktopEvidenceOperation.Run(DesktopEvidenceStep.SignatureVerify,
                        DesktopEvidenceLocation.RuntimeImage, () => throw rawError);
                }
                catch (DesktopRuntimeValidationException error) { wrapped = error; }
                Check.True(wrapped is { EvidenceReadFailure: true });
                evidence.RuntimeFailures[evidence.Runtime] = wrapped!;
                var opened = new List<string>(); evidence.OnImageOpen = opened.Add;
                var result = DesktopRuntimeDiscovery.Discover(evidence);
                Check.Null(result.Candidate);
                Check.Equal(wrapped!.Code, result.Code);
                Check.Equal(wrapped.Desktop, result.Desktop);
                Check.True(opened.SequenceEqual(new[] { evidence.Resource, evidence.Runtime }));
                Check.False(opened.Contains(sibling));
                Check.False(result.Desktop!.FormatSafe().Contains("private", StringComparison.Ordinal));
                Check.Equal(0, evidence.ActiveLeases);
                evidence.RuntimeFailures.Clear();
                var candidate = DesktopRuntimeDiscovery.Discover(evidence).Candidate!;
                evidence.RuntimeFailures[evidence.Runtime] = wrapped;
                DesktopRuntimeValidationException? launchError = null;
                try { candidate.AcquireLaunchLease!().Dispose(); }
                catch (DesktopRuntimeValidationException error) { launchError = error; }
                Check.True(launchError is { EvidenceReadFailure: true });
                Check.Equal(wrapped.Desktop, launchError!.Desktop);
                Check.Equal(0, evidence.ActiveLeases);
            });
        test("Desktop runtime: missing package resource cannot be replaced by a user runtime", () =>
        {
            using var evidence = new FakeEvidence { AnchorFailure = new FileNotFoundException("private resource") };
            var result = DesktopRuntimeDiscovery.Discover(evidence);
            Check.Null(result.Candidate);
            Check.True(result.Code is not null);
            Check.Equal(0, evidence.RuntimeScans);
        });
        foreach (var mutation in new[] { "registration", "manifest", "anchor hash", "runtime hash", "signature", "local root", "path denial" })
            test("Desktop runtime: launch revalidation stops after changed " + mutation, () =>
            {
                using var evidence = new FakeEvidence();
                var candidate = DesktopRuntimeDiscovery.Discover(evidence).Candidate!;
                switch (mutation)
                {
                    case "registration": evidence.Packages = [evidence.Packages[0] with { FullName = "OpenAI.Codex_26.928.2637.0_x64__2p2nqsd0c76g0" }]; break;
                    case "manifest": evidence.ManifestValid = false; break;
                    case "anchor hash": evidence.Anchor = evidence.Anchor with { Sha256 = new string('b', 64) }; break;
                    case "runtime hash": evidence.RuntimeImage = evidence.Anchor with { Sha256 = new string('b', 64) }; break;
                    case "signature": evidence.RuntimeImage = evidence.Anchor with { TrustedOpenAiSignature = false }; break;
                    case "local root": evidence.LocalApplicationData = Path.Combine(evidence.LocalApplicationData, "moved"); break;
                    case "path denial": evidence.RuntimeOpenFailure = new Win32Exception(5, "private path"); break;
                }
                Check.Throws<DesktopRuntimeValidationException>(() => candidate.AcquireLaunchLease!().Dispose());
                Check.Equal(0, evidence.ActiveLeases);
            });
        test("Desktop runtime: registration change during expensive verification closes both leases", () =>
        {
            using var evidence = new FakeEvidence();
            var candidate = DesktopRuntimeDiscovery.Discover(evidence).Candidate!;
            evidence.OnImageOpen = path =>
            {
                if (path == evidence.Runtime)
                    evidence.Packages = [evidence.Packages[0] with { FullName = "OpenAI.Codex_26.928.2637.0_x64__2p2nqsd0c76g0" }];
            };
            Check.Throws<DesktopRuntimeValidationException>(() => candidate.AcquireLaunchLease!().Dispose());
            Check.Equal(0, evidence.ActiveLeases);
        });
        test("Desktop runtime: exact manifest identity rejects lookalike publishers and versions", () =>
        {
            using var evidence = new FakeEvidence();
            var package = evidence.Packages.Single();
            Check.True(DesktopRuntimeDiscovery.ManifestMatches(package, "OpenAI.Codex", DesktopRuntimeDiscovery.StorePublisher, "26.928.2636.0", "x64"));
            foreach (var fullName in new[]
            {
                "Other.Codex_26.928.2636.0_x64__2p2nqsd0c76g0", "OpenAI.Codex_26.928.2636.0_x64__lookalike",
                "OpenAI.Codex_26.928.2636.0_arm64__2p2nqsd0c76g0", "OpenAI.Codex_26.928_x64__2p2nqsd0c76g0"
            }) Check.False(DesktopRuntimeDiscovery.ValidRegistration(package with { FullName = fullName }));
            Check.False(DesktopRuntimeDiscovery.ManifestMatches(package, "OpenAI.Codex", "CN=OpenAI", "26.928.2636.0", "x64"));
            Check.False(DesktopRuntimeDiscovery.ManifestMatches(package, "OpenAI.Codex", DesktopRuntimeDiscovery.StorePublisher, "26.928.2637.0", "x64"));
        });
        test("Desktop runtime: path allowlist is only direct or one lower-case sixteen-hex directory", () =>
        {
            using var evidence = new FakeEvidence();
            Check.True(DesktopRuntimeDiscovery.IsRuntimePath(evidence.LocalApplicationData, evidence.Runtime));
            Check.True(DesktopRuntimeDiscovery.IsRuntimePath(evidence.LocalApplicationData, evidence.AddRuntime("0123456789abcdef")));
            foreach (var child in new[] { "other", "0123456789ABCDEF", "0123456789abcde", "0123456789abcdef0", "../escape", "0123456789abcdef/nested" })
                Check.False(DesktopRuntimeDiscovery.IsRuntimePath(evidence.LocalApplicationData,
                    Path.Combine(evidence.LocalApplicationData, "OpenAI", "Codex", "bin", child, "codex.exe")));
            foreach (var path in new[] { "relative", "\\\\server\\share\\codex.exe", "//server/share/codex.exe", "bad\0path", evidence.Runtime + ".", evidence.Runtime + " " })
                Check.False(DesktopRuntimeDiscovery.IsCanonicalLocalPath(path));
        });
        test("Desktop runtime: production scanner ignores arbitrary directories and never recurses", () =>
        {
            using var evidence = new FakeEvidence();
            var valid = evidence.AddRuntime("0123456789abcdef");
            _ = evidence.AddRuntime("unrelated-cache");
            _ = evidence.AddRuntime("0123456789ABCDEF");
            _ = evidence.AddRuntime("0123456789abcdef/nested");
            var paths = new WindowsDesktopRuntimeEvidence().ExistingRuntimePaths(evidence.LocalApplicationData).ToArray();
            Check.True(paths.SequenceEqual(new[] { evidence.Runtime, valid }));
        });
        test("Desktop runtime: production scanner returns empty only for a genuinely absent root", () =>
        {
            using var files = new TempDirectory();
            Check.Equal(0, new WindowsDesktopRuntimeEvidence().ExistingRuntimePaths(files.Path).Count());
        });
        test("Desktop runtime: production scanner enforces its immediate-directory bound", () =>
        {
            using var evidence = new FakeEvidence();
            for (var index = 0; index <= DesktopRuntimeDiscovery.MaximumRuntimeDirectories; index++) evidence.AddRuntime(index.ToString("x16"));
            Check.Throws<DesktopRuntimeValidationException>(() =>
                new WindowsDesktopRuntimeEvidence().ExistingRuntimePaths(evidence.LocalApplicationData).ToArray());
        });
        test("Desktop runtime: real isolated manifest requires its namespace root and one exact identity", () =>
        {
            using var evidence = new FakeEvidence();
            var path = Path.Combine(evidence.Packages[0].Root, "AppxManifest.xml");
            var identity = "<Identity Name=\"OpenAI.Codex\" Publisher=\"" + DesktopRuntimeDiscovery.StorePublisher + "\" Version=\"26.928.2636.0\" ProcessorArchitecture=\"x64\" />";
            const string ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            File.WriteAllText(path, "<Package xmlns=\"" + ns + "\">" + identity + "</Package>");
            var production = new WindowsDesktopRuntimeEvidence(_ => 3);
            foreach (var origin in new[] { 0, 1, 2, 4, 5, 6 })
                Check.False(new WindowsDesktopRuntimeEvidence(_ => origin).VerifyPackageIdentity(evidence.Packages[0]));
            Check.True(production.VerifyPackageIdentity(evidence.Packages[0]));
            File.WriteAllText(path, "<Other xmlns=\"" + ns + "\">" + identity + "</Other>");
            Check.False(production.VerifyPackageIdentity(evidence.Packages[0]));
            File.WriteAllText(path, "<Package xmlns=\"" + ns + "\">" + identity + identity + "</Package>");
            Check.False(production.VerifyPackageIdentity(evidence.Packages[0]));
            File.WriteAllText(path, "<!DOCTYPE Package [<!ENTITY x SYSTEM 'file:///must-not-read'>]><Package>&x;</Package>");
            Check.Throws<DesktopRuntimeValidationException>(() => production.VerifyPackageIdentity(evidence.Packages[0]));
        });
        if (!OperatingSystem.IsWindows())
        {
            test("Desktop runtime: symbolic runtime and symbolic ancestor are both rejected before image reads", () =>
            {
                using var files = new TempDirectory();
                var original = files.Touch("official.exe");
                var link = Path.Combine(files.Path, "symlink.exe");
                File.CreateSymbolicLink(link, original);
                Check.Throws<DesktopRuntimeValidationException>(() => DesktopReadOnlyFileLease.Open(link).Dispose());
                var actualDirectory = Path.Combine(files.Path, "actual"); Directory.CreateDirectory(actualDirectory);
                File.WriteAllText(Path.Combine(actualDirectory, "codex.exe"), "fixture");
                var directoryLink = Path.Combine(files.Path, "junction");
                Directory.CreateSymbolicLink(directoryLink, actualDirectory);
                Check.Throws<DesktopRuntimeValidationException>(() => DesktopReadOnlyFileLease.Open(Path.Combine(directoryLink, "codex.exe")).Dispose());
            });
            test("Desktop runtime: reparse directory in supported layout is unavailable, never an empty scan", () =>
            {
                using var evidence = new FakeEvidence();
                var target = Path.Combine(evidence.LocalApplicationData, "unrelated"); Directory.CreateDirectory(target);
                Directory.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName(evidence.Runtime)!, "0123456789abcdef"), target);
                Check.Throws<DesktopRuntimeValidationException>(() =>
                    new WindowsDesktopRuntimeEvidence().ExistingRuntimePaths(evidence.LocalApplicationData).ToArray());
            });
        }
        asyncTest("Desktop runtime: verification failure is unavailable and contains no private evidence", async () =>
        {
            using var evidence = new FakeEvidence();
            evidence.RuntimeImage = evidence.Anchor with { Sha256 = new string('b', 64) };
            var traces = new List<SourceBackendDiagnostic>();
            var diagnostics = new List<CliDiagnostic>();
            var result = await new CodexQuotaClient(QuotaDataSource.Desktop, () => DesktopRuntimeDiscovery.Discover(evidence),
                diagnostics.Add, traces.Add).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            Check.Equal(CliErrorKind.DesktopRuntimeUnverified, diagnostics.Single().ErrorKind);
            Check.Equal(SourceBackendStage.Discover, traces.Single().Stage);
            Check.False(traces.Single().FormatSafe().Contains(evidence.LocalApplicationData, StringComparison.Ordinal));
            Check.False(traces.Single().FormatSafe().Contains(evidence.Anchor.Sha256, StringComparison.Ordinal));
        });
        asyncTest("Desktop runtime: failed prelaunch verification is traced and no process is started", async () =>
        {
            var diagnostics = new List<CliDiagnostic>();
            var traces = new List<SourceBackendDiagnostic>();
            var candidate = new CliCandidate(CliSource.Desktop, new ProcessStartInfo("must-never-launch"))
            {
                Location = SourceBackendLocation.DesktopExistingRuntime,
                IdentityEvidence = SourceBackendIdentityEvidence.RegisteredPackageBytesAndSignature,
                AcquireLaunchLease = () => throw new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopFileChanged, 5)
            };
            var result = await new CodexQuotaClient(QuotaDataSource.Desktop, () => new[] { candidate }, diagnostics.Add, traces.Add)
                .ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            Check.Equal(CliStartOperation.ValidateDesktopRuntime, diagnostics.Single().StartOperation);
            Check.Equal(5, diagnostics.Single().Code);
            var start = traces.Single(item => item.Stage == SourceBackendStage.Start);
            Check.Equal(SourceBackendExit.NotStarted, start.Exit);
            Check.Equal(SourceBackendExclusion.DesktopFileChanged, start.Exclusions);
            Check.Contains("identityEvidence=RegisteredPackageBytesAndSignature processEvidence=NotChecked", start.FormatSafe());
        });
        asyncTest("Desktop runtime: cancellation after acquiring launch lease releases it without starting", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var lease = new TrackingLease();
            var traces = new List<SourceBackendDiagnostic>();
            var candidate = new CliCandidate(CliSource.Desktop, new ProcessStartInfo("must-never-launch"))
            { AcquireLaunchLease = () => { cancellation.Cancel(); return lease; } };
            await Check.ThrowsAsync<OperationCanceledException>(new CodexQuotaClient(QuotaDataSource.Desktop,
                () => new[] { candidate }, trace: traces.Add).ReadAsync(cancellation.Token));
            Check.True(lease.Disposed);
            Check.True(traces.All(item => item.Exit == SourceBackendExit.NotStarted));
        });
        asyncTest("Desktop runtime: process start failure releases acquired lease", async () =>
        {
            var lease = new TrackingLease();
            var candidate = new CliCandidate(CliSource.Desktop, new ProcessStartInfo("nonexistent-fixture-process"))
            { AcquireLaunchLease = () => lease };
            var result = await new CodexQuotaClient(QuotaDataSource.Desktop, () => new[] { candidate })
                .ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            Check.True(lease.Disposed);
        });
        asyncTest("Desktop runtime: launch lease stays held through a synthetic quota-only process lifetime", async () =>
        {
            using var environment = new FakeChildEnvironment("success");
            var lease = new TrackingLease();
            var earlyRelease = false;
            var candidate = new CliCandidate(CliSource.Desktop, CliLocator.Find(environment.Executable)!)
            { AcquireLaunchLease = () => lease };
            var result = await new CodexQuotaClient(QuotaDataSource.Desktop, () => new[] { candidate }, trace: item =>
            {
                if (item.Stage is SourceBackendStage.Initialize or SourceBackendStage.QuotaRead) earlyRelease |= lease.Disposed;
            }).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Ready, result.Status);
            Check.False(earlyRelease);
            Check.True(lease.Disposed);
            await environment.AssertChildrenExitedAsync(1);
        });
    }

    private sealed class TrackingLease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeEvidence : IDesktopRuntimeEvidence, IDisposable
    {
        private readonly TempDirectory _files = new();
        public DesktopPackageRegistration[] Packages { get; set; }
        public string LocalApplicationData { get; set; }
        public string Resource { get; }
        public string Runtime { get; }
        public IEnumerable<string> RuntimePaths { get; set; }
        public DesktopImageIdentity Anchor { get; set; } = new(4096, new string('a', 64), new Version(0, 159, 2, 0), true);
        public DesktopImageIdentity RuntimeImage { get; set; }
        public Dictionary<string, DesktopImageIdentity> Images { get; } = new();
        public bool ManifestValid { get; set; } = true;
        public Exception? RuntimeScanFailure { get; set; }
        public Exception? AnchorFailure { get; set; }
        public Exception? RuntimeOpenFailure { get; set; }
        public Dictionary<string, Exception> RuntimeFailures { get; } = new();
        public Action<string>? OnImageOpen { get; set; }
        public int PackageReads { get; private set; }
        public int RuntimeScans { get; private set; }
        public int ActiveLeases { get; private set; }
        public int OpenCount { get; private set; }
        public FakeEvidence()
        {
            var root = Path.Combine(_files.Path, "registered-package");
            Packages = [new("OpenAI.Codex_26.928.2636.0_x64__2p2nqsd0c76g0", root)];
            Resource = _files.Touch(Path.Combine("registered-package", "app", "resources", "codex.exe"));
            LocalApplicationData = Path.Combine(_files.Path, "private-user-local");
            Runtime = _files.Touch(Path.Combine("private-user-local", "OpenAI", "Codex", "bin", "codex.exe"));
            RuntimePaths = [Runtime]; RuntimeImage = Anchor;
        }
        public string AddRuntime(string child) => _files.Touch(Path.Combine("private-user-local", "OpenAI", "Codex", "bin", child, "codex.exe"));
        public IEnumerable<DesktopPackageRegistration> RegisteredPackages() { PackageReads++; return Packages; }
        public bool VerifyPackageIdentity(DesktopPackageRegistration package) => ManifestValid;
        public IEnumerable<string> ExistingRuntimePaths(string localApplicationData)
        { RuntimeScans++; if (RuntimeScanFailure is { } error) throw error; return RuntimePaths; }
        public IDesktopImageLease OpenImage(string path, DesktopImageIdentity? expected = null)
        {
            OpenCount++;
            OnImageOpen?.Invoke(path);
            if (path == Resource && AnchorFailure is { } failure) throw failure;
            if (path != Resource && RuntimeOpenFailure is { } runtimeFailure) throw runtimeFailure;
            if (RuntimeFailures.TryGetValue(path, out var perPathFailure)) throw perPathFailure;
            ActiveLeases++;
            return new Image(path, path == Resource ? Anchor : Images.TryGetValue(path, out var value) ? value : RuntimeImage, this);
        }
        public void Dispose() { Check.Equal(0, ActiveLeases); _files.Dispose(); }
        private sealed class Image(string path, DesktopImageIdentity identity, FakeEvidence owner) : IDesktopImageLease
        {
            private bool _disposed;
            public string Path { get; } = path;
            public DesktopImageIdentity Identity { get; } = identity;
            public void Dispose() { if (!_disposed) { _disposed = true; owner.ActiveLeases--; } }
        }
    }
}
