using SonosControl.DAL.Interfaces;

namespace SonosControl.Web.Services;

public sealed class YouTubePlaybackMaintenanceService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public YouTubePlaybackMaintenanceService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var playbackService = scope.ServiceProvider.GetRequiredService<IYouTubePlaybackService>();
            await playbackService.MaintainSessionsAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }
}
