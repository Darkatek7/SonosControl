## 2024-05-23 - Accessible Data Tables
**Learning:** Tables with form inputs often trap screen reader users. They hear "Edit text" repeatedly without context because the column headers aren't automatically associated with the inputs in the cells.
**Action:** Always add unique `aria-label`s to inputs inside tables, incorporating the row identifier (e.g., "Monday Start Time" instead of just "Start Time").

## 2024-05-24 - Blazor Conditional Input Focus
**Learning:** In Blazor, when an input is conditionally rendered (e.g., toggling an "Edit" mode), the HTML `autofocus` attribute works perfectly to capture focus immediately. This is smoother than manually calling `FocusAsync` via JS interop for simple visibility toggles.
**Action:** Use `autofocus` on conditional inputs to reduce JS interop complexity and improve keyboard flow for "Edit" actions.
