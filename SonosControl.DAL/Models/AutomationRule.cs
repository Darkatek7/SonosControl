namespace SonosControl.DAL.Models;

public enum AutomationTriggerType
{
    None = 0,
    SourceFailure = 1,
    DeviceOffline = 2
}

public enum AutomationActionType
{
    None = 0,
    ApplyScene = 1,
    PlayFallbackSource = 2
}

public class AutomationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Rule";
    public bool Enabled { get; set; } = true;
    public AutomationTriggerType TriggerType { get; set; }
    public AutomationActionType ActionType { get; set; }
    public string? SceneId { get; set; }
    public string? FallbackUrl { get; set; }
    public SceneSourceType FallbackSourceType { get; set; } = SceneSourceType.None;
    public int RetryCount { get; set; } = 1;
    public int RetryDelaySeconds { get; set; } = 5;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
}
