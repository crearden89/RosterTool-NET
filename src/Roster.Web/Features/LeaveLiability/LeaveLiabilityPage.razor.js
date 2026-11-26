// Port of the browser parts of src/app/(app)/admin/leave-liability: LeaveClient.tsx
// (the views bar keeps the chosen view in sight), LeaveCalendar.tsx (the grid opens
// at today's date) and LiabilityTrend.tsx (the chart is drawn at its measured width).

/** On a phone the views bar scrolls sideways: keep the chosen view in sight. */
export function keepTabInView(bar) {
  const on = bar?.querySelector('button[aria-pressed="true"]');
  if (!bar || !on) return;
  if (on.offsetLeft < bar.scrollLeft || on.offsetLeft + on.offsetWidth > bar.scrollLeft + bar.clientWidth) {
    bar.scrollLeft = Math.max(0, on.offsetLeft + on.offsetWidth - bar.clientWidth + 8);
  }
}

/** The grid opens at today's date: on a phone only a week shows at once. */
export function scrollToToday(box) {
  const day = box?.querySelector("[data-today]");
  const name = box?.querySelector("th");
  if (box && day && name) box.scrollLeft = Math.max(0, day.offsetLeft - name.offsetWidth - 4);
}

/**
 * Reports the element's content width now and whenever it changes, so the SVG's
 * coordinate space stays 1:1 with its rendered width. Returns a handle to dispose.
 */
export function observeWidth(el, target) {
  const ro = new ResizeObserver((entries) => {
    const w = entries[0]?.contentRect.width;
    if (w) target.invokeMethodAsync("SetWidth", w);
  });
  ro.observe(el);
  return { dispose: () => ro.disconnect() };
}
