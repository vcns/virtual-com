using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Vcns.VirtualCom.Bridge.Tests;

/// <summary>
/// Runs code with a standard user's rights, even when the tests run elevated (as they do in CI): on the calling thread,
/// the Administrators group is deny-only, privileges are removed and the integrity level is medium.
/// </summary>
internal static unsafe partial class StandardUser
{
    private const uint DISABLE_MAX_PRIVILEGE = 0x1;
    private const uint SE_GROUP_INTEGRITY = 0x20;
    private const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    public static T Run<T>(Func<T> func)
    {
        using var token = RestrictedToken();
        return WindowsIdentity.RunImpersonated(token, () =>
        {
            // Windows quietly falls back to an identification-only token when impersonation isn't allowed, and every
            // registry read would then fail; this makes sure the code really runs as the restricted user.
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.ImpersonationLevel != TokenImpersonationLevel.Impersonation
                || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Couldn't drop administrator rights for this test.");
            return func();
        });
    }

    private static SafeAccessTokenHandle RestrictedToken()
    {
        // The restricted token gets the same access as this handle, and needs AdjustDefault to lower its integrity.
        using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query | TokenAccessLevels.AdjustDefault);
        var administrators = Binary(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        var mediumIntegrity = Binary(new SecurityIdentifier("S-1-16-8192"));
        fixed (byte* admin = administrators, medium = mediumIntegrity)
        {
            var disable = new SID_AND_ATTRIBUTES { Sid = (IntPtr)admin };
            if (!CreateRestrictedToken(current.AccessToken.DangerousGetHandle(), DISABLE_MAX_PRIVILEGE, 1, &disable, 0, IntPtr.Zero, 0, IntPtr.Zero, out var handle))
                throw new Win32Exception();
            var token = new SafeAccessTokenHandle(handle);
            var label = new SID_AND_ATTRIBUTES { Sid = (IntPtr)medium, Attributes = SE_GROUP_INTEGRITY };
            if (!SetTokenInformation(token.DangerousGetHandle(), TokenIntegrityLevel, &label, (uint)(sizeof(SID_AND_ATTRIBUTES) + mediumIntegrity.Length)))
            {
                var error = new Win32Exception();
                token.Dispose();
                throw error;
            }
            return token;
        }
    }

    private static byte[] Binary(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateRestrictedToken(IntPtr existingToken, uint flags, uint disableSidCount, SID_AND_ATTRIBUTES* sidsToDisable,
        uint deletePrivilegeCount, IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict, out IntPtr newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(IntPtr token, int informationClass, void* information, uint length);
}
