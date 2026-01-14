namespace SonosControl.DAL.Models
{
    public class SonosTrackInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string? AlbumArtUri { get; set; }
        public string? StreamContent { get; set; }

        public string GetDisplayString()
        {
            if (!string.IsNullOrWhiteSpace(StreamContent))
            {
                return StreamContent;
            }

            if (!string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist))
            {
                var title = string.IsNullOrWhiteSpace(Title) ? "Unknown Title" : Title;
                var artist = string.IsNullOrWhiteSpace(Artist) ? "Unknown Artist" : Artist;
                return $"{title} — {artist}";
            }

            return "No metadata available";
        }

        public bool IsValidMetadata()
        {
            var display = GetDisplayString();
            if (string.IsNullOrWhiteSpace(display) || display == "No metadata available") return false;

            // Filter out common technical/garbage strings from radio streams
            if (display.Contains("sonos.com", StringComparison.OrdinalIgnoreCase)) return false;
            if (display.Contains("upd-meta", StringComparison.OrdinalIgnoreCase)) return false;
            if (display.Contains("x-rincon", StringComparison.OrdinalIgnoreCase)) return false;
            if (display.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;

            // "Stream" is ambiguous, sometimes legitimate ("Live Stream"), sometimes technical.
            // But based on previous code filters, we preserve it.
            // However, previous code filtered "Stream" specifically.
            if (display.Contains("Stream", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }
    }
}
