// The few things the shared components need the browser for, as an ES module
// that components import with JS interop (IJSRuntime "import", "./js/roster.js").
// Everything else is Blazor.

// ─── Keys a control handles itself ──────────────────────────────────────────
// Blazor cannot cancel a key's default action for some keys and not others, so
// an element lists the keys it handles in data-prevent-keys
// ("ArrowLeft ArrowRight Home End"), and their default action (scrolling the
// page, submitting the form) is cancelled here. Blazor still gets the event.
document.addEventListener(
  "keydown",
  (e) => {
    const el = e.target instanceof Element ? e.target.closest("[data-prevent-keys]") : null;
    if (el && el.getAttribute("data-prevent-keys").split(" ").includes(e.key)) e.preventDefault();
  },
  true,
);

/** Nothing to do: importing the module installs the key handling above. */
export function ready() {}

/** Sets a cookie for the whole app, e.g. the remembered store. */
export function setCookie(name, value, maxAgeSeconds) {
  document.cookie = `${encodeURIComponent(name)}=${encodeURIComponent(value)}; path=/; max-age=${maxAgeSeconds}; samesite=lax`;
}

/** Calls dotnet[method]() when `key` is pressed anywhere; dispose() stops. */
export function onKey(key, dotnet, method) {
  const handler = (e) => {
    if (e.key === key) dotnet.invokeMethodAsync(method);
  };
  window.addEventListener("keydown", handler);
  return { dispose: () => window.removeEventListener("keydown", handler) };
}

// ─── Side sheet: focus in, Tab kept inside, Escape closes, focus back ───────
const FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** Traps focus in `sheet` until dispose(), which gives focus back to the opener. */
export function sideSheet(sheet, dotnet) {
  const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  sheet.focus();
  const onKey = (e) => {
    if (e.key === "Escape") {
      e.preventDefault();
      dotnet.invokeMethodAsync("CloseFromScript");
      return;
    }
    if (e.key !== "Tab") return;
    const items = [...sheet.querySelectorAll(FOCUSABLE)].filter((el) => el.offsetParent !== null);
    if (items.length === 0) return;
    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;
    // Focus left the sheet (the element that held it went away): bring it back.
    if (!sheet.contains(active)) {
      e.preventDefault();
      (e.shiftKey ? last : first).focus();
      return;
    }
    if (e.shiftKey && (active === first || active === sheet)) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && active === last) {
      e.preventDefault();
      first.focus();
    }
  };
  document.addEventListener("keydown", onKey);
  return {
    dispose: () => {
      document.removeEventListener("keydown", onKey);
      if (opener?.isConnected) opener.focus();
    },
  };
}

// ─── Name combobox: the open list, placed under (or over) its input ─────────
const LIST_MAX_H = 224; // matches max-h-56

/**
 * Places the list `listId` (rendered at the foot of the page, out of any
 * scrolling or sticky container) against `box`, keeps it there on scroll and
 * resize, and calls dotnet.CloseFromScript() on a press outside both.
 */
export function comboList(box, listId, dotnet) {
  const list = () => document.getElementById(listId);
  const place = () => {
    const el = list();
    if (!el) return;
    const r = box.getBoundingClientRect();
    // Open upward when the viewport below can't fit the list.
    const below = window.innerHeight - r.bottom;
    const top = below < LIST_MAX_H + 8 && r.top > LIST_MAX_H ? r.top - LIST_MAX_H - 4 : r.bottom + 4;
    el.style.top = `${top}px`;
    el.style.left = `${r.left}px`;
    el.style.width = `${Math.max(r.width, 190)}px`;
    el.style.visibility = "visible";
  };
  const onDown = (e) => {
    const el = list();
    if (!box.contains(e.target) && !(el && el.contains(e.target))) dotnet.invokeMethodAsync("CloseFromScript");
  };
  const onScroll = (e) => {
    const el = list();
    if (el && el.contains(e.target)) return;
    place();
  };
  document.addEventListener("mousedown", onDown);
  window.addEventListener("scroll", onScroll, true);
  window.addEventListener("resize", place);
  place();
  return {
    place,
    dispose: () => {
      document.removeEventListener("mousedown", onDown);
      window.removeEventListener("scroll", onScroll, true);
      window.removeEventListener("resize", place);
    },
  };
}

/** Scrolls an option into view inside its list. */
export function scrollIntoView(id) {
  document.getElementById(id)?.scrollIntoView({ block: "nearest" });
}
