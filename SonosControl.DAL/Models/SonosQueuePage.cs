using System.Collections.Generic;

namespace SonosControl.DAL.Models;

public sealed record SonosQueuePage(IReadOnlyList<SonosQueueItem> Items, int StartIndex, int NumberReturned, int TotalMatches)
{
    public bool HasMore => StartIndex + NumberReturned < TotalMatches;
}
