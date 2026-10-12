namespace SonosControl.DAL.Repos;

public static class SonosSourceValidator
{
    public static string? Validate(string sourceType, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "A URL or service link is required.";
        try
        {
            switch (sourceType)
            {
                case "radio":
                case "station": SonosMediaSourceBuilder.Radio(source); break;
                case "spotify": SonosMediaSourceBuilder.Spotify(source, "validation"); break;
                case "youtubemusic": SonosMediaSourceBuilder.YouTubeMusic(source, "validation"); break;
                case "youtube": YouTubeSourceUrl.Normalize(source); break;
                default: return "Choose a supported source type.";
            }
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return ex.Message.Split('\n')[0];
        }
    }
}
