# Known differences from the TypeScript app

The .NET edition applies the TypeScript app's rules in the same order and gets the same figures. Parity tests run
both engines on shared inputs and require identical results: Stage 1 (1,981 days), the painter (6,251), the shaper
(1,992), the fortnight roster (67 fortnights), pay (11,205), the till (86,780), sub-tasks (988) and load (328),
all identical. The 1,010 TypeScript unit tests are ported with the same cases and expected values.

This page lists every known place where the two editions still behave differently. Some differences are deliberate,
some come from the platform, and some fix a fault in the old app, as noted. Each line says what differs, why, and
whether it can change a number. Faults found in the old app are listed in
[CURRENT-APP-FINDINGS.md](CURRENT-APP-FINDINGS.md).

## Across the app

- **Text comparison is case-insensitive.** SQL Server's default collation ignores case, so two custom rule keys that differ only in case are one key, and lists sorted by name follow SQL Server's order for names with mixed case or punctuation (PostgreSQL's order differed). Store codes sort the same either way. *Numbers: no.*
- **Database error wording.** Where the old app showed the database's own refusal (a unique key, a foreign key, a value out of range), the new one shows SQL Server's wording. Where a page can itself send a value the database would refuse (an unreadable time, a part-day count, a fraction in a whole-number field, an unknown state), the old PostgreSQL message is produced before the write, word for word. *Numbers: no.*
- **A fixed order where the old app had none.** Where the old app read rows without an ORDER BY, PostgreSQL returned them in storage order, which is not defined. The new app always asks for an order: employees by creation then id; leave by start date then id; trading hours, labour windows, break rules and rostering rules by their keys; admin and reset tasks by sort order then name; notifications and review events newest first then id; non-op items and team members by name; and so on. The same rows are read. *Numbers: only where two rows tie and the consumer is order-sensitive (for example the order the fortnight engine meets employees, or summing a store's totals in a different order, which moves the 13th decimal). None of these differences show at displayed precision.*
- **Absent JSON fields are written as null.** Where the old app left a field out of stored JSON (plan snapshots, upload error reports), the new one writes `null`. Reading either form gives the same object, and the HTTP answers still leave null store codes out. *Numbers: no.*
- **Dates and Sydney time.** "Today" on the office pages is Sydney's date, as before. Timestamps are written in Sydney time with its offset. Dates are worked out on the calendar and never from the server's time zone. In a few places the old app gave the right dates only because its host ran in UTC: the UKG export and print, the Dashboard's month table and month arrows, the Analysis range, and the Sales fortnight arrows (which ran in the browser and landed a day early in Australia). *Numbers: no; the results are what the old app gave on its UTC host.*
- **Sign-in.** People sign in through the company's single sign-on (Entra ID with Active Directory groups, or Windows Authentication on premises) instead of a username and password. There are no password boxes, and an access level from AD groups, when mapped, overrides the stored one. A development sign-in exists for local testing only and refuses to start outside Development. *Numbers: no.*
- **Kiosk pages.** Floor Display and Load Planner remain no-login links per store. A setting (`Kiosk:RequireDeviceSignIn`) can require a device sign-in instead; when it is on, the "No login needed" pill is hidden because it would be untrue. A dropped kiosk connection retries every 15 seconds. *Numbers: no.*
- **Read failures are reported, not shown as empty.** The old database client returned `data: null` on an error and most pages carried on with empty data, so a failed read could show zeros. The new app lets the error surface. Each page then says plainly that its figures couldn't be read, under its header. The old app's deliberate fallbacks are kept: the default task priority, sub-task labels, labelling a held roster on read, the printed roster's plan, audit rows and review notices, and a notification count of 0. *Numbers: a figure is never shown from a failed read.*
- **Addresses that aren't dates.** Where a page's `?date=`, `?period=`, `?month=` or `?store=` cannot be read, the page opens its default (today, the fortnight being planned, the current month, no store). The old app showed its error page, an empty day, or "undefined NaN undefined", depending on the page. A date of the right shape that runs past the month's end still runs on into the next month, as before (`2026-02-30` is 2 March). *Numbers: no.*
- **Page refreshes.** The Blazor pages re-read their data after a save where the old app refreshed the route, and some pages read their data once on the interactive render instead of twice. *Numbers: no.*
- **Scheduled jobs.** The jobs run inside the app on the same UTC schedules, and the same `/api/cron/*` endpoints remain for an outside scheduler. On a server farm, switch the scheduler on for one instance only (`Roster:Jobs:RunScheduler`). As before, a run missed while the app is down is not made up. *Numbers: no.*

## Engine

- **Trade-curve axis ticks.** JavaScript computes 10^-4 and 10^-5 one binary digit away from the true value, and .NET's power function is exact. The ticks could differ in their last bit only when the busiest share is under 0.004, which a trade curve never is. *Numbers: no.*
- **Sorting text by locale.** The old app's `localeCompare` and the new app's equivalent both use the ICU root collation (checked equal on 1,225 pairs of the app's own strings). Text in other scripts could sort differently if the two ICU versions disagree. *Numbers: no.*
- **Case-insensitive patterns.** .NET folds the rare characters "ſ" and "K" (Kelvin sign) to s and k; JavaScript without its Unicode flag does not. The patterns only ever meet ASCII text. *Numbers: no.*
- **Number formatting.** The formatter copies the app's two locales, en-US (the server default) and en-AU, which write numbers identically (checked on 5,082 vectors). *Numbers: no.*
- **Stage 1's glue.** The loader around Stage 1 is not parity-checked as a unit, because its input is the database reads. Stage 1 itself, the day checker and the benchmark split are replayed against the TypeScript and match exactly. *Numbers: none known.*
- **Rostering, till, load, sub-tasks, pay.** No differences: identical on the full parity corpus.

## Data layer

- **Dashboard totals add exactly.** PostgreSQL summed exact decimals. SQL Server's columns are floating point, so the roll-up sums as exact decimals too (0.1 + 0.2 is 0.3). The stored 90% benchmark columns hold x / 0.9 as floating point, so they can differ from PostgreSQL's in the last binary digit. *Numbers: never at displayed precision.*
- **Approving a roster day.** The day's status is updated, then inserted if missing, inside one transaction (PostgreSQL's ON CONFLICT has no exact SQL Server counterpart). A non-whole break or a time past 23:59 is refused, as before. *Numbers: no.*
- **Unused till reads are skipped.** Publishing and plan shifts no longer compute till counts that the checkout split ignored. Same output, fewer reads. *Numbers: no.*
- **Held plans are written as the signed-in person.** The old app used the service role. The same write checks run first, so nobody is refused who was allowed before. *Numbers: no.*
- **Reference rows.** The rows the old migrations inserted (positions, load types, the non-op catalogue, admin and reset tasks, the planning settings row, variance and leave settings, the paid-break factor) are in a migration of their own, so a new database has them. *Numbers: no.*
- **Schema details.** A rostered shift always has an employee (open shifts exist only inside the engine). The consecutive-day cap is a whole number of days. SQL Server's time type cannot hold 24:00, which no page can send. *Numbers: no.*
- **UKG export and print dates.** Always the fortnight's start to start + 13 days; the old app needed a UTC server for that. An impossible date of the right shape fails as before ("Invalid time value"). *Numbers: no.*
- **Feed tie-breaks.** Shifts with the same start, legacy curve rows sharing a block, the print's shifts and leave rows have a fixed tie-break, and employees "ordered by id" follow the id's text, as PostgreSQL ordered them. *Numbers: no.*
- **Upload error report.** The whole-upload failure entry stores `storeCode: null` where the old app left the key out. *Numbers: no.*
- **Upper and lower case in feeds.** Feeds use invariant case mapping rather than JavaScript's full Unicode mapping. This differs only for characters such as "ß", which no store code, job or header contains. *Numbers: no.*
- **EMSO roster check.** The old behaviour is kept: when two or more matching alerts are unread, the check counts them as none and inserts another. The cron secret is now compared in constant time. *Numbers: no.*
- **Development data.** The seeder makes six invented stores (G001 "Example Store" and five more) with invented rosters, and sets no passwords. *Numbers: n/a.*

## Pages

### Plan
- **Generate's sealed parts stay on the server.** The steps run in the page's own server connection with the same checks. The key comes from `Plan:PartSecret`, or else is made per server process; the old app refused to generate without its secret. *Numbers: no.*
- **The "lost answer" safety net is kept.** Same timings and messages, although an in-process call cannot lose its answer the way the old server action could. *Numbers: no.*
- **An unreadable `?date=`.** Shows "no plan could be generated" as before, but highlights the first day's chip, where the old app highlighted none. *Numbers: no.*

### Roster
- **Links.** Day chips and review links are relative to the app's address (the same address). *Numbers: no.*
- **Show sub-tasks.** The switch lasts for the connection, where the old app kept it per page instance. *Numbers: no.*
- **Held-roster endpoint.** For a store id that isn't an id, the endpoint answers 500 with the old database-style message, after the scope check. *Numbers: no.*

### Review and Notifications
- **Notification times** are written in Sydney time in the same en-AU format, seconds included. The old page showed the server's UTC time (a fix). *Numbers: no.*
- **Each person's notifications are their own, a super user's too:** the bell, the list, mark-all-read and the marking on opening a review. The old database rules let a super user's count, list and mark-all-read cover everyone (a fix). *Numbers: the super user's unread count is now their own.*
- **A failed notifications read** counts as 0 unread and an empty list, as before. *Numbers: no.*

### Dashboard, Analysis and Notes History
- **Labour density chart.** Bars are laid out in percentages of the chart's width, with the y-axis labels in a 30-pixel column, instead of measuring the width in the browser. Bars are separated by a 0.25-pixel line instead of a 0.5-pixel gap. *Numbers: no.*
- **Cost of leave.** Leave rows are read by employee, start date and id. When two leave entries overlap on a day, the first row's hours count; in the old app which row came first was undefined. *Numbers: only for overlapping leave entries with different day hours.*

### Team Members and Weekends
- **Store picker.** Shows only when someone sees more than one store; the old page showed it to every non-store login. *Numbers: no.*
- **List order.** A person's restricted tasks are listed in the editor's order, and leave bookings by creation time. *Numbers: no.*
- **Requests list.** Every request is listed. The old list was capped at the newest 1,000 rows (a fix). *Numbers: no.*
- **Saves happen together.** A new member and their seven availability rows are one save. "Grant all" saves the grants and the weekend-marker changes together, with the same failure message. *Numbers: no.*
- **Removing a member.** Removing someone who is on a published roster, or has non-op entries, is refused with a message (the old app hit a foreign-key error). The editor ignores the result, as before. *Numbers: no.*
- **"Today" for fixed rosters.** The browser's date once the page is interactive, as before; the first paint uses Sydney's date. *Numbers: no.*
- **Weekends: changing store or fortnight** reloads the markers and drops unsaved edits, as a navigation did. *Numbers: no.*

### Sales, Non-Op and Leave Liability
- **Fixed orders.** Leave-liability snapshots by UKG employee id, UKG leave requests by employee and start, non-op items and team members by name, entries by creation then id. *Numbers: only the summation order inside a store's totals, at the 13th decimal.*
- **Sales with no store in scope** opens on today's fortnight; the old page threw. *Numbers: no.*
- **Sales fortnight arrows** step 14 calendar days. The old arrows landed a day early in Australia (a fix). *Numbers: no.*
- **Sales store picker** shows from two stores. *Numbers: no.*
- **Non-op minutes that aren't whole.** The page shows the database's old message before saving: `invalid input syntax for type integer: "30.5"`. *Numbers: no.*
- **Team Analysis columns menu** closes on a click outside through an overlay; Escape works as before. *Numbers: no.*
- **Leave calendar** scrolls back to today when the month changes. *Numbers: no.*

### Rules and process settings
- **Add a window for several days.** The windows are added one day at a time, in the order picked (the old app added them all at once). Same rows, same first-failure message. *Numbers: no.*

### Store and system settings
- **Users and EMSO logins have no passwords** (single sign-on). An empty username is refused with "A username is required." *Numbers: no.*
- **Creating a store is one transaction.** A refusal leaves nothing behind; the old app inserted the parts separately and deleted the store again on a later failure. *Numbers: no.*
- **Deleting a non-op category** deletes its sub-categories first, in one transaction, after the same "entries depend on it" check (PostgreSQL cascaded). *Numbers: no.*
- **Deleting a load type.** The friendly "has recorded loads against it" message is chosen from either database's foreign-key wording. *Numbers: no.*
- **Order of names.** Lists ordered by name follow SQL Server's collation. Two lists whose output depends on order are fixed in code: the variance report reads stores in id order, as PostgreSQL compared ids, and the EMSO list orders usernames by character code. *Numbers: no.*
- **Uploads** call the import pipeline directly, with the same checks and messages. Choosing another file type also clears the file box. *Numbers: no.*
- **Copy link / Copy token** use the address the page is open on. *Numbers: no.*

### Floor Display and Load Planner
- **Auto-refresh.** The wall screen re-reads its data every 300 seconds over its live connection. A failed refresh keeps the screen and tries again. *Numbers: no.*
- **The Load Planner's 15-second clock** runs on the server's clock, not the browser's. *Numbers: no.*
- **Walk notes' completion date** is the completion time's UTC date, which is what the old app read off the stored text. *Numbers: no.*
