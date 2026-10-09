(function () {
    "use strict";

    const html = document.documentElement;
    const mediaQuery = window.matchMedia("(prefers-color-scheme: dark)");
    let cleanup;

    function setTheme(dark) {
        if (dark) {
            html.setAttribute("data-theme", "dark");
        } else {
            html.removeAttribute("data-theme");
        }
        html.style.colorScheme = dark ? "dark" : "light";
    }

    function applyPreference(preference) {
        if (cleanup) {
            cleanup();
            cleanup = null;
        }
        if (preference === "dark" || preference === "light") {
            setTheme(preference === "dark");
            return;
        }

        const syncWithSystem = function () { setTheme(mediaQuery.matches); };
        syncWithSystem();
        if (mediaQuery.addEventListener) {
            mediaQuery.addEventListener("change", syncWithSystem);
            cleanup = function () { mediaQuery.removeEventListener("change", syncWithSystem); };
        } else if (mediaQuery.addListener) {
            mediaQuery.addListener(syncWithSystem);
            cleanup = function () { mediaQuery.removeListener(syncWithSystem); };
        }
    }

    window.sonosTheme = window.sonosTheme || {};
    window.sonosTheme.apply = applyPreference;
    applyPreference(document.currentScript?.dataset.preference || "system");
})();
