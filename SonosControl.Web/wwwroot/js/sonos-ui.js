(function () {
    "use strict";

    const ui = window.sonosUi = window.sonosUi || {};
    const focusTraps = new WeakMap();
    const focusableSelector = [
        "a[href]",
        "button:not([disabled])",
        "input:not([disabled])",
        "select:not([disabled])",
        "textarea:not([disabled])",
        "[tabindex]:not([tabindex='-1'])"
    ].join(",");

    ui.focusById = function (id) {
        window.requestAnimationFrame(function () {
            document.getElementById(id)?.focus();
        });
    };

    ui.activateFocusTrap = function (container, initialElement) {
        if (!container || focusTraps.has(container)) {
            return;
        }

        const previousFocus = document.activeElement;
        const keydown = function (event) {
            if (event.key !== "Tab") {
                return;
            }

            const focusable = Array.from(container.querySelectorAll(focusableSelector))
                .filter(function (element) {
                    return !element.hasAttribute("hidden") && element.getClientRects().length > 0;
                });

            if (focusable.length === 0) {
                event.preventDefault();
                container.focus();
                return;
            }

            const first = focusable[0];
            const last = focusable[focusable.length - 1];
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        };

        container.addEventListener("keydown", keydown);
        focusTraps.set(container, { previousFocus: previousFocus, keydown: keydown });

        window.requestAnimationFrame(function () {
            const target = initialElement || container.querySelector(focusableSelector) || container;
            target.focus();
        });
    };

    ui.releaseFocusTrap = function (container) {
        const trap = container ? focusTraps.get(container) : null;
        if (!trap) {
            return;
        }

        container.removeEventListener("keydown", trap.keydown);
        focusTraps.delete(container);
        if (trap.previousFocus instanceof HTMLElement && document.contains(trap.previousFocus)) {
            window.requestAnimationFrame(function () { trap.previousFocus.focus(); });
        }
    };

    ui.closeDisclosure = function (id) {
        const disclosure = document.getElementById(id);
        if (disclosure instanceof HTMLDetailsElement) {
            disclosure.open = false;
        }
    };

    document.addEventListener("pointerdown", function (event) {
        document.querySelectorAll("details[data-disclosure][open]").forEach(function (details) {
            if (!details.contains(event.target)) {
                details.open = false;
            }
        });
    });

    document.addEventListener("focusin", function (event) {
        document.querySelectorAll("details[data-disclosure][open]").forEach(function (details) {
            if (!details.contains(event.target)) {
                details.open = false;
            }
        });
    });

    document.addEventListener("keydown", function (event) {
        if (event.key !== "Escape") {
            return;
        }

        document.querySelectorAll("details[data-disclosure][open]").forEach(function (details) {
            details.open = false;
            details.querySelector("summary")?.focus();
        });
    });
})();
