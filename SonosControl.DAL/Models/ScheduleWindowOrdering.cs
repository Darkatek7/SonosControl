namespace SonosControl.DAL.Models;

public static class ScheduleWindowOrdering
{
    public const int MaximumPriority = 1000;

    public static IOrderedEnumerable<ScheduleWindow> Order(IEnumerable<ScheduleWindow> windows) => windows
        .OrderBy(window => window.Priority)
        .ThenByDescending(window => window.LastModifiedUtc)
        .ThenBy(window => window.Id, StringComparer.OrdinalIgnoreCase);

    public static void AssignPriorities(IReadOnlyList<ScheduleWindow> ordered)
    {
        // More windows cannot receive distinct integer priorities in this range.
        // Reject the operation before changing anything rather than change precedence.
        if (ordered.Count > MaximumPriority + 1)
            throw new InvalidOperationException("At most 1001 schedules can be assigned distinct priorities between 0 and 1000.");
        for (var index = 0; index < ordered.Count; index++)
            ordered[index].Priority = ordered.Count <= 1 ? 0 : (int)((long)index * MaximumPriority / (ordered.Count - 1));
    }
}
