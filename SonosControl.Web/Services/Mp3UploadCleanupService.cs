namespace SonosControl.Web.Services;

public sealed class Mp3UploadCleanupService(Mp3UploadService uploads, ILogger<Mp3UploadCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                uploads.CleanupExpiredFiles(stoppingToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "MP3 cleanup failed; it will retry at the next interval.");
            }
            await Task.Delay(uploads.CleanupInterval, stoppingToken);
        }
    }
}
