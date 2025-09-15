using System.Net;

namespace SonosControl.DAL.Models
{
    public class SonosSettings
    {
        public int Volume { get; set; } = 10;
        public TimeOnly StartTime { get; set; } = new TimeOnly(6, 0);
        public TimeOnly StopTime { get; set; } = new TimeOnly(18, 0);
        public string IP_Adress { get; set; } = "10.0.0.0";
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

        public string? AutoPlayStationUrl { get; set; }
        public string? AutoPlaySpotifyUrl { get; set; }
        public bool AutoPlayRandomStation { get; set; }
        public bool AutoPlayRandomSpotify { get; set; }

        public Dictionary<DayOfWeek, DaySchedule> DailySchedules { get; set; } = new();

        public List<DayOfWeek> ActiveDays { get; set; } = new();

        public bool AllowUserRegistration { get; set; } = true;
    }
}
