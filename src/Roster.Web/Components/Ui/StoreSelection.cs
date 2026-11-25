// Port of the store choice in src/lib/storeScope.ts and src/app/(app)/store-scope-actions.ts:
// the store last chosen in any store picker, which every other page then opens on
// (the roster_store_id cookie). Which stores a person may see is the data layer's
// store scope; this only remembers the choice.

namespace Roster.Web.Components.Ui;

/// <summary>
/// The remembered store choice, one per connection. App.razor reads the cookie on
/// each full page load; <see cref="StorePicker"/> writes it.
/// </summary>
public sealed class StoreSelection
{
    public const string CookieName = "roster_store_id";

    /// <summary>The store last chosen, if any (it may no longer be in the person's scope).</summary>
    public Guid? Remembered { get; private set; }

    /// <summary>Takes the cookie's value at the start of a page load or a connection.</summary>
    public void Load(string? cookieValue)
    {
        if (Remembered is null && Guid.TryParse(cookieValue, out var id)) Remembered = id;
    }

    /// <summary>Remembers a choice for the rest of this connection (the picker also writes the cookie).</summary>
    public void Remember(Guid storeId) => Remembered = storeId;
}

/// <summary>A store as a <see cref="StorePicker"/> offers it: "G101 - Northside".</summary>
public sealed record StoreOption(Guid Id, string Code, string Name)
{
    public string Label => $"{Code} - {Name}";
}
