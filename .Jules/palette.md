## 2024-05-23 - Accessible Data Tables
**Learning:** Tables with form inputs often trap screen reader users. They hear "Edit text" repeatedly without context because the column headers aren't automatically associated with the inputs in the cells.
**Action:** Always add unique `aria-label`s to inputs inside tables, incorporating the row identifier (e.g., "Monday Start Time" instead of just "Start Time").

## 2026-01-24 - Toggle-Edit Focus Management
**Learning:** When using conditional rendering (if/else blocks) to toggle between "view" and "edit" modes, focus is often lost when the DOM is updated. This forces keyboard users to navigate back to the element.
**Action:** Use the `autofocus` attribute on the input field in the "edit" block to immediately capture focus, creating a seamless transition.

## 2026-02-12 - MVC View Form Feedback
**Learning:** Standard ASP.NET MVC Views cannot rely on Blazor's `@onclick` state management. For simple form submissions like Login, vanilla JavaScript `submit` event listeners are the most robust way to provide immediate feedback (loading spinner, disabled state) without needing complex frontend frameworks.
**Action:** Use simple script injection for loading states on MVC Views (like Login/Logout) to improve perceived performance and prevent double-submit.

## 2026-06-15 - Autofocus in Modals
**Learning:** Blazor's `autofocus` attribute only triggers on the initial render. For modals that are hidden/shown via CSS classes (e.g., `d-none`/`d-block`), `autofocus` won't work on subsequent opens.
**Action:** Conditionally render the modal content (e.g., `@if (isOpen)`) to force a re-render when the modal opens, ensuring `autofocus` triggers every time.
