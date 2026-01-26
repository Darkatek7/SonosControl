## 2024-05-23 - Accessible Data Tables
**Learning:** Tables with form inputs often trap screen reader users. They hear "Edit text" repeatedly without context because the column headers aren't automatically associated with the inputs in the cells.
**Action:** Always add unique `aria-label`s to inputs inside tables, incorporating the row identifier (e.g., "Monday Start Time" instead of just "Start Time").

## 2026-01-24 - Toggle-Edit Focus Management
**Learning:** When using conditional rendering (if/else blocks) to toggle between "view" and "edit" modes, focus is often lost when the DOM is updated. This forces keyboard users to navigate back to the element.
**Action:** Use the `autofocus` attribute on the input field in the "edit" block to immediately capture focus, creating a seamless transition.
