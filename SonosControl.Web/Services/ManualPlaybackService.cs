using SonosControl.DAL.Interfaces;

namespace SonosControl.Web.Services;

public sealed class ManualPlaybackService(PlaybackCommandCoordinator commands, IServiceProvider services,
    ISonosConnectorRepo connector, ILogger<ManualPlaybackService> logger)
{
    public async Task<IAsyncDisposable> BeginAsync(string speakerIp, CancellationToken cancellationToken = default)
    {
        var commandLease = (await commands.EnterAsync(true, cancellationToken))!;
        try
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { speakerIp };
            try
            {
                foreach (var member in await connector.GetAllSpeakersInGroup(speakerIp, cancellationToken) ?? []) targets.Add(member);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not resolve the manual playback group for {SpeakerIp}.", speakerIp);
            }
            var timerLease = await services.GetRequiredService<SceneTimerService>().BeginSceneAsync(targets, cancellationToken);
            commands.TakeManualControl(targets);
            return new Lease(timerLease, commandLease);
        }
        catch { await commandLease.DisposeAsync(); throw; }
    }

    private sealed class Lease(IAsyncDisposable timers, IAsyncDisposable commands) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await timers.DisposeAsync();
            await commands.DisposeAsync();
        }
    }
}
