using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services
{
    public class PlaybackMonitorService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PlaybackMonitorService> _logger;

        // Maps Speaker IP -> Active PlaybackHistory ID
        private readonly ConcurrentDictionary<string, int> _activeSessions = new();

        // Maps Speaker IP -> Last known media signature (to detect track changes)
        private readonly ConcurrentDictionary<string, string> _lastMediaSignature = new();

        public PlaybackMonitorService(IServiceScopeFactory scopeFactory, ILogger<PlaybackMonitorService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PlaybackMonitorService started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await MonitorPlayback(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in PlaybackMonitorService loop.");
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }

        private async Task MonitorPlayback(CancellationToken token)
        {
            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var settings = await uow.ISettingsRepo.GetSettings();
            if (settings?.Speakers == null || !settings.Speakers.Any())
                return;

            foreach (var speaker in settings.Speakers)
            {
                token.ThrowIfCancellationRequested();
                await ProcessSpeaker(speaker, uow, db, token);
            }
        }

        private async Task ProcessSpeaker(SonosSpeaker speaker, IUnitOfWork uow, ApplicationDbContext db, CancellationToken token)
        {
            string ip = speaker.IpAddress;
            bool isPlaying = await uow.ISonosConnectorRepo.IsPlaying(ip);

            if (!isPlaying)
            {
                await CloseSessionIfExists(ip, db);
                return;
            }

            // Fetch info
            var trackInfoTask = uow.ISonosConnectorRepo.GetTrackInfoAsync(ip, token);
            var stationUrlTask = uow.ISonosConnectorRepo.GetCurrentStationAsync(ip, token);

            await Task.WhenAll(trackInfoTask, stationUrlTask);

            var trackInfo = await trackInfoTask;
            var stationUrl = await stationUrlTask;

            // Determine what's playing
            string trackName = "";
            string artist = "";
            string album = "";
            string mediaType = "Unknown";
            string mediaSignature = "";

            if (trackInfo != null && trackInfo.IsValidMetadata())
            {
                trackName = trackInfo.Title;
                artist = trackInfo.Artist;
                album = trackInfo.Album;
                mediaType = "Track";
                // Refine media type if possible
                if (stationUrl.Contains("spotify", StringComparison.OrdinalIgnoreCase)) mediaType = "Spotify";
                else if (stationUrl.Contains("youtube", StringComparison.OrdinalIgnoreCase)) mediaType = "YouTube Music";

                mediaSignature = $"{trackName}|{artist}|{album}";
            }
            else
            {
                // Fallback to station/stream info
                 var cleanStationUrl = stationUrl?.Replace("x-rincon-mp3radio://", "").Trim() ?? "";

                 if (cleanStationUrl.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                 {
                     mediaType = "Spotify";
                     trackName = "Spotify Connect";
                 }
                 else if (cleanStationUrl.Contains("youtube", StringComparison.OrdinalIgnoreCase))
                 {
                     mediaType = "YouTube Music";
                     trackName = "YouTube Music";
                 }
                 else
                 {
                     // Try to match with known stations
                     var knownStations = (await uow.ISettingsRepo.GetSettings())?.Stations;
                     var matched = knownStations?.FirstOrDefault(s => cleanStationUrl.Contains(s.Url ?? "INVALID", StringComparison.OrdinalIgnoreCase));

                     if (matched != null)
                     {
                         mediaType = "Station";
                         trackName = matched.Name;
                         artist = "Live Stream";
                     }
                     else
                     {
                         mediaType = "Stream";
                         trackName = "Playing Stream";
                         artist = cleanStationUrl;
                     }
                 }
                 mediaSignature = $"{mediaType}|{trackName}";
            }

            // Check if we have an active session
            if (_activeSessions.TryGetValue(ip, out int sessionId))
            {
                // Check if track changed
                if (_lastMediaSignature.TryGetValue(ip, out string? lastSig) && lastSig == mediaSignature)
                {
                    // Same track, update duration
                    await UpdateSessionDuration(sessionId, db);
                }
                else
                {
                    // Track changed
                    await CloseSessionIfExists(ip, db);
                    await StartNewSession(ip, speaker.Name, trackName, artist, album, mediaType, mediaSignature, db);
                }
            }
            else
            {
                // No active session, start new
                await StartNewSession(ip, speaker.Name, trackName, artist, album, mediaType, mediaSignature, db);
            }
        }

        private async Task StartNewSession(string ip, string speakerName, string track, string artist, string album, string mediaType, string signature, ApplicationDbContext db)
        {
            var history = new PlaybackHistory
            {
                SpeakerName = speakerName,
                TrackName = track,
                Artist = artist,
                Album = album,
                MediaType = mediaType,
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow,
                DurationSeconds = 0
            };

            db.PlaybackStats.Add(history);
            await db.SaveChangesAsync();

            _activeSessions[ip] = history.Id;
            _lastMediaSignature[ip] = signature;

            // _logger.LogInformation($"Started playback session {history.Id} on {speakerName}: {track}");
        }

        private async Task UpdateSessionDuration(int sessionId, ApplicationDbContext db)
        {
            var history = await db.PlaybackStats.FindAsync(sessionId);
            if (history != null)
            {
                history.EndTime = DateTime.UtcNow;
                history.DurationSeconds = (history.EndTime.Value - history.StartTime).TotalSeconds;
                await db.SaveChangesAsync();
            }
        }

        private async Task CloseSessionIfExists(string ip, ApplicationDbContext db)
        {
            if (_activeSessions.TryRemove(ip, out int sessionId))
            {
                _lastMediaSignature.TryRemove(ip, out _);
                await UpdateSessionDuration(sessionId, db);
                // _logger.LogInformation($"Closed playback session {sessionId} on {ip}");
            }
        }
    }
}
