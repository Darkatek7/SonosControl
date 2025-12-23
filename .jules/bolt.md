## 2024-05-23 - Parallelizing Data Fetching in Blazor Components
**Learning:** Sequential await calls in periodic update loops (like `OnAfterRenderAsync` timers) can significantly increase latency, especially when dealing with external I/O (like Sonos speakers).
**Action:** Identify independent async operations and use `Task.WhenAll` to run them concurrently. In this case, `LoadCurrentStation` (selected speaker) and `UpdateSpeakerStatuses` (all speakers) were independent enough to be parallelized, reducing the total update cycle duration.

## 2025-05-24 - Parallelizing Batch Operations in UI Event Handlers
**Learning:** User-initiated batch operations (like "Sync Play" across multiple speakers) are often implemented with simple `foreach` loops containing `await`. This leads to linear latency accumulation (N * avg_response_time).
**Action:** Refactor sequential loops to LINQ `Select` + `Task.WhenAll` when the operations are independent. This provides near-instantaneous feedback for the user and reduces the total operation time to roughly `max(latency)` instead of `sum(latency)`.
