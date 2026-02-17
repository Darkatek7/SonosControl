using System.Net;

namespace SonosControl.DAL.Models
{
    public class SonosSettings
    {
        public const string DefaultNowPlayingGradientStartColor = "#0f172a";
        public const string DefaultNowPlayingGradientMidColor = "#1e3a8a";
        public const string DefaultNowPlayingGradientEndColor = "#0f766e";

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

        public string? AutoPlayStationUrl { get; set; }
        public string? AutoPlaySpotifyUrl { get; set; }
        public string? AutoPlayYouTubeMusicUrl { get; set; }
        public bool AutoPlayRandomStation { get; set; }
        public bool AutoPlayRandomSpotify { get; set; }
        public bool AutoPlayRandomYouTubeMusic { get; set; }

        public Dictionary<DayOfWeek, DaySchedule> DailySchedules { get; set; } = new();

        public List<HolidaySchedule> HolidaySchedules { get; set; } = new();

        public List<Scene> Scenes { get; set; } = new();
        public List<ScheduleWindow> ScheduleWindows { get; set; } = new();
        public List<AutomationRule> AutomationRules { get; set; } = new();
        public List<QueueSnapshot> QueueSnapshots { get; set; } = new();
        public List<DeviceHealthStatus> DeviceHealthStatuses { get; set; } = new();
        public JukeboxSettings Jukebox { get; set; } = new();
        public List<JukeboxSuggestion> JukeboxSuggestions { get; set; } = new();

        public List<DayOfWeek> ActiveDays { get; set; } = new();

        public bool AllowUserRegistration { get; set; } = true;

        public string? DiscordWebhookUrl { get; set; }
        public string? TeamsWebhookUrl { get; set; }

        public string NowPlayingGradientStartColor { get; set; } = DefaultNowPlayingGradientStartColor;
        public string NowPlayingGradientMidColor { get; set; } = DefaultNowPlayingGradientMidColor;
        public string NowPlayingGradientEndColor { get; set; } = DefaultNowPlayingGradientEndColor;
    }
}
