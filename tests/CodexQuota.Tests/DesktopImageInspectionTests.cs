using System.Buffers.Binary;
using CodexQuota.Core;

internal static class DesktopImageInspectionTests
{
    private const DesktopEvidenceLocation Runtime = DesktopEvidenceLocation.RuntimeImage;
    public static void Register(Action<string, Action> test)
    {
        foreach (var size in new long[] { 1024, 373698352, DesktopRuntimeDiscovery.MaximumImageBytes })
            test("Desktop image: production inspection reads only PE fields without hashing or active signature verification, bytes=" + size, () =>
            {
                using var stream = new HeaderOnlyImage(size);
                var calls = new List<string>(); var budget = new DesktopEvidenceBudget();
                var version = new Version(0, 167, 0, 0);
                var result = WindowsDesktopRuntimeEvidence.ReadImageIdentity(stream, new(size, version), budget, Runtime,
                    () => { calls.Add("version"); return version; }, () => calls.Add("path"));
                Check.Equal(new DesktopImageIdentity(size, version), result);
                Check.Equal(16L, stream.BytesRead);
                Check.True(calls.SequenceEqual(new[] { "path", "version", "path" }));
                var detail = budget.Limit(DesktopEvidenceLimit.DirectoryCount,
                    DesktopEvidenceStep.DirectoryEnumeration, Runtime).Desktop!;
                Check.Equal(size, detail.ImageBytes);
                Check.Equal(0L, detail.HashBytes);
                Check.Equal(0L, detail.HashMilliseconds);
                Check.Equal(DesktopHashProvider.None, detail.HashProvider);
            });
        test("Desktop image: same-size same-version content no longer needs byte equality", () =>
        {
            using var first = new HeaderOnlyImage(4096, contentMarker: 1);
            using var second = new HeaderOnlyImage(4096, contentMarker: 2);
            var version = new Version(1, 2, 3, 4);
            DesktopImageIdentity Read(HeaderOnlyImage stream) => WindowsDesktopRuntimeEvidence.ReadImageIdentity(
                stream, null, new(), Runtime, () => version, () => { });
            Check.Equal(Read(first), Read(second));
            Check.Equal(32L, first.BytesRead + second.BytesRead);
        });
        foreach (var problem in new[] { "empty", "oversize", "size mismatch", "not PE", "not x64", "DLL" })
            test("Desktop image: rejects " + problem + " before version calls", () =>
            {
                var size = problem == "empty" ? 0 : problem == "oversize" ? DesktopRuntimeDiscovery.MaximumImageBytes + 1 : 4096;
                using var stream = new HeaderOnlyImage(size);
                if (problem == "not PE") stream.Header[0] = 0;
                if (problem == "not x64") BinaryPrimitives.WriteUInt16LittleEndian(stream.Header.AsSpan(0x84), 0x14c);
                if (problem == "DLL") BinaryPrimitives.WriteUInt16LittleEndian(stream.Header.AsSpan(0x96), 0x2002);
                var calls = 0;
                Check.Throws<DesktopRuntimeValidationException>(() => WindowsDesktopRuntimeEvidence.ReadImageIdentity(
                    stream, problem == "size mismatch" ? new(4097, null) : null, new(), Runtime,
                    () => { calls++; return null; }, () => calls++));
                Check.Equal(0, calls);
            });
        test("Desktop image: images with no PE version preserve nullable metadata", () =>
        {
            using var stream = new HeaderOnlyImage(4096);
            var result = WindowsDesktopRuntimeEvidence.ReadImageIdentity(stream, null, new(), Runtime,
                () => null, () => { });
            Check.Null(result.FileVersion);
        });
        test("Desktop image: old evidence enum values remain stable and new evidence makes no bytes claim", () =>
        {
            Check.Equal(0, (int)SourceBackendIdentityEvidence.Unknown);
            Check.Equal(1, (int)SourceBackendIdentityEvidence.RegisteredDesktopPackage);
            Check.Equal(2, (int)SourceBackendIdentityEvidence.RegisteredPackageBytesAndSignature);
            Check.Equal(3, (int)SourceBackendIdentityEvidence.RegisteredPackageVersionAndSignature);
            Check.Equal(4, (int)SourceBackendIdentityEvidence.RegisteredPackageMetadata);
            Check.Null(typeof(QuotaState).Assembly.GetType("CodexQuota.Core.DesktopImageHasher"));
            Check.Null(typeof(QuotaState).Assembly.GetType("CodexQuota.Core.WindowsDesktopSignature"));
            Check.False(typeof(QuotaState).Assembly.GetTypes().SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                .Any(method => method.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false)
                    .Cast<System.Runtime.InteropServices.DllImportAttribute>().Any(import =>
                        import.Value.Equals("wintrust.dll", StringComparison.OrdinalIgnoreCase))));
        });
    }

    // A large virtual executable whose body cannot be read: any accidental full-file loop
    // fails deterministically. Version callbacks are synthetic, not OS metadata tests.
    private sealed class HeaderOnlyImage : Stream
    {
        private readonly long length;
        private long position;
        internal byte[] Header { get; } = new byte[256];
        internal long BytesRead { get; private set; }
        internal HeaderOnlyImage(long length, byte contentMarker = 0)
        {
            this.length = length;
            BinaryPrimitives.WriteUInt16LittleEndian(Header, 0x5a4d);
            BinaryPrimitives.WriteInt32LittleEndian(Header.AsSpan(0x3c), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(0x80), 0x4550);
            BinaryPrimitives.WriteUInt16LittleEndian(Header.AsSpan(0x84), 0x8664);
            BinaryPrimitives.WriteUInt16LittleEndian(Header.AsSpan(0x96), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(Header.AsSpan(0x98), 0x20b);
            Header[0xff] = contentMarker;
        }
        public override long Length => length;
        public override long Position { get => position; set => position = value; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position < 0 || count > 16 || position + count > Header.Length)
                throw new TestFailure("Desktop quota inspection must not read or hash the image body.");
            var read = (int)Math.Min(count, Math.Max(0, length - position));
            Array.Copy(Header, position, buffer, offset, read); position += read; BytesRead += read; return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => length + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
