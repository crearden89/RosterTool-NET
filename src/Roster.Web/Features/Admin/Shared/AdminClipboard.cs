// New: navigator.clipboard.writeText, which the TypeScript pages call without
// waiting (void): a refused copy (no permission, an insecure origin) changes nothing.

using Microsoft.JSInterop;

namespace Roster.Web.Features.Admin.Shared;

public static class AdminClipboard
{
    public static async Task WriteAsync(IJSRuntime js, string text)
    {
        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
        }
        catch (JSException)
        {
        }
    }
}
