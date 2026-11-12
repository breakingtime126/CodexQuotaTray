using CodexQuota.Core;
using Microsoft.Win32.SafeHandles;

internal static class DesktopEvidenceBudgetTests
{
    private const DesktopEvidenceLocation Runtime = DesktopEvidenceLocation.RuntimeImage;
    public static void Register(Action<string, Action> test)
    {
        test("Desktop budget: launch phase does not restart the overall deadline", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            budget.BeginPhase();
            budget.CheckImageSize(4096, Runtime);
            clock.Advance(20000); budget.BeginPhase();
            budget.CheckImageSize(8192, Runtime);
            clock.Advance(20000);
            var error = Failure(budget.BeginPhase);
            AssertLimit(error, DesktopEvidenceLimit.VerificationTime, DesktopEvidenceStep.PackageFamilyQuery);
            Check.Equal(40000L, error.Desktop!.VerificationMilliseconds);
            Check.Equal(20000L, error.Desktop.PhaseMilliseconds);
            Check.Equal(0L, error.Desktop.HashBytes);
            Check.Equal(0L, error.Desktop.HashMilliseconds);
            Check.Equal(DesktopHashProvider.None, error.Desktop.HashProvider);
        });
        test("Desktop budget: every image records its size without a cumulative hash budget", () =>
        {
            var budget = new DesktopEvidenceBudget();
            for (var index = 0; index < 8; index++)
                budget.CheckImageSize(DesktopRuntimeDiscovery.MaximumImageBytes, Runtime);
            var detail = budget.Limit(DesktopEvidenceLimit.DirectoryCount,
                DesktopEvidenceStep.DirectoryEnumeration, Runtime).Desktop!;
            Check.Equal(DesktopRuntimeDiscovery.MaximumImageBytes, detail.ImageBytes);
            Check.Equal(0L, detail.HashBytes);
            Check.Equal(0L, detail.HashMilliseconds);
            Check.Equal(DesktopHashProvider.None, detail.HashProvider);
        });
        test("Desktop budget: image size limit remains before metadata validation", () =>
        {
            var budget = new DesktopEvidenceBudget();
            var error = Failure(() => budget.CheckImageSize(DesktopRuntimeDiscovery.MaximumImageBytes + 1, Runtime));
            AssertLimit(error, DesktopEvidenceLimit.ImageBytes, DesktopEvidenceStep.ImageRead);
            Check.Equal(DesktopRuntimeDiscovery.MaximumImageBytes + 1, error.Desktop!.ImageBytes);
            Check.Equal(0L, error.Desktop.HashBytes);
            foreach (var length in new long[] { 0, -1 })
                Check.Equal(SourceBackendExclusion.DesktopRuntimeUnverified,
                    Failure(() => budget.CheckImageSize(length, Runtime)).Exclusion);
        });
        test("Desktop budget: decoration clears legacy hash activity rather than copying it", () =>
        {
            var budget = new DesktopEvidenceBudget(); budget.CheckImageSize(8192, Runtime);
            var old = new DesktopRuntimeValidationException(SourceBackendExclusion.Inaccessible,
                desktop: new(DesktopEvidenceStep.VersionRead, Runtime)
                { HashBytes = 123456, HashMilliseconds = 12, HashProvider = DesktopHashProvider.WindowsCng });
            var detail = budget.Decorate(old).Desktop!;
            Check.Equal(8192L, detail.ImageBytes);
            Check.Equal(0L, detail.HashBytes);
            Check.Equal(0L, detail.HashMilliseconds);
            Check.Equal(DesktopHashProvider.None, detail.HashProvider);
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
        test("Desktop budget: image metadata stage crossing deadline rejects before version read", () =>
        {
            var clock = new Clock(); var budget = new DesktopEvidenceBudget(clock.Read);
            var versionReads = 0;
            var error = Failure(() =>
            {
                DesktopEvidenceOperation.Run(DesktopEvidenceStep.ImageRead, Runtime, () => clock.Advance(40001), budget);
                DesktopEvidenceOperation.Run(DesktopEvidenceStep.VersionRead, Runtime, () => versionReads++, budget);
            });
            AssertLimit(error, DesktopEvidenceLimit.VerificationTime, DesktopEvidenceStep.ImageRead);
            Check.Equal(0, versionReads);
        });
        test("Desktop budget: shared cancellation blocks next operation without throwing cancellation", () =>
        {
            using var cancel = new CancellationTokenSource();
            var budget = new DesktopEvidenceBudget(cancellationToken: cancel.Token);
            budget.BeginPhase(); cancel.Cancel();
            var called = false;
            var error = Failure(() => DesktopEvidenceOperation.Run(DesktopEvidenceStep.ImageRead,
                Runtime, () => called = true, budget));
            AssertLimit(error, DesktopEvidenceLimit.VerificationTime, DesktopEvidenceStep.ImageRead);
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
