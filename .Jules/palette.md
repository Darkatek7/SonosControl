## 2025-05-18 - [Interaction Improvement: Separate Loading States]
**Learning:** Shared loading states (like `_isGrouping`) for related but distinct actions (Group vs. Ungroup) can lead to confusing UI where clicking one button causes the *other* button to show a spinner, while the clicked button only disables.
**Action:** Always verify that loading indicators appear on the *element that triggered the action*. Split shared state variables (e.g., `_isGrouping` vs. `_isUngrouping`) when necessary to provide accurate visual feedback, even if the underlying logic is related.
