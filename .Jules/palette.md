## 2024-05-23 - Accessible Data Tables
**Learning:** Tables with form inputs often trap screen reader users. They hear "Edit text" repeatedly without context because the column headers aren't automatically associated with the inputs in the cells.
**Action:** Always add unique `aria-label`s to inputs inside tables, incorporating the row identifier (e.g., "Monday Start Time" instead of just "Start Time").
