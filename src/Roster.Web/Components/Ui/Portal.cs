// New: createPortal, which NameCombobox uses to escape clipping containers.

using Microsoft.AspNetCore.Components;

namespace Roster.Web.Components.Ui;

/// <summary>
/// Content drawn at the foot of the page instead of where it is declared, out
/// of any scrolling box or sticky cell that would clip or bury it (the open
/// list of a <see cref="NameCombobox"/> inside the roster grid). A
/// <see cref="Portal"/> hands its content here; the layout's
/// <see cref="PortalOutlet"/> draws it. One per connection.
/// </summary>
public sealed class PortalService
{
    private readonly List<(string Id, RenderFragment Content)> _items = [];

    /// <summary>Raised when content is added, changed or removed.</summary>
    public event Action? Changed;

    internal int Outlets { get; set; }

    /// <summary>Whether a layout draws portal content; without one, a Portal draws in place.</summary>
    public bool HasOutlet => Outlets > 0;

    public IReadOnlyList<(string Id, RenderFragment Content)> Items => _items;

    public void Set(string id, RenderFragment content)
    {
        var i = _items.FindIndex(x => x.Id == id);
        if (i >= 0) _items[i] = (id, content);
        else _items.Add((id, content));
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        if (_items.RemoveAll(x => x.Id == id) > 0) Changed?.Invoke();
    }
}
