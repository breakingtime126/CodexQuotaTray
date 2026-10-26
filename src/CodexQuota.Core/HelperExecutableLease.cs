using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CodexQuota.Core;

/// <summary>
/// A verified reader image, held open without write/delete sharing until its owned process exits.
/// Windows directory handles also prevent a checked ancestor from being renamed underneath it.
/// </summary>
public sealed class HelperExecutableLease : IDisposable
{
    public const long MaximumExecutableBytes = 64L * 1024 * 1024;
    public const long MaximumCompressedBytes = 32L * 1024 * 1024;
    private readonly FileStream _image;
    private readonly List<IDisposable> _directories;
    public string ExecutablePath { get; }

    private HelperExecutableLease(string path, FileStream image, List<IDisposable> directories)
    { ExecutablePath = path; _image = image; _directories = directories; }

    public static HelperExecutableLease Extract(string localApplicationData, Func<Stream> openCompressedPayload,
        string expectedSha256, long executableLength, long compressedLength)
    {
        ValidateMetadata(expectedSha256, executableLength, compressedLength);
        var hash = expectedSha256.ToLowerInvariant();
        // Reject UNC/device/drive-relative roots before any filesystem probe, including
        // Directory.Exists, so cache resolution cannot authenticate to a remote share.
        if (!Path.IsPathRooted(localApplicationData)) throw Invalid();
        if (Path.DirectorySeparatorChar == '\\' &&
            (localApplicationData.Length < 3 || !char.IsLetter(localApplicationData[0]) ||
             localApplicationData[1] != ':' || localApplicationData[2] is not ('\\' or '/'))) throw Invalid();
        var root = Path.GetFullPath(localApplicationData);
        if (Path.DirectorySeparatorChar == '\\' && Native.GetDriveType(Path.GetPathRoot(root)!) == 4) throw Invalid();
        if (!Directory.Exists(root)) throw Invalid();
        var guards = new List<IDisposable>();
        string? temporary = null;
        try
        {
            var parent = PinAncestors(root, guards);
            foreach (var child in new[] { "CodexQuotaTray", "reader", ReadOnlyQuotaProtocol.HelperVersion + "-" + hash })
            {
                root = Path.Combine(root, child);
                if (parent is null)
                {
                    Directory.CreateDirectory(root);
                    PinDirectory(root, guards);
                }
                else
                {
                    var directory = OpenRelative(parent, child, directory: true, create: true);
                    guards.Add(directory);
                    EnsureAttributes(parent, directory: true);
                    // A retained nondeletable entry prevents FSCTL_SET_REPARSE_POINT from
                    // converting this directory in place. Create it by the retained handle,
                    // never through a path that an empty-directory junction race could redirect.
                    var marker = OpenRelative(directory, ".reader-pin", directory: false, create: true);
                    guards.Add(marker);
                    EnsureAttributes(directory, directory: true);
                    parent = directory;
                }
            }
            var path = Path.Combine(root, "codex-quota-reader.exe");
            if (ExistsIncludingReparsePoint(path))
                return new(path, OpenVerifiedImage(path, hash, executableLength), guards);

            // Resource bytes are bounded before decompression. The generated manifest records
            // the exact input and output sizes, in addition to the executable SHA-256.
            using var payload = openCompressedPayload() ?? throw Invalid();
            if (payload.CanSeek && payload.Length != compressedLength) throw Invalid();
            using var compressed = new MemoryStream();
            CopyBounded(payload, compressed, compressedLength);
            compressed.Position = 0;
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            temporary = Path.Combine(root, ".reader-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                CopyBounded(gzip, target, executableLength);
                target.Flush(flushToDisk: true);
                VerifyImage(target, hash, executableLength);
            }
            try { File.Move(temporary, path); }
            catch (IOException) when (ExistsIncludingReparsePoint(path))
            {
                // Another tray may have installed the identical payload. Never replace an
                // existing file, and never repair a mismatching image by overwriting it.
            }
            var verified = OpenVerifiedImage(path, hash, executableLength);
            DeleteTemporary(temporary);
            temporary = null;
            return new(path, verified, guards);
        }
        catch
        {
            DeleteTemporary(temporary);
            DisposeGuards(guards);
            throw;
        }
    }

    // Used by synthetic process tests; production always resolves the embedded payload above.
    internal static HelperExecutableLease OpenVerified(string path, string expectedSha256, long executableLength)
    {
        ValidateMetadata(expectedSha256, executableLength, 1);
        path = Path.GetFullPath(path);
        var guards = new List<IDisposable>();
        try
        {
            PinAncestors(Path.GetDirectoryName(path) ?? throw Invalid(), guards);
            return new(path, OpenVerifiedImage(path, expectedSha256, executableLength), guards);
        }
        catch { DisposeGuards(guards); throw; }
    }

    private static FileStream OpenVerifiedImage(string path, string expectedHash, long length)
    {
        FileStream image;
        if (Path.DirectorySeparatorChar == '\\')
        {
            // OPEN_REPARSE_POINT examines the object itself, rather than following a last-
            // component symlink between an attribute check and opening the executable.
            var handle = Native.CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000 | 0x08000000, IntPtr.Zero);
            if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
            try
            {
                EnsureAttributes(handle, directory: false);
                image = new FileStream(handle, FileAccess.Read, 8192, isAsync: false);
            }
            catch { handle.Dispose(); throw; }
        }
        else
        {
            EnsureAttributes(File.GetAttributes(path), directory: false);
            image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        try { VerifyImage(image, expectedHash, length); return image; }
        catch { image.Dispose(); throw; }
    }

    private static void ValidateMetadata(string hash, long executableLength, long compressedLength)
    {
        if (hash is not { Length: 64 } || hash.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) ||
            executableLength <= 0 || executableLength > MaximumExecutableBytes ||
            compressedLength <= 0 || compressedLength > MaximumCompressedBytes) throw Invalid();
    }

    private static void VerifyImage(FileStream image, string expectedHash, long length)
    {
        if (image.Length != length) throw Invalid();
        image.Position = 0;
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(image);
        var actual = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        var expected = expectedHash.ToLowerInvariant();
        var difference = 0;
        for (var index = 0; index < actual.Length; index++) difference |= actual[index] ^ expected[index];
        if (difference != 0) throw Invalid();
        image.Position = 0;
    }

    private static void CopyBounded(Stream source, Stream destination, long expectedLength)
    {
        var buffer = new byte[8192];
        long total = 0;
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            total += count;
            if (total > expectedLength) throw Invalid();
            destination.Write(buffer, 0, count);
        }
        if (total != expectedLength) throw Invalid();
    }

    private static SafeFileHandle? PinAncestors(string directory, List<IDisposable> guards)
    {
        var paths = new Stack<DirectoryInfo>();
        for (var item = new DirectoryInfo(directory); item is not null; item = item.Parent) paths.Push(item);
        if (Path.DirectorySeparatorChar != '\\')
        {
            while (paths.Count > 0) PinDirectory(paths.Pop().FullName, guards);
            return null;
        }
        var root = paths.Pop().FullName;
        // A per-user local cache must not cause remote filesystem authentication or execution.
        if (root.StartsWith("\\\\", StringComparison.Ordinal)) throw Invalid();
        var parent = PinDirectory(root, guards) ?? throw Invalid();
        while (paths.Count > 0)
        {
            var child = OpenRelative(parent, paths.Pop().Name, directory: true, create: false);
            guards.Add(child);
            // Once this child denies deletion, the checked parent cannot become empty or
            // acquire a reparse point. Each following open is rooted at that exact handle.
            EnsureAttributes(parent, directory: true);
            parent = child;
        }
        return parent;
    }

    private static SafeFileHandle? PinDirectory(string path, List<IDisposable> guards)
    {
        if (Path.DirectorySeparatorChar != '\\')
        {
            EnsureAttributes(File.GetAttributes(path), directory: true);
            return null;
        }
        var handle = Native.CreateFile(path, 0x80, 1 | 2, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
        try { EnsureAttributes(handle, directory: true); guards.Add(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory, bool create)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || name is "." or "..") throw Invalid();
        var text = Marshal.StringToHGlobalUni(name);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.UnicodeString)));
        try
        {
            var unicode = new Native.UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = text
            };
            Marshal.StructureToPtr(unicode, unicodePointer, false);
            var attributes = new Native.ObjectAttributes
            {
                Length = Marshal.SizeOf(typeof(Native.ObjectAttributes)),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = 0x40 | 0x1000 // OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE.
            };
            var status = Native.NtCreateFile(out var handle, directory ? 0x001000A0u : 0x00100081u,
                ref attributes, out _, IntPtr.Zero, 0x80, directory ? 3u : 1u, create ? 3u : 1u,
                0x00200000u | 0x20u | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
            if (status < 0)
            {
                handle?.Dispose();
                throw new Win32Exception(unchecked((int)Native.RtlNtStatusToDosError(status)));
            }
            if (handle is null || handle.IsInvalid) { handle?.Dispose(); throw Invalid(); }
            try { EnsureAttributes(handle, directory); return handle; }
            catch { handle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(unicodePointer); Marshal.FreeHGlobal(text); }
    }

    private static void EnsureAttributes(SafeFileHandle handle, bool directory)
    {
        if (!Native.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        EnsureAttributes((FileAttributes)info.Attributes, directory);
    }
    private static void EnsureAttributes(FileAttributes attributes, bool directory)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw Invalid();
    }
    private static bool ExistsIncludingReparsePoint(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static void DeleteTemporary(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private static InvalidDataException Invalid() => new("Embedded reader integrity validation failed.");
    private static void DisposeGuards(List<IDisposable> guards)
    {
        for (var index = guards.Count - 1; index >= 0; index--) guards[index].Dispose();
    }
    public void Dispose() { _image.Dispose(); DisposeGuards(_directories); }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory, ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor, SecurityQualityOfService;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
        [DllImport("ntdll.dll")]
        internal static extern int NtCreateFile(out SafeFileHandle handle, uint desiredAccess,
            ref ObjectAttributes attributes, out IoStatusBlock statusBlock, IntPtr allocationSize,
            uint attributesMask, uint sharing, uint disposition, uint options, IntPtr extendedAttributes, uint extendedAttributesLength);
        [DllImport("ntdll.dll")]
        internal static extern uint RtlNtStatusToDosError(int status);
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint GetDriveType(string rootPath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    }
}
