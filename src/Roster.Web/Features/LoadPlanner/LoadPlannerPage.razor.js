// Port of the browser half of src/app/load/[token]/LoadPlanner.tsx's back link and
// LoadKiosk.tsx's job chooser.

// Opened from the Floor Display's strip in a tab of its own: close it, and if the
// browser will not let the page close its tab, go to the Floor Display instead.
export function closeTab(href) {
    window.close();
    window.setTimeout(() => window.location.assign(href), 300);
}

// The kiosk's job chooser: focus the pressed chip, and close on Escape or a tap
// anywhere else. The page draws the phone layout beside the kiosk one, hidden: a
// tap there is not a tap outside this chooser.
export function chooser(root, dotnet) {
    const shown = () => root.getClientRects().length > 0;
    root.querySelector("button[aria-pressed='true']")?.focus();
    const onKey = (e) => {
        if (e.key === "Escape" && shown()) dotnet.invokeMethodAsync("CloseChooser");
    };
    const onDown = (e) => {
        const el = e.target;
        if (!shown() || root.contains(el) || el?.closest?.("[data-job]")) return;
        dotnet.invokeMethodAsync("CloseChooser");
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onDown);
    return {
        dispose() {
            document.removeEventListener("keydown", onKey);
            document.removeEventListener("pointerdown", onDown);
        },
    };
}
