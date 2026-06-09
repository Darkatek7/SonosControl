using SonosControl.DAL.Models;

namespace SonosControl.DAL.Interfaces;

public interface IYouTubePlaybackService
{
    Task<YouTubePlaybackSession> PreparePlaybackAsync(string sourceUrl, CancellationToken cancellationToken = default);
    Task<YouTubePlaybackOpenResult?> OpenPlaybackAsync(string sessionId, CancellationToken cancellationToken = default);
    Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default);
}
