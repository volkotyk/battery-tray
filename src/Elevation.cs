using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace BatteryTray;

/// <summary>
/// With UAC off every process gets the full admin token. The app needs no admin rights,
/// so it starts itself again with a SAFER "normal user" token (what runas /trustlevel:0x20000 does).
/// </summary>
public static class Elevation
{
    public const string RestrictedArg = "--restricted";

    public static bool IsAdmin()
    {
        using WindowsIdentity id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts this exe with a normal-user token. Returns false if Windows refuses.</summary>
    public static bool RelaunchRestricted(IEnumerable<string> args)
    {
        if (!SaferCreateLevel(SAFER_SCOPEID_USER, SAFER_LEVELID_NORMALUSER, SAFER_LEVEL_OPEN, out IntPtr level, IntPtr.Zero))
            return false;
        try
        {
            if (!SaferComputeTokenFromLevel(level, IntPtr.Zero, out IntPtr token, 0, IntPtr.Zero)) return false;
            try
            {
                string exe = Environment.ProcessPath!;
                string cmd = string.Join(' ', new[] { $"\"{exe}\"", RestrictedArg }.Concat(args.Select(a => $"\"{a}\"")));
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                if (!CreateProcessAsUser(token, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                        Path.GetDirectoryName(exe), ref si, out PROCESS_INFORMATION pi))
                    throw new Win32Exception();
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                return true;
            }
            finally { CloseHandle(token); }
        }
        catch (Win32Exception) { return false; }
        finally { SaferCloseLevel(level); }
    }

    private const uint SAFER_SCOPEID_USER = 2, SAFER_LEVELID_NORMALUSER = 0x20000, SAFER_LEVEL_OPEN = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferCreateLevel(uint scope, uint level, uint flags, out IntPtr handle, IntPtr reserved);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferComputeTokenFromLevel(IntPtr level, IntPtr inToken, out IntPtr outToken, uint flags, IntPtr reserved);
    [DllImport("advapi32.dll")] private static extern bool SaferCloseLevel(IntPtr level);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
