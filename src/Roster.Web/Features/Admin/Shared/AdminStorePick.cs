// New: a Super User page's own store switch (setSelectedStore, then the page again
// with ?store=): it asks first when the page holds unsaved changes, remembers the
// choice for every other page (the roster_store_id cookie), then opens the page on it.

using Microsoft.AspNetCore.Components;
using Roster.Web.Components.Ui;

namespace Roster.Web.Features.Admin.Shared;

public static class AdminStorePick
{
    /// <summary>
    /// True when the switch went ahead (the page is navigating); false when the
    /// person chose to stay (the caller redraws its select).
    /// </summary>
    public static async Task<bool> GoAsync(
        Guid storeId, string path, SaveScope? scope, StoreSelection selection, RosterJs js, NavigationManager navigation)
    {
        if (scope is not null && !await scope.ConfirmLeaveAsync()) return false;
        selection.Remember(storeId);
        await js.SetCookieAsync(StoreSelection.CookieName, storeId.ToString(), TimeSpan.FromDays(365));
        navigation.NavigateTo($"{path}?store={storeId}");
        return true;
    }
}
