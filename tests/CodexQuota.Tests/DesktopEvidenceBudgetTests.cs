using System.Security.Cryptography;
using CodexQuota.Core;
using Microsoft.Win32.SafeHandles;

internal static class DesktopEvidenceBudgetTests
{
    private const DesktopEvidenceLocation Runtime = DesktopEvidenceLocation.RuntimeImage;
    public static void Register(Action<string, Action> test)
    {
        test("Desktop hash: production provider hashes full 1 MiB chunks and trailing bytes", () =>
        {
            var bytes = new byte[DesktopImageHasher.BufferBytes * 2 + 179];
            new Random(723).NextBytes(bytes);
            using var stream = new TimedStream(bytes, new Clock());
            var actual = DesktopImageHasher.Compute(stream, new DesktopEvidenceBudget(), Runtime);
            Check.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), actual);
            Check.Equal(DesktopHashProvider.PlatformDefault, DesktopImageHasher.Provider);
            Check.True(stream.Requests.All(size => size == 1024 * 1024));
            Check.Equal(4, stream.Requests.Count); // two full chunks, tail, EOF
        });
        test("Desktop hash: earlier signature and scan work does not consume hash time", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            budget.BeginPhase();
            DesktopEvidenceOperation.Run(DesktopEvidenceStep.SignatureVerify, Runtime,
                () => clock.Advance(12100), budget);
            using var stream = new TimedStream(new byte[65536], clock, readMilliseconds: 0.6);
            Check.Equal(64, DesktopImageHasher.Compute(stream, budget, Runtime).Length);
            var detail = budget.Limit(DesktopEvidenceLimit.DirectoryCount,
                DesktopEvidenceStep.DirectoryEnumeration, Runtime).Desktop!;
            Check.Equal(0L, detail.HashMilliseconds); // 0.6 ms, rounded down only for display
            Check.Equal(12100L, detail.PhaseMilliseconds);
            Check.Equal(65536L, detail.HashBytes);
        });
        test("Desktop hash: hash time is cumulative across images in one phase", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            budget.BeginPhase();
            using var first = new TimedStream(new byte[16], clock, readMilliseconds: 9000);
            DesktopImageHasher.Compute(first, budget, DesktopEvidenceLocation.PackageImage);
            using var second = new TimedStream(new byte[16], clock, readMilliseconds: 3001);
            var error = Failure(() => DesktopImageHasher.Compute(second, budget, Runtime));
            AssertLimit(error, DesktopEvidenceLimit.HashTime, DesktopEvidenceStep.ImageHash);
            Check.Equal(12001L, error.Desktop!.HashMilliseconds);
            Check.Equal(32L, error.Desktop.HashBytes);
            Check.Equal(16L, error.Desktop.ImageBytes);
            Check.Equal(1, second.Requests.Count);
            Check.Null(error.Code);
        });
        foreach (var stage in new[] { "transform", "final", "eof" })
            test("Desktop hash: budget includes " + stage + " and checks immediately afterward", () =>
            {
                var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
                budget.BeginPhase();
                using var stream = new TimedStream(new byte[17], clock,
                    eofMilliseconds: stage == "eof" ? 12000 : 0);
                var hash = new TimedHash(clock, stage == "transform" ? 12000 : 0, stage == "final" ? 12000 : 0);
                var error = Failure(() => DesktopImageHasher.Compute(stream, budget, Runtime, () => hash));
                AssertLimit(error, DesktopEvidenceLimit.HashTime, DesktopEvidenceStep.ImageHash);
                Check.Equal(12000L, error.Desktop!.HashMilliseconds);
                Check.True(hash.Disposed);
                Check.Equal(stage == "transform" ? 1 : 2, stream.Requests.Count);
            });
        test("Desktop hash: registration confirmation does not clear phase totals", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            var evidence = new WindowsDesktopRuntimeEvidence(registeredPackages: () => Array.Empty<DesktopPackageRegistration>(), budget: budget);
            evidence.BeginEvidencePhase();
            using var first = new TimedStream(new byte[5], clock, readMilliseconds: 9000);
            DesktopImageHasher.Compute(first, budget, Runtime);
            Check.Equal(0, evidence.RegisteredPackages().Count());
            using var second = new TimedStream(new byte[5], clock, readMilliseconds: 4000);
            var error = Failure(() => DesktopImageHasher.Compute(second, budget, Runtime));
            Check.Equal(13000L, error.Desktop!.HashMilliseconds);
            AssertLimit(error, DesktopEvidenceLimit.HashTime, DesktopEvidenceStep.ImageHash);
        });
        test("Desktop budget: explicit launch phase resets hash time but never lifetime", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            budget.BeginPhase();
            using var first = new TimedStream(new byte[5], clock, readMilliseconds: 9000);
            DesktopImageHasher.Compute(first, budget, Runtime);
            clock.Advance(11000);
            budget.BeginPhase();
            using var second = new TimedStream(new byte[5], clock, readMilliseconds: 4000);
            DesktopImageHasher.Compute(second, budget, Runtime);
            clock.Advance(16000);
            var error = Failure(budget.BeginPhase);
            AssertLimit(error, DesktopEvidenceLimit.VerificationTime, DesktopEvidenceStep.PackageFamilyQuery);
            Check.Equal(40000L, error.Desktop!.VerificationMilliseconds);
            Check.Equal(4000L, error.Desktop.HashMilliseconds);
        });
        test("Desktop budget: lazily enumerated package work is inside overall deadline", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            var moves = 0;
            IEnumerable<DesktopPackageRegistration> Packages()
            {
                moves++; clock.Advance(40001);
                yield return new("OpenAI.Codex_26.928.2636.0_x64__2p2nqsd0c76g0", "fixture");
                moves++; throw new IOException("Must not read stale registration");
            }
            var evidence = new WindowsDesktopRuntimeEvidence(registeredPackages: Packages, budget: budget);
            var entries = evidence.RegisteredPackages();
            Check.Equal(0, moves);
            var error = Failure(() => entries.FirstOrDefault());
            AssertLimit(error, DesktopEvidenceLimit.VerificationTime, DesktopEvidenceStep.PackageFamilyQuery);
            Check.Equal(1, moves);
        });
        test("Desktop budget: final signature stage crossing deadline rejects before next stage", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            var versionReads = 0;
            var error = Failure(() =>
            {
                DesktopEvidenceOperation.Run(DesktopEvidenceStep.SignatureVerify, Runtime, () => clock.Advance(40001), budget);
                DesktopEvidenceOperation.Run(DesktopEvidenceStep.VersionRead, Runtime, () => versionReads++, budget);
            });
            AssertLimit(error, DesktopEvidenceLimit.VerificationTime, DesktopEvidenceStep.SignatureVerify);
            Check.Equal(0, versionReads);
        });
        test("Desktop budget: shared cancellation blocks next operation without throwing cancellation", () =>
        {
            using var cancel = new CancellationTokenSource();
            var budget = new DesktopEvidenceBudget(cancellationToken: cancel.Token);
            budget.BeginPhase(); cancel.Cancel();
            var called = false;
            var error = Failure(() => DesktopEvidenceOperation.Run(DesktopEvidenceStep.SignatureVerify,
                Runtime, () => called = true, budget));
            AssertLimit(error, DesktopEvidenceLimit.VerificationTime, DesktopEvidenceStep.SignatureVerify);
            Check.False(called);
        });
        test("Desktop budget: completed native open crossing deadline closes returned handle", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            var api = new SlowOpenApi(clock); var guards = new List<IDisposable>();
            try
            {
                var error = Failure(() => DesktopReadOnlyFileLease.OpenWindowsTarget(
                    @"C:\safe\codex.exe", false, guards, Runtime, api, budget).Dispose());
                AssertLimit(error, DesktopEvidenceLimit.VerificationTime, DesktopEvidenceStep.RootOpen);
                Check.True(api.Handle!.IsClosed);
                Check.Equal(0, api.MetadataCalls);
                Check.Equal(0, guards.Count);
            }
            finally { foreach (var guard in guards) guard.Dispose(); }
        });
        test("Desktop hash: image and total byte limits have distinct closed diagnostics", () =>
        {
            var budget = new DesktopEvidenceBudget();
            using var oversized = new DeclaredLengthStream(DesktopRuntimeDiscovery.MaximumImageBytes + 1);
            var image = Failure(() => DesktopImageHasher.Compute(oversized, budget, Runtime));
            AssertLimit(image, DesktopEvidenceLimit.ImageBytes, DesktopEvidenceStep.ImageHash);
            Check.Equal(DesktopRuntimeDiscovery.MaximumImageBytes + 1, image.Desktop!.ImageBytes);
            Check.Equal(0L, image.Desktop.HashBytes);
            budget.BeginPhase();
            for (var index = 0; index < 4; index++) budget.ReserveImage(DesktopRuntimeDiscovery.MaximumImageBytes, Runtime);
            var total = Failure(() => budget.ReserveImage(1, Runtime));
            AssertLimit(total, DesktopEvidenceLimit.TotalHashBytes, DesktopEvidenceStep.ImageHash);
            budget.BeginPhase();
            budget.ReserveImage(DesktopRuntimeDiscovery.MaximumImageBytes, Runtime);
        });
        foreach (var stage in new[] { "factory", "read", "transform", "final" })
            test("Desktop hash: " + stage + " failure preserves exact read-failure provenance", () =>
            {
                var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
                using var stream = new TimedStream(new byte[9], clock, failRead: stage == "read");
                var hash = new TimedHash(clock, failAt: stage);
                var error = Failure(() => DesktopImageHasher.Compute(stream, budget, Runtime,
                    () => stage == "factory" ? throw new CryptographicException("private factory") : hash));
                Check.Equal(stage == "factory" ? DesktopEvidenceStep.HashProviderCreate : DesktopEvidenceStep.ImageHash,
                    error.Desktop!.Step);
                Check.Equal(Runtime, error.Desktop.Location);
                Check.True(error.EvidenceReadFailure);
                Check.Equal(DesktopEvidenceLimit.None, error.Desktop.Limit);
                Check.False(error.Desktop.FormatSafe().Contains("private", StringComparison.Ordinal));
                Check.True(stage == "factory" || hash.Disposed);
            });
        test("Desktop budget: native failure provenance wins if the failed operation crosses deadline", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            var error = Failure(() => DesktopEvidenceOperation.Run<int>(DesktopEvidenceStep.TargetOpen, Runtime, () =>
            {
                clock.Advance(40001);
                throw new System.ComponentModel.Win32Exception(5, "private denied path");
            }, budget));
            Check.Equal(SourceBackendExclusion.Inaccessible, error.Exclusion);
            Check.Equal(5, error.Code);
            Check.True(error.EvidenceReadFailure);
            Check.Equal(DesktopEvidenceLimit.None, error.Desktop!.Limit);
            Check.Equal(DesktopEvidenceStep.TargetOpen, error.Desktop.Step);
            Check.Equal(40001L, error.Desktop.VerificationMilliseconds);
        });
        test("Desktop budget: directory count retains exact limit and operation", () =>
        {
            using var files = new TempDirectory();
            for (var index = 0; index < 65; index++) Directory.CreateDirectory(Path.Combine(files.Path, index.ToString()));
            using var directory = DesktopReadOnlyFileLease.PinDirectory(files.Path, new DesktopEvidenceBudget());
            var error = Failure(() => directory.ChildDirectoryNames());
            AssertLimit(error, DesktopEvidenceLimit.DirectoryCount, DesktopEvidenceStep.DirectoryEnumeration);
        });
        test("Desktop diagnostics: limit provider and bounded numbers are safe closed fields", () =>
        {
            var detail = new DesktopEvidenceDiagnostic(DesktopEvidenceStep.ImageHash, Runtime)
            {
                Limit = (DesktopEvidenceLimit)987, HashProvider = (DesktopHashProvider)987,
                ImageBytes = long.MaxValue, HashBytes = -1, HashMilliseconds = 12001,
                PhaseMilliseconds = 15000, VerificationMilliseconds = 30000
            };
            var safe = detail.FormatSafe();
            Check.Contains("desktopLimit=Unknown hashProvider=Unknown", safe);
            Check.Contains("imageBytes=999999999999 hashBytes=0 hashMs=12001 phaseMs=15000 verificationMs=30000", safe);
        });
    }

    private static void AssertLimit(DesktopRuntimeValidationException error, DesktopEvidenceLimit limit, DesktopEvidenceStep step)
    {
        Check.Equal(SourceBackendExclusion.DesktopEvidenceLimit, error.Exclusion);
        Check.Equal(limit, error.Desktop!.Limit);
        Check.Equal(step, error.Desktop.Step);
        Check.Null(error.Desktop.NativeCode);
    }
    private static DesktopRuntimeValidationException Failure(Action action)
    {
        try { action(); }
        catch (DesktopRuntimeValidationException error) { return error; }
        throw new TestFailure("Expected bounded Desktop evidence failure.");
    }
    private sealed class Clock
    {
        private TimeSpan _now;
        internal TimeSpan Read() => _now;
        internal void Advance(double milliseconds) => _now += TimeSpan.FromMilliseconds(milliseconds);
    }
    private sealed class TimedStream(byte[] bytes, Clock clock, double readMilliseconds = 0,
        double eofMilliseconds = 0, bool failRead = false) : MemoryStream(bytes, writable: false)
    {
        internal List<int> Requests { get; } = new();
        public override int Read(byte[] buffer, int offset, int count)
        {
            Requests.Add(count);
            if (failRead) throw new IOException("private read");
            var read = base.Read(buffer, offset, count);
            clock.Advance(read == 0 ? eofMilliseconds : readMilliseconds);
            return read;
        }
    }
    private sealed class DeclaredLengthStream(long length) : MemoryStream
    {
        public override long Length => length;
        public override int Read(byte[] buffer, int offset, int count) => throw new TestFailure("Oversized image must not be read.");
    }
    private sealed class TimedHash(Clock clock, double transformMilliseconds = 0, double finalMilliseconds = 0,
        string? failAt = null) : HashAlgorithm
    {
        private readonly SHA256 _inner = SHA256.Create();
        internal bool Disposed { get; private set; }
        public override void Initialize() => _inner.Initialize();
        protected override void HashCore(byte[] array, int ibStart, int cbSize)
        {
            clock.Advance(transformMilliseconds);
            if (failAt == "transform") throw new CryptographicException("private transform");
            _inner.TransformBlock(array, ibStart, cbSize, array, ibStart);
        }
        protected override byte[] HashFinal()
        {
            clock.Advance(finalMilliseconds);
            if (failAt == "final") throw new CryptographicException("private final");
            _inner.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return _inner.Hash!;
        }
        protected override void Dispose(bool disposing)
        { Disposed = true; if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class SlowOpenApi(Clock clock) : DesktopReadOnlyFileLease.IWindowsFileApi
    {
        internal SafeFileHandle? Handle;
        internal int MetadataCalls;
        public uint DriveType(string root) => 3;
        public SafeFileHandle OpenRoot(string root, uint access, uint sharing, uint disposition, uint flags)
        { clock.Advance(40001); return Handle = new SafeFileHandle(new IntPtr(101), ownsHandle: false); }
        public SafeFileHandle OpenRelative(SafeFileHandle root, string path, uint access, uint attributes,
            uint sharing, uint disposition, uint options) => throw new TestFailure("Must not open another handle.");
        public FileAttributes Attributes(SafeFileHandle handle) { MetadataCalls++; return FileAttributes.Directory; }
        public string CanonicalPath(SafeFileHandle handle) { MetadataCalls++; return @"C:\"; }
    }
}
