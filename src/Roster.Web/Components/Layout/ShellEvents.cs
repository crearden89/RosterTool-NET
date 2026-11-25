// New: lets a page tell the shell something within one connection.

namespace Roster.Web.Components.Layout;

/// <summary>
/// Things a page tells the shell, within one connection: the unread count has
/// changed (the Notifications page marked everything read: call RaiseUnreadChanged and the menu reads the count again).
/// </summary>
public sealed class ShellEvents
{
    public event Action? UnreadChanged;

    public void RaiseUnreadChanged() => UnreadChanged?.Invoke();
}
