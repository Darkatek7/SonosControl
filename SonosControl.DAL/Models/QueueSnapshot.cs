namespace SonosControl.DAL.Models;

public class QueueSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Snapshot";
    public string SpeakerIp { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<QueueSnapshotItem> Items { get; set; } = new();
}

public class QueueSnapshotItem
{
    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? ResourceUri { get; set; }
}
