## Palette Journal

## 2025-05-21 - [Async Feedback in Blazor]
**Learning:** Users lack feedback during long-running network operations like multi-speaker sync, leading to potential "button mashing" and double submissions. Blazor's server-side nature makes this latency variable.
**Action:** Consistently apply the "loading state pattern" (boolean flag + `try...finally` + Bootstrap spinner) to all network-dependent action buttons.

## 2025-12-24 - [Loading State for Select Inputs]
**Learning:** Dropdowns that trigger async data refreshes (like context switching) are often overlooked for loading states. Users may try to change selection again before the first update completes.
**Action:** Disable the select input and show a spinner adjacent to the label to indicate the context switch is in progress.
