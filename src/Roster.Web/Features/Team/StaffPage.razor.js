// Port of the browser parts of src/app/(app)/setup/staff/StaffClient.tsx and
// FixedRosters.tsx: bringing a card into view on arrival, and today's date in the
// browser's own time zone.

/** Scrolls the element with this id to the top of the view. */
export function scrollToId(id) {
  document.getElementById(id)?.scrollIntoView({ block: "start" });
}

/**
 * Today in the BROWSER's timezone (the user is in their store's timezone).
 * toISOString() is the UTC date - yesterday until mid-morning in Australia.
 */
export function localToday() {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}
