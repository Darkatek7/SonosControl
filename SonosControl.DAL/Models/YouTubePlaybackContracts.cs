namespace SonosControl.DAL.Models;

public sealed class YouTubePlaybackSession
{
    public string SessionId { get; init; } = string.Empty;
    public string StreamUrl { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public bool UsesTempFile { get; init; }
}

public sealed class YouTubePlaybackOpenResult : IAsyncDisposable
{
    public Stream? Stream { get; init; }
    public string? FilePath { get; init; }
    public string ContentType { get; init; } = "audio/mpeg";
    public Func<ValueTask>? DisposeAsyncAction { get; init; }

    public ValueTask DisposeAsync()
        => DisposeAsyncAction?.Invoke() ?? ValueTask.CompletedTask;
}
