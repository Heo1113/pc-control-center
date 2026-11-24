using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PcControlCenter.Operator;

/// <summary>
/// Whether the Operator host can use administrator rights, which is what the host guard refuses to automate with.
/// </summary>
/// <remarks>
/// On Windows that means: the Administrators group is enabled in the host's token, or its integrity level is above
/// medium (the level of a normal user's programs; Windows keeps medium-level programs away from higher windows). It is
/// the same integrity rule project E applies to windows (a window whose process has a higher level than the host is
/// treated as elevated and never automated). Windows' TokenElevation flag alone is not used: on a machine with UAC turned
/// off it reports even a Safer normal-user token (Administrators deny-only, medium integrity) as elevated.
/// </remarks>
public static partial class HostRights
{
    private const int TokenIntegrityLevel = 25;
    private const int MediumIntegrity = 0x2000;

    public static bool HasAdministratorRights(out string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            description = Environment.IsPrivilegedProcess ? "root" : "normal user";
            return Environment.IsPrivilegedProcess;
        }

        return WindowsRights(out description);
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsRights(out string description)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var administrators = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var integrity = IntegrityLevel(identity.Token);
        description = $"Administrators {(administrators ? "enabled" : "not enabled")}, integrity 0x{integrity:X4}, TokenElevation={Environment.IsPrivilegedProcess}";
        return administrators || integrity > MediumIntegrity;
    }

    [SupportedOSPlatform("windows")]
    private static int IntegrityLevel(nint token)
    {
        _ = GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var length);
        if (length <= 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            // TOKEN_MANDATORY_LABEL starts with the label SID pointer; the level is the SID's last sub-authority.
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            return int.Parse(sid.Value[(sid.Value.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int informationClass, nint information, int length, out int returnLength);
}
