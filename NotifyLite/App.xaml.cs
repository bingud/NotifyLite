using NotifyLite.Helpers;
using NotifyLite.Managers;
using NotifyLite.Services;
using NotifyLite.Windows;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace NotifyLite;

/// <summary>
/// Application entry point. Runs as a tray-only app with no main window.
/// Orchestrates: notification listening -> custom toast rendering -> tray management.
/// </summary>
public partial class App : Application
{
    private ConfigManager _configManager = null!;
    private NotificationSuppressor _suppressor = null!;
    private NotificationListener _listener = null!;
    private ToastManager _toastManager = null!;
    private TrayManager _trayManager = null!;
    private NotificationHistoryManager _historyManager = null!;
    private FloatingIconWindow? _floatingIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handlers to prevent crashes
        DispatcherUnhandledException += (_, args) =>
        {
            Debug.WriteLine($"[App] Unhandled UI exception: {args.Exception.Message}");
            Debug.WriteLine(args.Exception.StackTrace);
            args.Handled = true; // Prevent crash
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Debug.WriteLine($"[App] Unhandled domain exception: {(args.ExceptionObject as Exception)?.Message}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Debug.WriteLine($"[App] Unobserved task exception: {args.Exception.Message}");
            args.SetObserved(); // Prevent crash
        };

        // 1. Load configuration
        _configManager = new ConfigManager();
        _configManager.Load();
        _configManager.ConfigChanged += OnConfigChanged;

        // 2. Initialize toast manager (handles custom UI rendering)
        _toastManager = new ToastManager(_configManager);

        // 2b. Initialize notification history
        _historyManager = new NotificationHistoryManager(_configManager);

        // 2c. Initialize floating icon if enabled
        if (_configManager.Config.ShowFloatingIcon)
        {
            ShowFloatingIcon();
        }

        // 2d. Wire history manager to toast manager
        _toastManager.SetHistoryManager(_historyManager, _floatingIcon);

        // 3. Native banners stay in Action Center; we only suppress popups
        //    when custom toasts are enabled.
        ActionCenterManager.Initialize();

        // 4. Initialize notification suppressor (applied immediately if custom toasts are on)
        _suppressor = new NotificationSuppressor();
        ApplyBannerMode();

        // 5. Initialize notification listener
        _listener = new NotificationListener();
        _listener.NotificationReceived += OnNotificationReceived;

        // 6. Initialize tray icon with menu
        _trayManager = new TrayManager(_configManager);
        _trayManager.Initialize();
        _trayManager.EnabledChanged += OnEnabledChanged;
        _trayManager.HistoryRequested += (_, _) => Dispatcher.BeginInvoke(ToggleHistoryWidget);
        _trayManager.ExitRequested += (_, _) => Shutdown();
        _historyManager.UnreadChanged += (_, _) => Dispatcher.BeginInvoke(ApplyUnreadBadge);
        ApplyUnreadBadge();

        // 7. Start intercepting if enabled
        if (_configManager.Config.Enabled)
        {
            await StartInterception();
        }
        else
        {
            _trayManager.UpdateTooltip("Disabled");
        }
    }

    /// <summary>Handle config changes (e.g. floating icon toggle).</summary>
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var config = _configManager.Config;

            if (config.ShowFloatingIcon)
            {
                if (_floatingIcon == null || !_floatingIcon.IsVisible)
                {
                    ShowFloatingIcon();
                    _toastManager.SetFloatingIcon(_floatingIcon);
                }
            }
            else
            {
                HideFloatingIcon();
                _toastManager.SetFloatingIcon(null);
            }

            ApplyBannerMode();
            ApplyUnreadBadge();
            if (!config.ShowCustomToasts)
            {
                _toastManager.DismissAll();
            }
        });
    }

    private void ApplyUnreadBadge()
    {
        var count = _configManager.Config.CountUnreadNotifications
            ? _historyManager.UnreadCount
            : 0;
        _trayManager.SetUnreadCount(count);
    }

    private void ShowFloatingIcon()
    {
        if (_floatingIcon != null) return;
        _floatingIcon = new FloatingIconWindow(_configManager, _historyManager);
        _floatingIcon.Show();
    }

    private void HideFloatingIcon()
    {
        if (_floatingIcon != null)
        {
            _floatingIcon.Close();
            _floatingIcon = null;
        }
    }

    private void ApplyBannerMode()
    {
        if (!_configManager.Config.Enabled)
        {
            _suppressor.Restore();
            return;
        }

        if (_configManager.Config.ShowCustomToasts)
            _suppressor.Suppress();
        else
            _suppressor.Restore();
    }

    private void ToggleHistoryWidget()
    {
        var existing = Current.Windows.OfType<HistoryWidget>().FirstOrDefault();
        if (existing != null)
        {
            existing.Close();
            return;
        }

        var widget = new HistoryWidget(_historyManager, _configManager, _floatingIcon);
        PositionHistoryWidget(widget);
        widget.Show();
        widget.Activate();
    }

    private void PositionHistoryWidget(HistoryWidget widget)
    {
        var workArea = SystemParameters.WorkArea;
        if (_floatingIcon is { IsVisible: true })
        {
            if (_floatingIcon.Left > workArea.Width / 2)
                widget.Left = _floatingIcon.Left - 400 - 10;
            else
                widget.Left = _floatingIcon.Left + _floatingIcon.Width + 10;
            widget.Top = _floatingIcon.Top;
        }
        else
        {
            widget.Left = workArea.Right - 400 - 16;
            widget.Top = workArea.Bottom - 420;
        }

        if (widget.Top < workArea.Top + 10)
            widget.Top = workArea.Top + 10;
        if (widget.Top + widget.MaxHeight > workArea.Bottom - 10)
            widget.Top = workArea.Bottom - widget.MaxHeight - 10;
    }

    /// <summary>Start listening for notifications and suppress native banners.</summary>
    private async Task StartInterception()
    {
        ApplyBannerMode();

        // Retry loop: after a reboot, Windows notification subsystem may not
        // be ready immediately. We retry a few times with delays before giving up.
        const int maxRetries = 5;
        const int delaySeconds = 5;
        bool accessGranted = false;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            accessGranted = await _listener.StartAsync();
            if (accessGranted) break;

            Debug.WriteLine($"[App] Notification access attempt {attempt}/{maxRetries} failed, retrying in {delaySeconds}s...");

            if (attempt < maxRetries)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }

        if (accessGranted)
        {
            _trayManager.UpdateTooltip("Active - Listening");
            Debug.WriteLine("[App] Notification interception started.");
        }
        else
        {
            _trayManager.UpdateTooltip("Permission Required");
            Debug.WriteLine("[App] Notification access was not granted after retries.");

            MessageBox.Show(
                "NotifyLite needs permission to access your notifications.\n\n" +
                "Please grant notification access in Windows Settings:\n" +
                "Settings > Privacy > Notifications",
                "NotifyLite - Permission Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>Stop listening and restore native banners.</summary>
    private void StopInterception()
    {
        _listener.Stop();
        _suppressor.Restore();
        _trayManager.UpdateTooltip("Disabled");
        _toastManager.DismissAll();
        Debug.WriteLine("[App] Notification interception stopped.");
    }

    /// <summary>Handle enable/disable toggle from tray menu.</summary>
    private async void OnEnabledChanged(object? sender, bool enabled)
    {
        if (enabled)
        {
            await StartInterception();
        }
        else
        {
            StopInterception();
        }
    }

    private void OnNotificationReceived(object? sender, Models.InterceptedNotification data)
    {
        // Track known apps for Settings UI
        if (!string.IsNullOrEmpty(data.AppName) &&
            !_configManager.Config.KnownApps.Contains(data.AppName))
        {
            _configManager.Config.KnownApps.Add(data.AppName);
            _configManager.Save();
        }

        // Dispatch to UI thread with error handling
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_historyManager.Add(data))
                    _floatingIcon?.AnimateNotificationIn();

                if (_configManager.Config.ShowCustomToasts)
                    _toastManager.ShowToast(data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Error showing toast: {ex.Message}");
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Clean up: restore native banners, close floating icon
        HideFloatingIcon();
        _suppressor?.Dispose();
        _listener?.Dispose();
        _trayManager?.Dispose();

        base.OnExit(e);
    }
}
