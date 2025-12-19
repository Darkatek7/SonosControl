## 2024-05-23 - Parallelizing Data Fetching in Blazor Components
**Learning:** Sequential await calls in periodic update loops (like `OnAfterRenderAsync` timers) can significantly increase latency, especially when dealing with external I/O (like Sonos speakers).
**Action:** Identify independent async operations and use `Task.WhenAll` to run them concurrently. In this case, `LoadCurrentStation` (selected speaker) and `UpdateSpeakerStatuses` (all speakers) were independent enough to be parallelized, reducing the total update cycle duration.
