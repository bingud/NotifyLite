using NotifyLite.Helpers;
using NotifyLite.Models;
using System.Collections.ObjectModel;
using System.Linq;

namespace NotifyLite.Managers;

/// <summary>
/// In-memory store for notifications, displayed in the history widget.
/// Thread-safe. FIFO with a global max and optional per-app limits.
/// </summary>
public class NotificationHistoryManager
{
    private readonly object _lock = new();
    private readonly ConfigManager _configManager;
    private const int DefaultMaxItems = 50;

    public NotificationHistoryManager(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    public ObservableCollection<InterceptedNotification> Notifications { get; } = new();

    public int Count
    {
        get { lock (_lock) return Notifications.Count; }
    }

    public int UnreadCount
    {
        get { lock (_lock) return Notifications.Count(n => n.IsUnread); }
    }

    public event EventHandler? CountChanged;
    public event EventHandler? UnreadChanged;

    /// <summary>
    /// Add a notification to history (newest first).
    /// Returns false when a history filter rejects it (max 0) so callers can skip unread UI.
    /// </summary>
    public bool Add(InterceptedNotification notification)
    {
        var maxForApp = GetMaxCountFor(notification);
        if (maxForApp <= 0)
            return false;

        notification.IsUnread = true;

        lock (_lock)
        {
            Notifications.Insert(0, notification);
            TrimApp(notification, maxForApp);

            var globalMax = _configManager.Config.HistoryMaxItems;
            if (globalMax <= 0) globalMax = DefaultMaxItems;
            while (Notifications.Count > globalMax)
                Notifications.RemoveAt(Notifications.Count - 1);
        }

        CountChanged?.Invoke(this, EventArgs.Empty);
        UnreadChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Remove a single notification.</summary>
    public void Remove(InterceptedNotification notification)
    {
        bool removed;
        lock (_lock)
        {
            removed = Notifications.Remove(notification);
        }

        if (!removed) return;
        CountChanged?.Invoke(this, EventArgs.Empty);
        UnreadChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clear all notifications.</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            Notifications.Clear();
        }
        CountChanged?.Invoke(this, EventArgs.Empty);
        UnreadChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mark notifications as read after they have been visible in the history popup.</summary>
    public void MarkRead(IEnumerable<InterceptedNotification> items)
    {
        var any = false;
        lock (_lock)
        {
            foreach (var item in items)
            {
                if (!item.IsUnread) continue;
                item.IsUnread = false;
                any = true;
            }
        }

        if (any)
            UnreadChanged?.Invoke(this, EventArgs.Empty);
    }

    private int GetMaxCountFor(InterceptedNotification notification)
    {
        var filters = _configManager.Config.HistoryFilters;
        if (filters is { Count: > 0 })
        {
            foreach (var filter in filters)
            {
                if (string.IsNullOrWhiteSpace(filter.AppName)) continue;
                if (MatchesFilter(filter.AppName, notification))
                    return Math.Max(0, filter.MaxCount);
            }
        }

        var global = _configManager.Config.HistoryMaxItems;
        return global > 0 ? global : DefaultMaxItems;
    }

    private void TrimApp(InterceptedNotification justAdded, int maxForApp)
    {
        var ofApp = Notifications.Where(n => SameApp(n, justAdded)).ToList();
        for (var i = maxForApp; i < ofApp.Count; i++)
            Notifications.Remove(ofApp[i]);
    }

    private static bool MatchesFilter(string filter, InterceptedNotification notification)
    {
        return filter.Equals(notification.AppName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(notification.AppUserModelId)
                && filter.Equals(notification.AppUserModelId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameApp(InterceptedNotification a, InterceptedNotification b)
    {
        if (!string.IsNullOrEmpty(a.AppUserModelId)
            && !string.IsNullOrEmpty(b.AppUserModelId)
            && a.AppUserModelId.Equals(b.AppUserModelId, StringComparison.OrdinalIgnoreCase))
            return true;

        return a.AppName.Equals(b.AppName, StringComparison.OrdinalIgnoreCase);
    }
}
