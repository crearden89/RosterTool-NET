// Port of app/(app)/setup/requests/redirectTarget.ts.
// Where an old Requests link lands now that day-off requests and fixed
// rosters are managed on Team Members (owner, 15/11/2026). The store comes
// along; the tab and person the old page read pick the place:
//   /setup/requests                          -> the Requests card
//   /setup/requests?tab=fixed                -> the Fixed rosters card
//   /setup/requests?tab=fixed&employee=<id>  -> that person's fixed-roster sheet
// (The old page opened a person's pattern only on its Fixed tab, so a person
// without tab=fixed lands on the Requests card, as it did.)

using System.Text;

namespace Roster.Web.Features.Team;

public static class RequestsRedirect
{
    public static string Target(string? tab = null, string? employee = null, string? store = null)
    {
        var q = new List<(string Name, string Value)>();
        if (!string.IsNullOrEmpty(store)) q.Add(("store", store));
        if (tab == "fixed" && !string.IsNullOrEmpty(employee)) q.Add(("fixed", employee));
        else q.Add(("section", tab == "fixed" ? "fixed" : "requests"));
        return "/setup/staff?" + string.Join("&", q.Select(p => $"{FormEncode(p.Name)}={FormEncode(p.Value)}"));
    }

    // URLSearchParams.toString(): application/x-www-form-urlencoded, which leaves
    // ASCII letters, digits and *-._ as they are, writes a space as +, and
    // percent-encodes every other UTF-8 byte in capitals.
    private static string FormEncode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '*' or '-' or '.' or '_') sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
