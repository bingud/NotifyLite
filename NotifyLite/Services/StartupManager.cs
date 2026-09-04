using Microsoft.Win32;
using System.Diagnostics;
using Windows.ApplicationModel;

namespace NotifyLite.Services;

/// <summary>
/// Auto-start on logon. Packaged (MSIX) apps cannot use the Run key — Windows
/// blocks launching the WindowsApps exe — so we use a windows.startupTask.
/// Unpackaged builds still use the registry.
/// </summary>
public static class StartupManager
{
    public const string TaskId = "NotifyLiteStartup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "NotifyLite";

    public readonly record struct Result(bool Enabled, string? Error);

    public static async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged())
        {
            ClearLegacyRunKey();
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartupManager] GetAsync failed: {ex.Message}");
                return false;
            }
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<Result> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged())
        {
            ClearLegacyRunKey();
            return await SetStartupTaskAsync(enabled);
        }

        SetRegistryRun(enabled);
        return new Result(enabled, null);
    }

    private static async Task<Result> SetStartupTaskAsync(bool enabled)
    {
        StartupTask task;
        try
        {
            task = await StartupTask.GetAsync(TaskId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupManager] GetAsync failed: {ex.Message}");
            return new Result(false,
                "Auto-start needs this NotifyLite version reinstalled (MSIX startup task).");
        }

        if (!enabled)
        {
            if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
                task.Disable();
            return new Result(false, null);
        }

        switch (task.State)
        {
            case StartupTaskState.Enabled:
            case StartupTaskState.EnabledByPolicy:
                return new Result(true, null);

            case StartupTaskState.Disabled:
            {
                var state = await task.RequestEnableAsync();
                if (state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
                    return new Result(true, null);
                if (state == StartupTaskState.DisabledByUser)
                    return new Result(false, DisabledByUserMessage);
                return new Result(false, "Windows refused to enable auto-start.");
            }

            case StartupTaskState.DisabledByUser:
                return new Result(false, DisabledByUserMessage);

            case StartupTaskState.DisabledByPolicy:
                return new Result(false, "Auto-start is blocked by policy.");

            default:
                return new Result(false, $"Auto-start state: {task.State}");
        }
    }

    private const string DisabledByUserMessage =
        "Windows disabled NotifyLite in startup apps. Turn it on in Task Manager → Startup apps.";

    private static void SetRegistryRun(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupManager] Registry Run failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The old Run-key entry pointed at the WindowsApps exe, which never launches.
    /// Only strip it for packaged installs.
    /// </summary>
    public static void ClearLegacyRunKey()
    {
        if (!IsPackaged()) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupManager] Could not clear Run key: {ex.Message}");
        }
    }

    private static bool IsPackaged()
    {
        try
        {
            _ = Package.Current.Id.FamilyName;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
