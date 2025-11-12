using System;

namespace SonosControl.DAL.Models;

public sealed record SonosQueueItem(int Index, string Title, string? Artist, string? Album, string? ResourceUri)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Artist)
        ? Title
        : $"{Artist} – {Title}";
}
