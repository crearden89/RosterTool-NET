using Microsoft.JSInterop;

namespace Roster.Web.Components.Ui;

/// <summary>
/// The browser helpers in wwwroot/js/roster.js, for the shared components. One
/// per connection; the module is imported on first use. Call it from
/// OnAfterRenderAsync (there is no browser while a page prerenders).
/// </summary>
public sealed class RosterJs(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;

    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/roster.js").AsTask();

    /// <summary>Loads the module, which installs the data-prevent-keys handling.</summary>
    public async ValueTask ReadyAsync() => await (await Module).InvokeVoidAsync("ready");

    public async ValueTask SetCookieAsync(string name, string value, TimeSpan maxAge) =>
        await (await Module).InvokeVoidAsync("setCookie", name, value, (int)maxAge.TotalSeconds);

    /// <summary>The browser's own confirm box: true for OK.</summary>
    public ValueTask<bool> ConfirmAsync(string message) => js.InvokeAsync<bool>("confirm", message);

    /// <summary>Calls <paramref name="method"/> on <paramref name="target"/> when <paramref name="key"/> is pressed; dispose the result to stop.</summary>
    public async ValueTask<IJSObjectReference> OnKeyAsync<T>(string key, DotNetObjectReference<T> target, string method) where T : class =>
        await (await Module).InvokeAsync<IJSObjectReference>("onKey", key, target, method);

    internal async ValueTask<IJSObjectReference> SideSheetAsync<T>(Microsoft.AspNetCore.Components.ElementReference sheet, DotNetObjectReference<T> target) where T : class =>
        await (await Module).InvokeAsync<IJSObjectReference>("sideSheet", sheet, target);

    internal async ValueTask<IJSObjectReference> ComboListAsync<T>(Microsoft.AspNetCore.Components.ElementReference box, string listId, DotNetObjectReference<T> target) where T : class =>
        await (await Module).InvokeAsync<IJSObjectReference>("comboList", box, listId, target);

    internal async ValueTask ScrollIntoViewAsync(string id) => await (await Module).InvokeVoidAsync("scrollIntoView", id);

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try { await _module.Result.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}

internal static class JsHandles
{
    /// <summary>Disposes a handle a helper returned, ignoring a connection already gone.</summary>
    public static async ValueTask DisposeQuietlyAsync(this IJSObjectReference? handle)
    {
        if (handle is null) return;
        try
        {
            await handle.InvokeVoidAsync("dispose");
            await handle.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (TaskCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
