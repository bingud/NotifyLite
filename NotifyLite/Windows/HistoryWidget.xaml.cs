using NotifyLite.Helpers;
using NotifyLite.Managers;
using NotifyLite.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NotifyLite.Windows;

/// <summary>
/// Popup notification history widget. Shows past notifications with clear/dismiss.
/// Closes on click-away (Deactivated event).
/// Hidden from Alt+Tab and Win+Tab task switcher.
/// </summary>
public partial class HistoryWidget : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.HideFromTaskSwitcher(this);
    }

    private readonly NotificationHistoryManager _historyManager;
    private readonly ConfigManager _configManager;
    private readonly Window? _ownerIcon;
    private readonly EventHandler _countChangedHandler;
    private Native.LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook;
    private bool _armed;
    private bool _isClosing;

    public HistoryWidget(NotificationHistoryManager historyManager, ConfigManager configManager, Window? ownerIcon = null)
    {
        InitializeComponent();
        _historyManager = historyManager;
        _configManager = configManager;
        _ownerIcon = ownerIcon;

        _countChangedHandler = (_, _) => Dispatcher.BeginInvoke(RefreshList);
        _historyManager.CountChanged += _countChangedHandler;
        SizeChanged += Window_SizeChanged;
        Closed += (_, _) =>
        {
            _historyManager.CountChanged -= _countChangedHandler;
            UnhookMouse();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshList();
        InstallMouseHook();

        // Tray clicks steal focus immediately. Re-take foreground after that
        // so later click-away can raise Deactivated.
        try
        {
            await Task.Delay(80);
            if (_isClosing) return;
            Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            _armed = true;
        }
        catch
        {
            _armed = true;
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged && _ownerIcon != null)
        {
            var workArea = SystemParameters.WorkArea;
            var newTop = _ownerIcon.Top;
            
            if (newTop < workArea.Top + 10)
                newTop = workArea.Top + 10;
            if (newTop + ActualHeight > workArea.Bottom - 10)
                newTop = workArea.Bottom - ActualHeight - 10;
                
            Top = newTop;
        }
    }

    private void RefreshList()
    {
        NotificationList.Children.Clear();

        var notifications = _historyManager.Notifications.ToList();

        if (notifications.Count == 0)
        {
            EmptyText.Visibility = System.Windows.Visibility.Visible;
            ClearAllBtn.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        EmptyText.Visibility = System.Windows.Visibility.Collapsed;
        ClearAllBtn.Visibility = System.Windows.Visibility.Visible;

        foreach (var notif in notifications)
        {
            var card = CreateNotificationCard(notif);
            NotificationList.Children.Add(card);
        }

        Dispatcher.BeginInvoke(MarkVisibleAsRead, DispatcherPriority.Loaded);
    }

    private void HistoryScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
            MarkVisibleAsRead();
    }

    /// <summary>Count cards actually on screen as read and drop them from the tray badge.</summary>
    private void MarkVisibleAsRead()
    {
        if (_isClosing || HistoryScroll == null || NotificationList.Children.Count == 0)
            return;

        var viewport = HistoryScroll.ViewportHeight;
        if (viewport <= 0)
            viewport = HistoryScroll.ActualHeight;
        if (viewport <= 0 || double.IsNaN(viewport) || double.IsInfinity(viewport))
            return;

        var toMark = new List<InterceptedNotification>();
        foreach (var child in NotificationList.Children.OfType<Border>())
        {
            if (child.Tag is not InterceptedNotification notif || !notif.IsUnread)
                continue;
            if (child.ActualHeight <= 0)
                continue;

            GeneralTransform transform;
            try { transform = child.TransformToAncestor(HistoryScroll); }
            catch (InvalidOperationException) { continue; }

            var top = transform.Transform(new System.Windows.Point(0, 0)).Y;
            var bottom = top + child.ActualHeight;
            var visible = Math.Min(bottom, viewport) - Math.Max(top, 0.0);
            if (visible >= Math.Min(child.ActualHeight * 0.4, 20))
                toMark.Add(notif);
        }

        if (toMark.Count > 0)
            _historyManager.MarkRead(toMark);
    }

    private Border CreateNotificationCard(InterceptedNotification notif)
    {
        var card = new Border
        {
            Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#2B2B2B")),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(8, 3, 8, 3),
            Padding = new Thickness(10, 8, 8, 8),
            Cursor = Cursors.Hand,
            Tag = notif,
            ToolTip = string.IsNullOrEmpty(notif.AppUserModelId)
                ? notif.AppName
                : $"{notif.AppName}\n{notif.AppUserModelId}"
        };

        card.MouseEnter += (s, _) =>
            ((Border)s!).Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#333333"));
        card.MouseLeave += (s, _) =>
            ((Border)s!).Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#2B2B2B"));
        card.MouseLeftButtonUp += Card_Click;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Content
        var content = new StackPanel();

        // App name + time
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };

        var appName = new TextBlock
        {
            Text = notif.AppName ?? "Unknown",
            FontSize = 16,
            Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#999999")),
            FontWeight = FontWeights.SemiBold
        };
        DockPanel.SetDock(appName, Dock.Left);
        header.Children.Add(appName);

        var time = new TextBlock
        {
            Text = FormatTime(notif.Timestamp),
            FontSize = 16,
            Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#888888")),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        DockPanel.SetDock(time, Dock.Right);
        header.Children.Add(time);

        content.Children.Add(header);

        // Title
        if (!string.IsNullOrEmpty(notif.Title))
        {
            content.Children.Add(new TextBlock
            {
                Text = notif.Title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF")),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 300
            });
        }

        // Body
        if (!string.IsNullOrEmpty(notif.Body))
        {
            content.Children.Add(new TextBlock
            {
                Text = notif.Body,
                FontSize = 16,
                Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxWidth = 300
            });
        }

        Grid.SetColumn(content, 0);
        grid.Children.Add(content);

        // Close button
        var closeBtn = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(2),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = notif
        };
        closeBtn.Child = new TextBlock
        {
            Text = "\u2715",
            FontSize = 16,
            Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#666")),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeBtn.MouseEnter += (s, _) =>
            ((Border)s!).Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#3D3D3D"));
        closeBtn.MouseLeave += (s, _) =>
            ((Border)s!).Background = Brushes.Transparent;
        closeBtn.MouseLeftButtonUp += CloseItem_Click;

        Grid.SetColumn(closeBtn, 1);
        grid.Children.Add(closeBtn);

        card.Child = grid;
        return card;
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is InterceptedNotification notif)
        {
            // Try to launch the app
            if (!string.IsNullOrEmpty(notif.AppUserModelId))
            {
                try { AppLauncher.TryLaunch(notif.AppUserModelId); }
                catch (Exception ex) { Debug.WriteLine($"[HistoryWidget] Launch failed: {ex.Message}"); }
            }
        }
    }

    private void CloseItem_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // Prevent card click
        if (sender is Border border && border.Tag is InterceptedNotification notif)
        {
            _historyManager.Remove(notif);
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _historyManager.ClearAll();
        TryClose();
    }

    private void UIElement_OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer scv)
        {
            scv.ScrollToVerticalOffset(scv.VerticalOffset - (e.Delta / 3.0));
            e.Handled = true;
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_armed)
            TryClose();
    }

    private void TryClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        UnhookMouse();
        Close();
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = OnMouse;
        var module = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
    }

    private void UnhookMouse()
    {
        if (_mouseHook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 &&
            (wParam == Native.WM_LBUTTONDOWN || wParam == Native.WM_RBUTTONDOWN || wParam == Native.WM_MBUTTONDOWN))
        {
            var data = Marshal.PtrToStructure<Native.MsllHook>(lParam);
            var x = data.pt.X;
            var y = data.pt.Y;
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing) return;
                if (Native.IsTaskbarPoint(x, y)) return;
                Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect);
                if (x < rect.Left || x > rect.Right || y < rect.Top || y > rect.Bottom)
                    TryClose();
            });
        }

        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static string FormatTime(DateTime timestamp)
    {
        var diff = DateTime.Now - timestamp;
        if (diff.TotalSeconds < 60) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return timestamp.ToString("MMM d");
    }

    private static class Native
    {
        public const int WH_MOUSE_LL = 14;
        public static readonly IntPtr WM_LBUTTONDOWN = 0x0201;
        public static readonly IntPtr WM_RBUTTONDOWN = 0x0204;
        public static readonly IntPtr WM_MBUTTONDOWN = 0x0207;

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MsllHook
        {
            public Point pt;
            public int mouseData;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        public static bool IsTaskbarPoint(int x, int y)
        {
            var hwnd = WindowFromPoint(new Point { X = x, Y = y });
            if (hwnd == IntPtr.Zero) return false;
            var root = GetAncestor(hwnd, 2);
            if (root != IntPtr.Zero) hwnd = root;

            var name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow";
        }

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point pt);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
