using Hardcodet.Wpf.TaskbarNotification;
using NotifyLite.Helpers;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace NotifyLite.Services;

/// <summary>
/// Manages the system tray icon and its context menu.
/// Provides Enable/Disable, Theme, Auto-start, and Exit controls.
/// </summary>
public class TrayManager : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private readonly ConfigManager _configManager;
    private int _unreadCount;
    private string _status = "Custom Notifications";
    private IntPtr _iconHandle;

    /// <summary>Fired when the user toggles notification interception on/off.</summary>
    public event EventHandler<bool>? EnabledChanged;

    /// <summary>Fired when the user toggles the theme.</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>Fired when the user clicks Exit.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Fired when the user wants notification history (tray left-click or menu).</summary>
    public event EventHandler? HistoryRequested;

    public TrayManager(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>Initialize the tray icon with context menu.</summary>
    public void Initialize()
    {
        var contextMenu = CreateContextMenu();
        contextMenu.FontSize = 16;

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "NotifyLite — Custom Notifications",
            ContextMenu = contextMenu
        };
        ApplyIconAndTooltip();
        _trayIcon.TrayLeftMouseUp += (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Build the right-click context menu.</summary>
    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();

        // Enable / Disable toggle
        var enableItem = new MenuItem
        {
            Header = _configManager.Config.Enabled ? "✅ Enabled" : "⬜ Disabled",
            Tag = "enable"
        };
        enableItem.Click += (s, _) =>
        {
            _configManager.Config.Enabled = !_configManager.Config.Enabled;
            ((MenuItem)s!).Header = _configManager.Config.Enabled ? "✅ Enabled" : "⬜ Disabled";
            _configManager.Save();
            EnabledChanged?.Invoke(this, _configManager.Config.Enabled);
        };
        menu.Items.Add(enableItem);

        menu.Items.Add(new Separator());

        // Theme toggle
        var themeItem = new MenuItem
        {
            Header = _configManager.Config.Theme == "Dark" ? "🌙 Dark Theme" : "☀️ Light Theme",
            Tag = "theme"
        };
        themeItem.Click += (s, _) =>
        {
            _configManager.Config.Theme = _configManager.Config.Theme == "Dark" ? "Light" : "Dark";
            ((MenuItem)s!).Header = _configManager.Config.Theme == "Dark" ? "🌙 Dark Theme" : "☀️ Light Theme";
            _configManager.Save();
            ThemeChanged?.Invoke(this, _configManager.Config.Theme);
        };
        menu.Items.Add(themeItem);

        // Sound toggle
        var soundItem = new MenuItem
        {
            Header = _configManager.Config.SoundEnabled ? "🔔 Sound: ON" : "🔕 Sound: OFF",
            Tag = "sound"
        };
        soundItem.Click += (s, _) =>
        {
            _configManager.Config.SoundEnabled = !_configManager.Config.SoundEnabled;
            ((MenuItem)s!).Header = _configManager.Config.SoundEnabled ? "🔔 Sound: ON" : "🔕 Sound: OFF";
            _configManager.Save();
        };
        menu.Items.Add(soundItem);

        var startupItem = new MenuItem
        {
            Header = "🔄 Auto-start: …",
            Tag = "startup"
        };
        _ = RefreshStartupHeaderAsync(startupItem);
        startupItem.Click += async (s, _) =>
        {
            var currentlyEnabled = await StartupManager.IsEnabledAsync();
            var result = await StartupManager.SetEnabledAsync(!currentlyEnabled);
            ((MenuItem)s!).Header = result.Enabled ? "🔄 Auto-start: ON" : "🔄 Auto-start: OFF";
            _configManager.Config.AutoStart = result.Enabled;
            _configManager.Save();
            if (!string.IsNullOrEmpty(result.Error))
                MessageBox.Show(result.Error, "NotifyLite", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new Separator());

        var historyItem = new MenuItem { Header = "Notification history" };
        historyItem.Click += (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(historyItem);

        var customToastItem = new MenuItem
        {
            Header = _configManager.Config.ShowCustomToasts ? "Custom toasts: ON" : "Custom toasts: OFF (native)",
            Tag = "customToasts"
        };
        customToastItem.Click += (s, _) =>
        {
            _configManager.Config.ShowCustomToasts = !_configManager.Config.ShowCustomToasts;
            ((MenuItem)s!).Header = _configManager.Config.ShowCustomToasts
                ? "Custom toasts: ON"
                : "Custom toasts: OFF (native)";
            _configManager.Save();
        };
        menu.Items.Add(customToastItem);

        menu.Items.Add(new Separator());

        // Settings
        var settingsItem = new MenuItem { Header = "⚙️ Settings" };
        settingsItem.Click += (_, _) =>
        {
            // Only open one settings window at a time
            var existing = Application.Current.Windows.OfType<NotifyLite.Windows.SettingsWindow>().FirstOrDefault();
            if (existing != null)
            {
                existing.Activate();
                return;
            }
            var settingsWindow = new NotifyLite.Windows.SettingsWindow(_configManager);
            settingsWindow.Show();
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        // Exit
        var exitItem = new MenuItem { Header = "❌ Exit" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exitItem);

        return menu;
    }

    private static async Task RefreshStartupHeaderAsync(MenuItem item)
    {
        var enabled = await StartupManager.IsEnabledAsync();
        item.Dispatcher.Invoke(() =>
        {
            item.Header = enabled ? "🔄 Auto-start: ON" : "🔄 Auto-start: OFF";
        });
    }

    /// <summary>Draw the tray icon, overlaying an unread count when greater than zero.</summary>
    private Icon CreateIcon(int unread)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(System.Drawing.Color.Transparent);

        using var brush = new SolidBrush(ColorTranslator.FromHtml("#2B2B2B"));
        g.FillRectangle(brush, 2, 2, 28, 28);
        using var borderPen = new Pen(ColorTranslator.FromHtml("#3D3D3D"), 1);
        g.DrawRectangle(borderPen, 2, 2, 28, 28);

        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        using var textBrush = new SolidBrush(System.Drawing.Color.White);

        if (unread <= 0)
        {
            using var font = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold);
            g.DrawString("N", font, textBrush, new RectangleF(0, 0, 32, 32), format);
        }
        else
        {
            var label = unread > 99 ? "99" : unread.ToString();
            using var badgeBrush = new SolidBrush(System.Drawing.Color.FromArgb(200, 40, 40));
            g.FillEllipse(badgeBrush, 3, 3, 26, 26);

            var fontSize = label.Length >= 2 ? 13f : 16f;
            using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold);
            g.DrawString(label, font, textBrush, new RectangleF(0, 1, 32, 32), format);
        }

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    private void ApplyIconAndTooltip()
    {
        if (_trayIcon == null) return;

        var icon = CreateIcon(_unreadCount);
        var newHandle = icon.Handle;
        _trayIcon.Icon = icon;

        if (_iconHandle != IntPtr.Zero && _iconHandle != newHandle)
            DestroyIcon(_iconHandle);
        _iconHandle = newHandle;

        ApplyTooltip();
    }

    private void ApplyTooltip()
    {
        if (_trayIcon == null) return;
        var unread = _unreadCount > 0 ? $" · {_unreadCount} unread" : "";
        _trayIcon.ToolTipText = $"NotifyLite — {_status}{unread}";
    }

    /// <summary>Show the unread count on the tray icon. 0 restores the default "N".</summary>
    public void SetUnreadCount(int count)
    {
        var next = Math.Max(0, count);
        if (next == _unreadCount && _trayIcon?.Icon != null)
        {
            ApplyTooltip();
            return;
        }
        _unreadCount = next;
        ApplyIconAndTooltip();
    }

    /// <summary>Update the tooltip to show current status.</summary>
    public void UpdateTooltip(string status)
    {
        _status = status;
        ApplyTooltip();
    }

    public void Dispose()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Icon = null;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
