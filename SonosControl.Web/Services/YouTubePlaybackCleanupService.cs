using SonosControl.DAL.Interfaces;

namespace SonosControl.Web.Services;

public sealed class YouTubePlaybackCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public YouTubePlaybackCleanupService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var playbackService = scope.ServiceProvider.GetRequiredService<IYouTubePlaybackService>();
            await playbackService.CleanupExpiredSessionsAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
