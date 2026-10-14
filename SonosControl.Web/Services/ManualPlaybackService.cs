using SonosControl.DAL.Interfaces;

namespace SonosControl.Web.Services;

public sealed class ManualPlaybackService(PlaybackCommandCoordinator commands, IServiceProvider services,
    ISonosConnectorRepo connector, ILogger<ManualPlaybackService> logger)
{
    public async Task<IAsyncDisposable> BeginAsync(string speakerIp, CancellationToken cancellationToken = default,
        PlaybackCommandOrigin origin = PlaybackCommandOrigin.Manual)
        => await BeginAsync([speakerIp], cancellationToken, origin);

    public async Task<IAsyncDisposable> BeginAsync(IEnumerable<string> speakerIps,
        CancellationToken cancellationToken = default, PlaybackCommandOrigin origin = PlaybackCommandOrigin.Manual)
    {
        var requested = (speakerIps ?? throw new ArgumentNullException(nameof(speakerIps)))
            .Where(ip => !string.IsNullOrWhiteSpace(ip)).Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) throw new ArgumentException("At least one speaker address is required.", nameof(speakerIps));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var targets = await ResolveTargetsAsync(requested, cancellationToken);
            var commandLease = await commands.EnterManualAsync(targets, origin, cancellationToken);
            try
            {
                var stableTargets = await ResolveTargetsAsync(requested, cancellationToken);
                if (!targets.SetEquals(stableTargets))
                {
                    await commandLease.DisposeAsync();
                    if (attempt == 0) continue;
                    throw new InvalidOperationException("Speaker groups changed repeatedly while waiting for playback ownership. Try again.");
                }

                var timerLease = await services.GetRequiredService<SceneTimerService>().BeginSceneAsync(targets, cancellationToken);
                return new Lease(timerLease, commandLease);
            }
            catch { await commandLease.DisposeAsync(); throw; }
        }
        throw new InvalidOperationException("Unable to acquire stable speaker group ownership.");
    }

    public Task<IAsyncDisposable> BeginTargetsAsync(IEnumerable<string> speakerIps,
        CancellationToken cancellationToken = default, PlaybackCommandOrigin origin = PlaybackCommandOrigin.Manual)
        => BeginAsync(speakerIps, cancellationToken, origin);

    private async Task<HashSet<string>> ResolveTargetsAsync(IEnumerable<string> speakerIps, CancellationToken cancellationToken)
    {
        var targets = new HashSet<string>(speakerIps, StringComparer.OrdinalIgnoreCase);
        foreach (var speakerIp in targets.ToArray())
        {
            try
            {
                foreach (var member in await connector.GetAllSpeakersInGroup(speakerIp, cancellationToken) ?? [])
                    if (!string.IsNullOrWhiteSpace(member)) targets.Add(member.Trim());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not resolve the manual playback group for {SpeakerIp}.", speakerIp);
            }
        }
        return targets;
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
