## 2024-05-23 - Accessible Data Tables
**Learning:** Tables with form inputs often trap screen reader users. They hear "Edit text" repeatedly without context because the column headers aren't automatically associated with the inputs in the cells.
**Action:** Always add unique `aria-label`s to inputs inside tables, incorporating the row identifier (e.g., "Monday Start Time" instead of just "Start Time").

## 2026-01-24 - Toggle-Edit Focus Management
**Learning:** When using conditional rendering (if/else blocks) to toggle between "view" and "edit" modes, focus is often lost when the DOM is updated. This forces keyboard users to navigate back to the element.
**Action:** Use the `autofocus` attribute on the input field in the "edit" block to immediately capture focus, creating a seamless transition.

## 2026-02-12 - MVC View Form Feedback
**Learning:** Standard ASP.NET MVC Views cannot rely on Blazor's `@onclick` state management. For simple form submissions like Login, vanilla JavaScript `submit` event listeners are the most robust way to provide immediate feedback (loading spinner, disabled state) without needing complex frontend frameworks.
**Action:** Use simple script injection for loading states on MVC Views (like Login/Logout) to improve perceived performance and prevent double-submit.

## 2026-06-15 - Hamburger Menu Accessibility
**Learning:** Collapsible navigation menus ("hamburger menus") often lack state information. Screen reader users can find the button but don't know if the menu is open or closed, or what it controls.
**Action:** Always add `aria-expanded` (linked to state), `aria-label="Toggle navigation"`, and `aria-controls="[menu-container-id]"` to the toggle button.
