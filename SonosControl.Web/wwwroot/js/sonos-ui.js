(function () {
    "use strict";

    const ui = window.sonosUi = window.sonosUi || {};
    ui._scrollLockCount = ui._scrollLockCount || 0;
    ui.setBodyScrollLock = function (locked) {
        ui._scrollLockCount = Math.max(0, ui._scrollLockCount + (locked ? 1 : -1));
        const shouldLock = ui._scrollLockCount > 0;
        document.documentElement.classList.toggle("scroll-locked", shouldLock);
        document.body.classList.toggle("scroll-locked", shouldLock);
    };

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

    const modalStack = [];
    const inertElements = new Map();
    const editorViewports = new WeakMap();
    const modalObserver = new MutationObserver(function () {
        if (modalStack.length) updateModalBackground();
    });

    function focusableElements(container) {
        return Array.from(container.querySelectorAll(focusableSelector)).filter(function (element) {
            return !element.closest("[inert]") && element.getClientRects().length > 0
                && getComputedStyle(element).visibility !== "hidden";
        });
    }

    function updateModalBackground() {
        inertElements.forEach(function (wasInert, element) { element.inert = wasInert; });
        inertElements.clear();
        const top = modalStack[modalStack.length - 1];
        modalStack.forEach(function (trap, index) {
            const backdrop = trap.container.closest(".app-dialog-backdrop");
            if (backdrop) backdrop.style.zIndex = String(1300 + index * 10);
        });
        if (!top) return;
        for (let current = top.container; current.parentElement; current = current.parentElement) {
            Array.from(current.parentElement.children).forEach(function (sibling) {
                if (sibling !== current && sibling instanceof HTMLElement
                    && !["SCRIPT", "STYLE", "LINK"].includes(sibling.tagName)) {
                    inertElements.set(sibling, sibling.inert);
                    sibling.inert = true;
                }
            });
        }
    }

    ui.isTopFocusTrap = function (container) {
        return modalStack[modalStack.length - 1]?.container === container;
    };

    ui.activateFocusTrap = function (container, initialElement) {
        if (!container || focusTraps.has(container)) return;
        const trap = { container: container, previousFocus: document.activeElement, initialElement: initialElement };
        focusTraps.set(container, trap);
        modalStack.push(trap);
        if (modalStack.length === 1) modalObserver.observe(document.body, { childList: true, subtree: true });
        ui.setBodyScrollLock(true);
        updateModalBackground();
        window.requestAnimationFrame(function () {
            if (ui.isTopFocusTrap(container)) (initialElement || focusableElements(container)[0] || container).focus();
        });
    };

    ui.releaseFocusTrap = function (container) {
        const trap = container ? focusTraps.get(container) : null;
        if (!trap) return;
        const wasTop = ui.isTopFocusTrap(container);
        const index = modalStack.indexOf(trap);
        if (index >= 0) modalStack.splice(index, 1);
        focusTraps.delete(container);
        if (modalStack.length === 0) modalObserver.disconnect();
        const backdrop = container.closest(".app-dialog-backdrop");
        if (backdrop) backdrop.style.zIndex = "";
        ui.setBodyScrollLock(false);
        updateModalBackground();
        if (wasTop) window.requestAnimationFrame(function () {
            const top = modalStack[modalStack.length - 1];
            const previous = trap.previousFocus;
            if (previous instanceof HTMLElement && previous.isConnected && !previous.closest("[inert]")
                && (!top || top.container.contains(previous))) previous.focus();
            else if (top) (top.initialElement || focusableElements(top.container)[0] || top.container).focus();
        });
    };

    document.addEventListener("keydown", function (event) {
        const top = modalStack[modalStack.length - 1];
        if (!top || event.key !== "Tab") return;
        const elements = focusableElements(top.container);
        const first = elements[0] || top.container;
        const last = elements[elements.length - 1] || top.container;
        if (event.shiftKey && (document.activeElement === first || !top.container.contains(document.activeElement))) {
            event.preventDefault(); last.focus();
        } else if (!event.shiftKey && (document.activeElement === last || !top.container.contains(document.activeElement))) {
            event.preventDefault(); first.focus();
        }
    }, true);

    document.addEventListener("focusin", function (event) {
        const top = modalStack[modalStack.length - 1];
        if (top && !top.container.contains(event.target))
            (top.initialElement || focusableElements(top.container)[0] || top.container).focus();
    });

    ui.observeEditorViewport = function (container, reference) {
        const media = window.matchMedia("(max-width: 767.98px)");
        const changed = function () { reference.invokeMethodAsync("EditorViewportChanged", media.matches).catch(function () {}); };
        media.addEventListener("change", changed);
        editorViewports.set(container, { media: media, changed: changed });
        return media.matches;
    };

    ui.stopObservingEditorViewport = function (container) {
        const subscription = editorViewports.get(container);
        if (subscription) subscription.media.removeEventListener("change", subscription.changed);
        editorViewports.delete(container);
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
