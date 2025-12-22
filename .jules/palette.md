## Palette Journal

## 2025-05-21 - [Async Feedback in Blazor]
**Learning:** Users lack feedback during long-running network operations like multi-speaker sync, leading to potential "button mashing" and double submissions. Blazor's server-side nature makes this latency variable.
**Action:** Consistently apply the "loading state pattern" (boolean flag + `try...finally` + Bootstrap spinner) to all network-dependent action buttons.
