using System;

public class Program
{
    public static void Main()
    {
        var savedUrl = "http://0n-reggae.radionetz.de/0n-reggae.mp3";
        var rawPlayingUrl = "x-rincon-mp3radio://0n-reggae.radionetz.de/0n-reggae.mp3";
        var currentStationUrl = rawPlayingUrl.Replace("x-rincon-mp3radio://", "").Trim();

        Console.WriteLine($"Saved URL: {savedUrl}");
        Console.WriteLine($"Current Station URL (Logic): {currentStationUrl}");

        var matchOld = currentStationUrl.Contains(savedUrl, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"Old Match Logic: {matchOld}");

        var matchNew = UrlsMatch(savedUrl, currentStationUrl);
        Console.WriteLine($"New Match Logic: {matchNew}");

        // Test case 2: slightly different
         var savedUrl2 = "http://mp3.ffh.de/radioffh/hqlivestream.mp3";
         var rawPlayingUrl2 = "x-rincon-mp3radio://mp3.ffh.de/radioffh/hqlivestream.mp3?sABC=123";
         var currentStationUrl2 = rawPlayingUrl2.Replace("x-rincon-mp3radio://", "").Trim();

         Console.WriteLine($"Saved URL 2: {savedUrl2}");
         Console.WriteLine($"Current Station URL 2: {currentStationUrl2}");
         Console.WriteLine($"Old Match Logic 2: {currentStationUrl2.Contains(savedUrl2, StringComparison.OrdinalIgnoreCase)}");
         Console.WriteLine($"New Match Logic 2: {UrlsMatch(savedUrl2, currentStationUrl2)}");

    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        return url.Replace("x-rincon-mp3radio://", "", StringComparison.OrdinalIgnoreCase)
                  .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                  .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                  .Replace("www.", "", StringComparison.OrdinalIgnoreCase)
                  .Trim('/')
                  .Trim();
    }

    private static bool UrlsMatch(string url1, string url2)
    {
        if (string.IsNullOrEmpty(url1) || string.IsNullOrEmpty(url2)) return false;
        var norm1 = NormalizeUrl(url1);
        var norm2 = NormalizeUrl(url2);
        return norm1.Contains(norm2, StringComparison.OrdinalIgnoreCase) ||
               norm2.Contains(norm1, StringComparison.OrdinalIgnoreCase);
    }
}
