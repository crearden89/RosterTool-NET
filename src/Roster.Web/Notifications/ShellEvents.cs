namespace Roster.Web.Notifications;

/// <summary>
/// Things a page tells the shell, within one connection: the unread count has
/// changed (the Notifications page marked everything read).
/// </summary>
public sealed class ShellEvents
{
    public event Action? UnreadChanged;

    public void RaiseUnreadChanged() => UnreadChanged?.Invoke();
}
