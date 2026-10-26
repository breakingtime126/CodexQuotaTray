using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CodexQuota.Tray;

/// <summary>
/// Read the real logon SID. WindowsIdentity.Groups deliberately filters out logon-ID
/// groups on Framework and modern .NET, so it cannot be used for this purpose.
/// </summary>
internal static class LogonSessionIdentity
{
    internal static string Read(WindowsIdentity identity)
    {
        const int tokenLogonSid = 28;
        const int insufficientBuffer = 122;
        const uint logonIdAttributes = 0xC0000000;
        var token = identity.AccessToken; // Borrowed handle; WindowsIdentity owns its lifetime.
        var minimum = Marshal.SizeOf(typeof(TokenGroups));
        var success = GetTokenInformation(token, tokenLogonSid, IntPtr.Zero, 0, out var length);
        var error = Marshal.GetLastWin32Error();
        if (success || error != insufficientBuffer) throw new Win32Exception(error);
        if (length < minimum || length > 65536) throw new SecurityException("Invalid logon information size.");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, tokenLogonSid, buffer, length, out var returned))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (returned < minimum || returned > length) throw new SecurityException("Invalid logon information.");
            var groups = Marshal.PtrToStructure<TokenGroups>(buffer);
            if (groups.Count != 1 || (groups.Group.Attributes & logonIdAttributes) != logonIdAttributes)
                throw new SecurityException("Missing logon identity.");
            var start = buffer.ToInt64();
            var sid = groups.Group.Sid.ToInt64();
            if (sid < start || sid > start + returned - 8) throw new SecurityException("Invalid logon SID.");
            var subAuthorities = Marshal.ReadByte(groups.Group.Sid, 1);
            if (subAuthorities > 15 || sid + 8 + subAuthorities * 4 > start + returned)
                throw new SecurityException("Invalid logon SID length.");
            var value = new SecurityIdentifier(groups.Group.Sid).Value;
            if (!value.StartsWith("S-1-5-5-", StringComparison.Ordinal)) throw new SecurityException("Unexpected logon identity.");
            return value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroups { public uint Count; public SidAndAttributes Group; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int informationLength, out int returnLength);
}
