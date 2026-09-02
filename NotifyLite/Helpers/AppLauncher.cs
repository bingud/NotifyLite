using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NotifyLite.Helpers;

/// <summary>
/// Launches the app that posted a toast from its Application User Model ID.
/// Avoids shell:AppsFolder as a first try — that opens the Applications folder
/// when the AUMID is a Win32 implicit ID rather than a packaged app.
/// </summary>
public static class AppLauncher
{
    public static bool TryLaunch(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;

        if (TryActivate(aumid)) return true;
        if (TryLaunchImplicitWin32(aumid)) return true;

        // Packaged apps look like "Publisher.Name_family!App"
        if (aumid.Contains('!') || (aumid.Contains('_') && !aumid.Contains('\\')))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"shell:AppsFolder\\{aumid}",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppLauncher] AppsFolder failed: {ex.Message}");
            }
        }

        Debug.WriteLine($"[AppLauncher] Could not launch: {aumid}");
        return false;
    }

    private static bool TryActivate(string aumid)
    {
        try
        {
            var aam = (IApplicationActivationManager)new ApplicationActivationManager();
            int hr = aam.ActivateApplication(aumid, null, ActivateOptions.None, out _);
            return hr >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Desktop apps without an explicit AUMID get "{KnownFolderGuid}\relative\path.exe".
    /// </summary>
    private static bool TryLaunchImplicitWin32(string aumid)
    {
        if (!aumid.StartsWith('{')) return false;
        var close = aumid.IndexOf('}');
        if (close < 1 || close + 2 >= aumid.Length) return false;
            if (!Guid.TryParse(aumid.Substring(1, close - 1), out var folderId)) return false;

        var relative = aumid[(close + 1)..].TrimStart('\\');
        if (string.IsNullOrEmpty(relative)) return false;

        try
        {
            int hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var pPath);
            if (hr != 0 || pPath == IntPtr.Zero) return false;

            string? folder;
            try { folder = Marshal.PtrToStringUni(pPath); }
            finally { Marshal.FreeCoTaskMem(pPath); }

            if (string.IsNullOrEmpty(folder)) return false;

            var exe = Path.Combine(folder, relative);
            if (!File.Exists(exe)) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppLauncher] Win32 launch failed: {ex.Message}");
            return false;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ActivateOptions options,
            out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    [Flags]
    private enum ActivateOptions { None = 0 }
}
