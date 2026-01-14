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
    }
}
