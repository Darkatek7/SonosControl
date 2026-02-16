using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly IMetricsCollector _metricsCollector;
        private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DurationPersistInterval = TimeSpan.FromSeconds(60);

        // Maps Speaker IP -> Active PlaybackHistory ID
        private readonly ConcurrentDictionary<string, int> _activeSessions = new();

        // Maps Speaker IP -> Last known media signature (to detect track changes)
        private readonly ConcurrentDictionary<string, string> _lastMediaSignature = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastDurationPersistUtc = new();

        public PlaybackMonitorService(
            IServiceScopeFactory scopeFactory,
            ILogger<PlaybackMonitorService> logger,
            IMetricsCollector metricsCollector)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _metricsCollector = metricsCollector;
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

                await Task.Delay(MonitorInterval, stoppingToken);
            }
        }

        private async Task MonitorPlayback(CancellationToken token)
        {
            var cycleStartedUtc = DateTime.UtcNow;
            var speakersProcessed = 0;
            var sessionWrites = 0;

            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            try
            {
                var settings = await uow.ISettingsRepo.GetSettings();
                if (settings?.Speakers == null || !settings.Speakers.Any())
                {
                    return;
                }

                var knownStations = BuildKnownStationLookup(settings);
                var nowUtc = DateTime.UtcNow;

                foreach (var speaker in settings.Speakers)
                {
                    token.ThrowIfCancellationRequested();
                    sessionWrites += await ProcessSpeaker(speaker, uow, db, knownStations, nowUtc, token);
                    speakersProcessed++;
                }

                if (db.ChangeTracker.HasChanges())
                {
                    await db.SaveChangesAsync(token);
                }
            }
            finally
            {
                _metricsCollector.RecordPlaybackMonitorCycle(DateTime.UtcNow - cycleStartedUtc, speakersProcessed, sessionWrites);
            }
        }

        private async Task<int> ProcessSpeaker(
            SonosSpeaker speaker,
            IUnitOfWork uow,
            ApplicationDbContext db,
            IReadOnlyDictionary<string, string> knownStations,
            DateTime nowUtc,
            CancellationToken token)
        {
            var ip = speaker.IpAddress;
            var isPlaying = await uow.ISonosConnectorRepo.IsPlaying(ip);

            if (!isPlaying)
            {
                return await CloseSessionIfExists(ip, db, nowUtc, token);
            }

            // Fetch info
            var trackInfoTask = uow.ISonosConnectorRepo.GetTrackInfoAsync(ip, token);
            var stationUrlTask = uow.ISonosConnectorRepo.GetCurrentStationAsync(ip, token);

            await Task.WhenAll(trackInfoTask, stationUrlTask);

            var trackInfo = await trackInfoTask;
            var stationUrl = (await stationUrlTask) ?? string.Empty;
            var cleanStationUrl = NormalizeStationUrl(stationUrl);

            // Determine what's playing
            var trackName = string.Empty;
            var artist = string.Empty;
            var album = string.Empty;
            var mediaType = "Unknown";
            var mediaSignature = string.Empty;

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
                    // Try to match with known stations loaded once per cycle.
                    var matchedStation = MatchKnownStation(cleanStationUrl, knownStations);
                    if (!string.IsNullOrWhiteSpace(matchedStation))
                    {
                        mediaType = "Station";
                        trackName = matchedStation;
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
                    // Same track, update duration with throttling to avoid write amplification.
                    if (ShouldPersistSessionDuration(ip, nowUtc))
                    {
                        var persisted = await UpdateSessionDuration(sessionId, db, nowUtc, token);
                        if (persisted)
                        {
                            _lastDurationPersistUtc[ip] = nowUtc;
                            _metricsCollector.RecordPlaybackSessionWrite(skippedByThrottle: false);
                            return 1;
                        }
                    }
                    else
                    {
                        _metricsCollector.RecordPlaybackSessionWrite(skippedByThrottle: true);
                    }
                }
                else
                {
                    // Track changed
                    var writes = await CloseSessionIfExists(ip, db, nowUtc, token);
                    writes += await StartNewSession(ip, speaker.Name, trackName, artist, album, mediaType, mediaSignature, nowUtc, db, token);
                    return writes;
                }
            }
            else
            {
                // No active session, start new
                return await StartNewSession(ip, speaker.Name, trackName, artist, album, mediaType, mediaSignature, nowUtc, db, token);
            }

            return 0;
        }

        private bool ShouldPersistSessionDuration(string ip, DateTime nowUtc)
        {
            if (!_lastDurationPersistUtc.TryGetValue(ip, out var lastPersistedUtc))
            {
                return true;
            }

            return nowUtc - lastPersistedUtc >= DurationPersistInterval;
        }

        private async Task<int> StartNewSession(
            string ip,
            string speakerName,
            string track,
            string artist,
            string album,
            string mediaType,
            string signature,
            DateTime nowUtc,
            ApplicationDbContext db,
            CancellationToken token)
        {
            var history = new PlaybackHistory
            {
                SpeakerName = speakerName,
                TrackName = track,
                Artist = artist,
                Album = album,
                MediaType = mediaType,
                StartTime = nowUtc,
                EndTime = nowUtc,
                DurationSeconds = 0
            };

            db.PlaybackStats.Add(history);
            await db.SaveChangesAsync(token);

            _activeSessions[ip] = history.Id;
            _lastMediaSignature[ip] = signature;
            _lastDurationPersistUtc[ip] = nowUtc;
            _metricsCollector.RecordPlaybackSessionWrite(skippedByThrottle: false);

            return 1;
        }

        private async Task<bool> UpdateSessionDuration(int sessionId, ApplicationDbContext db, DateTime nowUtc, CancellationToken token)
        {
            var history = await db.PlaybackStats.FirstOrDefaultAsync(x => x.Id == sessionId, token);
            if (history == null)
            {
                return false;
            }

            history.EndTime = nowUtc;
            history.DurationSeconds = (history.EndTime.Value - history.StartTime).TotalSeconds;
            return true;
        }

        private async Task<int> CloseSessionIfExists(string ip, ApplicationDbContext db, DateTime nowUtc, CancellationToken token)
        {
            if (!_activeSessions.TryRemove(ip, out var sessionId))
            {
                return 0;
            }

            _lastMediaSignature.TryRemove(ip, out _);
            _lastDurationPersistUtc.TryRemove(ip, out _);

            var persisted = await UpdateSessionDuration(sessionId, db, nowUtc, token);
            if (persisted)
            {
                _metricsCollector.RecordPlaybackSessionWrite(skippedByThrottle: false);
                return 1;
            }

            return 0;
        }

        private static Dictionary<string, string> BuildKnownStationLookup(SonosSettings settings)
        {
            var stationLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (settings.Stations == null)
            {
                return stationLookup;
            }

            foreach (var station in settings.Stations)
            {
                if (string.IsNullOrWhiteSpace(station.Url) || string.IsNullOrWhiteSpace(station.Name))
                {
                    continue;
                }

                stationLookup.TryAdd(station.Url.Trim(), station.Name);
            }

            return stationLookup;
        }

        private static string NormalizeStationUrl(string stationUrl)
            => stationUrl.Replace("x-rincon-mp3radio://", "", StringComparison.OrdinalIgnoreCase).Trim();

        private static string? MatchKnownStation(string normalizedStationUrl, IReadOnlyDictionary<string, string> knownStations)
        {
            foreach (var station in knownStations)
            {
                if (normalizedStationUrl.Contains(station.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return station.Value;
                }
            }

            return null;
        }
    }
}
