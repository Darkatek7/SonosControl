using SonosControl.DAL.Models;

namespace SonosControl.DAL.Interfaces;

public interface IYouTubePlaybackService
{
    Task<YouTubePlaybackSession> PreparePlaybackAsync(
        string sourceUrl,
        YouTubePlaybackMode? playbackMode = null,
        int? preferredQueueLength = null,
        CancellationToken cancellationToken = default);
    Task ActivateSessionAsync(string sessionId, string speakerIp, CancellationToken cancellationToken = default);
    Task<YouTubePlaybackQueueItem?> GetQueueItemAsync(string sessionId, int itemIndex, CancellationToken cancellationToken = default);
    Task<YouTubePlaybackOpenResult?> OpenPlaybackAsync(string sessionId, int itemIndex = 0, CancellationToken cancellationToken = default);
    Task MaintainSessionsAsync(CancellationToken cancellationToken = default);
    Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default);
}
