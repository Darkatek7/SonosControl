using System.Net;

namespace SonosControl.DAL.Models
{
    public class SonosSettings
    {
        public const int CurrentSettingsSchemaVersion = 2;
        public const string DefaultNowPlayingGradientStartColor = "#0f172a";
        public const string DefaultNowPlayingGradientMidColor = "#1e3a8a";
        public const string DefaultNowPlayingGradientEndColor = "#0f766e";

        // A missing value identifies a pre-v2 settings document. Keep the legacy
        // fields below serializable for one compatibility release; only v2
        // Scenes and ScheduleWindows are consumed by the runtime scheduler.
        public int SettingsSchemaVersion { get; set; }

        public int Volume { get; set; } = 10;
        public int MaxVolume { get; set; } = 100;
        public TimeOnly StartTime { get; set; } = new TimeOnly(6, 0);
        public TimeOnly StopTime { get; set; } = new TimeOnly(18, 0);
        public string IP_Adress { get; set; } = "10.0.0.0";
        public List<SonosSpeaker> Speakers { get; set; } = new();
        public List<TuneInStation> Stations { get; set; } = new()
        {
            new TuneInStation { Name = "Antenne Vorarlberg", Url = "web.radio.antennevorarlberg.at/av-live/stream/mp3" },
            new TuneInStation { Name = "Radio V", Url = "orf-live.ors-shoutcast.at/vbg-q2a" },
            new TuneInStation { Name = "Rock Antenne Bayern", Url = "stream.rockantenne.bayern/80er-rock/stream/mp3" },
            new TuneInStation { Name = "Kronehit", Url = "onair.krone.at/kronehit.mp3" },
            new TuneInStation { Name = "Ö3", Url = "orf-live.ors-shoutcast.at/oe3-q2a" },
            new TuneInStation { Name = "Radio Paloma", Url = "www3.radiopaloma.de/RP-Hauptkanal.pls" }
        };
        public List<SpotifyObject> SpotifyTracks { get; set; } = new()
        {
            new SpotifyObject { Name = "Top 50 Global", Url = "https://open.spotify.com/playlist/37i9dQZEVXbMDoHDwVN2tF" },
            new SpotifyObject { Name = "Astroworld", Url = "https://open.spotify.com/album/41GuZcammIkupMPKH2OJ6I" }
        };

        public List<YouTubeMusicObject> YouTubeMusicCollections { get; set; } = new()
        {
            new YouTubeMusicObject { Name = "Supermix", Url = "https://music.youtube.com/playlist?list=LM" },
            new YouTubeMusicObject { Name = "Energize", Url = "https://music.youtube.com/watch?v=dQw4w9WgXcQ" }
        };

        public List<YouTubeObject> YouTubeCollections { get; set; } = new();

        public string? AutoPlayStationUrl { get; set; }
        public string? AutoPlaySpotifyUrl { get; set; }
        public string? AutoPlayYouTubeUrl { get; set; }
        public string? AutoPlayYouTubeMusicUrl { get; set; }
        public bool AutoPlayRandomStation { get; set; }
        public bool AutoPlayRandomSpotify { get; set; }
        public bool AutoPlayRandomYouTube { get; set; }
        public bool AutoPlayRandomYouTubeMusic { get; set; }

        public Dictionary<DayOfWeek, DaySchedule> DailySchedules { get; set; } = new();

        public List<HolidaySchedule> HolidaySchedules { get; set; } = new();

        public List<Scene> Scenes { get; set; } = new();
        public List<ScheduleWindow> ScheduleWindows { get; set; } = new();
        public List<DateOnly> AutomationExcludedDates { get; set; } = new();
        public List<DateOnly> AnnualAutomationExcludedDates { get; set; } = new();
        public List<AutomationRule> AutomationRules { get; set; } = new();
        public List<QueueSnapshot> QueueSnapshots { get; set; } = new();
        public List<DeviceHealthStatus> DeviceHealthStatuses { get; set; } = new();
        public JukeboxSettings Jukebox { get; set; } = new();
        public List<JukeboxSuggestion> JukeboxSuggestions { get; set; } = new();

        public List<DayOfWeek> ActiveDays { get; set; } = new();

        public bool AllowUserRegistration { get; set; } = true;

        public string? DiscordWebhookUrl { get; set; }
        public string? TeamsWebhookUrl { get; set; }

        public bool ExcludesAutomationDate(DateOnly date) =>
            AutomationExcludedDates?.Contains(date) == true
            || AnnualAutomationExcludedDates?.Any(excluded => excluded.Month == date.Month && excluded.Day == date.Day) == true;

        public void NormalizeAutomationDateExceptions()
        {
            AutomationExcludedDates ??= new();
            AnnualAutomationExcludedDates ??= new();
            ScheduleWindows ??= new();

            // Legacy holiday overrides intentionally replace a baseline schedule on
            // one date. Keep those baseline filters local so the override can play.
            var legacyOverrideDates = ScheduleWindows
                .Where(window => window.Id?.StartsWith("legacy-holiday-window-", StringComparison.Ordinal) == true
                    && window.IsEnabled && window.StartDate.HasValue && window.StartDate == window.EndDate)
                .Select(window => window.StartDate!.Value)
                .ToHashSet();

            foreach (var window in ScheduleWindows)
            {
                window.ExcludedDates ??= new();
                window.AnnualExcludedDates ??= new();
                var localOverrideDates = window.Id?.StartsWith("legacy-window-", StringComparison.Ordinal) == true
                    ? window.ExcludedDates.Where(legacyOverrideDates.Contains).ToList()
                    : new List<DateOnly>();
                AutomationExcludedDates.AddRange(window.ExcludedDates.Except(localOverrideDates));
                AnnualAutomationExcludedDates.AddRange(window.AnnualExcludedDates);
                window.ExcludedDates = localOverrideDates;
                window.AnnualExcludedDates.Clear();
            }

            AutomationExcludedDates = AutomationExcludedDates.Distinct().OrderBy(date => date).ToList();
            AnnualAutomationExcludedDates = AnnualAutomationExcludedDates
                .DistinctBy(date => (date.Month, date.Day))
                .OrderBy(date => date.Month).ThenBy(date => date.Day).ToList();
        }

        public string NowPlayingGradientStartColor { get; set; } = DefaultNowPlayingGradientStartColor;
        public string NowPlayingGradientMidColor { get; set; } = DefaultNowPlayingGradientMidColor;
        public string NowPlayingGradientEndColor { get; set; } = DefaultNowPlayingGradientEndColor;
    }
}
