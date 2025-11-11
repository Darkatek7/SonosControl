using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using ByteDev.Sonos;
using ByteDev.Sonos.Models;
using SonosControl.DAL.Interfaces;


namespace SonosControl.DAL.Repos
{
    public class SonosConnectorRepo : ISonosConnectorRepo
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public SonosConnectorRepo(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        private HttpClient CreateClient()
        {
            return _httpClientFactory.CreateClient(nameof(SonosConnectorRepo));
        }
        public async Task PausePlaying(string ip)
        {
            SonosController controller = new SonosControllerFactory().Create(ip);
            await controller.PauseAsync();
        }

        public async Task StopPlaying(string ip)
        {
            SonosController controller = new SonosControllerFactory().Create(ip);
            await controller.StopAsync();
        }

        public virtual async Task StartPlaying(string ip)
        {
            SonosController controller = new SonosControllerFactory().Create(ip);
            await controller.PlayAsync();
        }

        public async Task<bool> IsPlaying(string ip)
        {
            SonosController controller = new SonosControllerFactory().Create(ip);
            bool result = false;

            try
            {
                result = await controller.GetIsPlayingAsync();
            }
            catch
            {
            }

            return result;
        }

        public async Task<int> GetVolume(string ip)
        {
            SonosController controller = new SonosControllerFactory().Create(ip);
            var volume = await controller.GetVolumeAsync();
            return volume.Value;
        }

        public async Task SetVolume(string ip, int volume)
        {
            SonosController controller = new SonosControllerFactory().Create(ip);
            SonosVolume sonosVolume = new SonosVolume(volume);
            await controller.SetVolumeAsync(sonosVolume);
        }

        public virtual async Task SetTuneInStationAsync(string ip, string stationUri, CancellationToken cancellationToken = default)
        {
            stationUri = stationUri
                .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                .Replace("http://", "", StringComparison.OrdinalIgnoreCase);

            // Decide which URI to send:
            //  - If the stationUri already contains "://", use it as-is.
            //  - Otherwise assume it's a plain TuneIn stream and prefix with x-rincon-mp3radio://
            string currentUri = stationUri.Contains("://")
                ? stationUri
                : $"x-rincon-mp3radio://{stationUri}";

            string soapRequest = $@"
    <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""
                s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
      <s:Body>
        <u:SetAVTransportURI xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
          <InstanceID>0</InstanceID>
          <CurrentURI>{currentUri}</CurrentURI>
          <CurrentURIMetaData></CurrentURIMetaData>
        </u:SetAVTransportURI>
      </s:Body>
    </s:Envelope>";

            cancellationToken.ThrowIfCancellationRequested();

            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            string url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"");

            bool success = false;

            try
            {
                var client = CreateClient();
                var response = await client.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();
                Console.WriteLine("Station set successfully!");
                success = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting station: {ex.Message}");
            }

            if (success)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await StartPlaying(ip);
            }
        }


        public async Task<string> GetCurrentTrackInfoAsync(string ip, CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";

                using var content = new StringContent(
                    @"<?xml version=""1.0"" encoding=""utf-8""?>
            <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""
                        s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
                <s:Body>
                    <u:GetPositionInfo xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
                        <InstanceID>0</InstanceID>
                    </u:GetPositionInfo>
                </s:Body>
            </s:Envelope>", Encoding.UTF8, "text/xml");

                content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
                content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetPositionInfo\"");

                var client = CreateClient();
                var response = await client.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(cancellationToken);

                // Extract TrackMetaData block
                var match = Regex.Match(xml, @"<TrackMetaData>(.*?)</TrackMetaData>", RegexOptions.Singleline);

                if (match.Success)
                {
                    var metadataXml = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value); // Unescape XML

                    // Extract the title from TrackMetaData
                    var titleMatch = Regex.Match(metadataXml, @"<dc:title>(.*?)</dc:title>");
                    var creatorMatch = Regex.Match(metadataXml, @"<dc:creator>(.*?)</dc:creator>");

                    var title = titleMatch.Success ? titleMatch.Groups[1].Value : "Unknown Title";
                    var artist = creatorMatch.Success ? creatorMatch.Groups[1].Value : "Unknown Artist";

                    return $"{title} — {artist}";
                }

                return "No metadata available";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public async Task<string> GetCurrentTrackAsync(string ip, CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";

                using var content = new StringContent(
                    @"<?xml version=""1.0"" encoding=""utf-8""?>
            <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""
                        s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
                <s:Body>
                    <u:GetPositionInfo xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
                        <InstanceID>0</InstanceID>
                    </u:GetPositionInfo>
                </s:Body>
            </s:Envelope>", Encoding.UTF8, "text/xml");

                content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
                content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetPositionInfo\"");

                var client = CreateClient();
                var response = await client.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(cancellationToken);

                // Extract <TrackMetaData> content
                var match = Regex.Match(xml, @"<TrackMetaData>(.*?)</TrackMetaData>", RegexOptions.Singleline);

                if (match.Success)
                {
                    var metadataXml = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);

                    // Try dc:title first
                    var titleMatch = Regex.Match(metadataXml, @"<dc:title>(.*?)</dc:title>");
                    var creatorMatch = Regex.Match(metadataXml, @"<dc:creator>(.*?)</dc:creator>");
                    var streamContentMatch = Regex.Match(metadataXml, @"<r:streamContent>(.*?)</r:streamContent>");

                    if (streamContentMatch.Success)
                    {
                        return streamContentMatch.Groups[1].Value; // Use this if available
                    }

                    var title = titleMatch.Success ? titleMatch.Groups[1].Value : "Unknown Title";
                    var artist = creatorMatch.Success ? creatorMatch.Groups[1].Value : "Unknown Artist";

                    return $"{title} — {artist}";
                }

                return "No metadata available";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public async Task<(TimeSpan Position, TimeSpan Duration)> GetTrackProgressAsync(string ip, CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";

                using var content = new StringContent(
                    @"<?xml version=""1.0"" encoding=""utf-8""?>
            <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""
                        s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
                <s:Body>
                    <u:GetPositionInfo xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
                        <InstanceID>0</InstanceID>
                    </u:GetPositionInfo>
                </s:Body>
            </s:Envelope>", Encoding.UTF8, "text/xml");

                content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
                content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetPositionInfo\"");

                var client = CreateClient();
                var response = await client.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(cancellationToken);

                var doc = new XmlDocument();
                doc.LoadXml(xml);

                TimeSpan.TryParse(doc.GetElementsByTagName("RelTime").Item(0)?.InnerText ?? "00:00:00", out var relTime);
                TimeSpan.TryParse(doc.GetElementsByTagName("TrackDuration").Item(0)?.InnerText ?? "00:00:00", out var trackDuration);

                return (relTime, trackDuration);
            }
            catch
            {
                return (TimeSpan.Zero, TimeSpan.Zero);
            }
        }


        public async Task<string> GetCurrentStationAsync(string ip, CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";
                using var content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                    "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    "<s:Body>" +
                    "<u:GetMediaInfo xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
                    "<InstanceID>0</InstanceID>" +
                    "</u:GetMediaInfo>" +
                    "</s:Body>" +
                    "</s:Envelope>", Encoding.UTF8, "text/xml");

                content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
                content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetMediaInfo\"");

                var client = CreateClient();
                var response = await client.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(cancellationToken);

                // Extract the station title from the XML response using Regex
                var match = Regex.Match(xml, @"<CurrentURI>(?<stationUrl>.*?)</CurrentURI>", RegexOptions.Singleline);

                if (match.Success)
                {
                    return match.Groups["stationUrl"].Value;
                }

                return "Unknown Station";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public async Task<string?> SearchSpotifyTrackAsync(string query, string accessToken, CancellationToken cancellationToken = default)
        {
            var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=1";
            var client = CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            var trackUri = doc.RootElement
                .GetProperty("tracks")
                .GetProperty("items")[0]
                .GetProperty("uri")
                .GetString();

            return trackUri;
        }

        public async Task PlaySpotifyTrackAsync(string ip, string spotifyUrl, string? fallbackStationUri = null, CancellationToken cancellationToken = default)
        {
            var trackMatch = Regex.Match(spotifyUrl, @"track/(?<trackId>[\w\d]+)");
            var playlistMatch = Regex.Match(spotifyUrl, @"playlist/(?<playlistId>[\w\d]+)");
            var albumMatch = Regex.Match(spotifyUrl, @"album/(?<albumId>[\w\d]+)"); // Add regex for album

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(fallbackStationUri))
            {
                await SetTuneInStationAsync(ip, fallbackStationUri, cancellationToken);
            }

            string? rinconId = await GetRinconIdAsync(ip, cancellationToken);
            if (rinconId == null)
            {
                Console.WriteLine("Could not retrieve RINCON ID.");
                return;
            }

            string sonosUri;
            string metadata;

            if (trackMatch.Success)
            {
                string trackId = trackMatch.Groups["trackId"].Value;
                sonosUri = $"x-sonos-vli:RINCON_{rinconId}:2,spotify:{trackId}";

                metadata = $@"<DIDL-Lite xmlns:dc=""http://purl.org/dc/elements/1.1/"" 
                                               xmlns:upnp=""urn:schemas-upnp-org:metadata-1-0/upnp/"" 
                                               xmlns=""urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"">
                                <item id=""00032020spotify%3atrack%3a{trackId}"" 
                                      parentID=""00020000spotify"" restricted=""true"">
                                    <dc:title>Spotify Track</dc:title>
                                    <upnp:class>object.item.audioItem.musicTrack</upnp:class>
                                    <desc id=""cdudn"" nameSpace=""urn:schemas-rinconnetworks-com:metadata-1-0/"">SA_RINCON2311_X_#Svc2311-0-Token</desc>
                                </item>
                             </DIDL-Lite>";
            }
            else if (playlistMatch.Success)
            {
                string playlistId = playlistMatch.Groups["playlistId"].Value;
                sonosUri = $"x-sonos-vli:RINCON_{rinconId}:2,spotify:{playlistId}";

                metadata = $@"<DIDL-Lite xmlns:dc=""http://purl.org/dc/elements/1.1/"" 
                                               xmlns:upnp=""urn:schemas-upnp-org:metadata-1-0/upnp/"" 
                                               xmlns=""urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"">
                                <item id=""00020000spotify%3aplaylist%3a{playlistId}"" 
                                      parentID=""00020000spotify"" restricted=""true"">
                                    <dc:title>Spotify Playlist</dc:title>
                                    <upnp:class>object.container.playlistContainer</upnp:class>
                                    <desc id=""cdudn"" nameSpace=""urn:schemas-rinconnetworks-com:metadata-1-0/"">SA_RINCON2311_X_#Svc2311-0-Token</desc>
                                </item>
                             </DIDL-Lite>";
            }
            else if (albumMatch.Success)
            {
                string albumId = albumMatch.Groups["albumId"].Value;
                sonosUri = $"x-sonos-vli:RINCON_{rinconId}:2,spotify:{albumId}";

                metadata = $@"<DIDL-Lite xmlns:dc=""http://purl.org/dc/elements/1.1/"" 
                                               xmlns:upnp=""urn:schemas-upnp-org:metadata-1-0/upnp/"" 
                                               xmlns=""urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"">
                                <item id=""00020000spotify%3aalbum%3a{albumId}"" 
                                      parentID=""00020000spotify"" restricted=""true"">
                                    <dc:title>Spotify Album</dc:title>
                                    <upnp:class>object.container.album.musicAlbum</upnp:class>
                                    <desc id=""cdudn"" nameSpace=""urn:schemas-rinconnetworks-com:metadata-1-0/"">SA_RINCON2311_X_#Svc2311-0-Token</desc>
                                </item>
                             </DIDL-Lite>";
            }
            else
            {
                Console.WriteLine("Invalid Spotify URL.");
                return;
            }

            // Build SOAP request
            string soapRequest = $@"
            <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""
                         s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
              <s:Body>
                <u:SetAVTransportURI xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
                  <InstanceID>0</InstanceID>
                  <CurrentURI>{sonosUri}</CurrentURI>
                  <CurrentURIMetaData>{SecurityElement.Escape(metadata)}</CurrentURIMetaData>
                </u:SetAVTransportURI>
              </s:Body>
            </s:Envelope>";

            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"");

            string url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";
            var client = CreateClient();
            var response = await client.PostAsync(url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error setting Spotify playback: {response.ReasonPhrase}");
                return;
            }

            Console.WriteLine("Spotify playback started.");

            cancellationToken.ThrowIfCancellationRequested();
            await StartPlaying(ip);
        }

        public async Task PlayYouTubeMusicTrackAsync(string ip, string youtubeMusicUrl, string? fallbackStationUri = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(fallbackStationUri))
            {
                await SetTuneInStationAsync(ip, fallbackStationUri, cancellationToken);
            }

            string trimmedUrl = youtubeMusicUrl.Trim();
            string contentType = "track";
            string? contentId = null;

            if (trimmedUrl.StartsWith("ytm:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmedUrl.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    contentType = parts[1].ToLowerInvariant();
                    contentId = parts[2];
                }
            }
            else
            {
                var playlistMatch = Regex.Match(trimmedUrl, @"[?&]list=(?<id>[A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
                var trackMatch = Regex.Match(trimmedUrl, @"[?&]v=(?<id>[A-Za-z0-9_-]{6,})", RegexOptions.IgnoreCase);

                if (playlistMatch.Success)
                {
                    contentType = "playlist";
                    contentId = playlistMatch.Groups["id"].Value;
                }
                else if (trackMatch.Success)
                {
                    contentType = "track";
                    contentId = trackMatch.Groups["id"].Value;
                }
                else
                {
                    var customMatch = Regex.Match(trimmedUrl, @"youtube(?:music)?:(?<type>track|playlist):(?<id>[A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
                    if (customMatch.Success)
                    {
                        contentType = customMatch.Groups["type"].Value.ToLowerInvariant();
                        contentId = customMatch.Groups["id"].Value;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(contentId))
            {
                Console.WriteLine("Invalid YouTube Music URL.");
                return;
            }

            var rinconId = await GetRinconIdAsync(ip, cancellationToken);
            if (rinconId == null)
            {
                Console.WriteLine("Could not retrieve RINCON ID.");
                return;
            }

            string escapedId = Uri.EscapeDataString(contentId);
            string sonosUri;
            string metadata;

            if (contentType.Equals("playlist", StringComparison.OrdinalIgnoreCase))
            {
                sonosUri = $"x-sonos-vli:RINCON_{rinconId}:4,youtubemusic:playlist:{contentId}";
                metadata = $@"<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\"
                                               xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"
                                               xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">
                                <item id=\"0006206cyoutubemusic%3aplaylist%3a{escapedId}\"
                                      parentID=\"0006206cyoutubemusic\" restricted=\"true\">
                                    <dc:title>YouTube Music Playlist</dc:title>
                                    <upnp:class>object.container.playlistContainer</upnp:class>
                                    <desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">SA_RINCON51463_X_#Svc51463-0-Token</desc>
                                </item>
                             </DIDL-Lite>";
            }
            else
            {
                contentType = "track";
                sonosUri = $"x-sonos-vli:RINCON_{rinconId}:4,youtubemusic:track:{contentId}";
                metadata = $@"<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\"
                                               xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"
                                               xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">
                                <item id=\"0004206cyoutubemusic%3atrack%3a{escapedId}\"
                                      parentID=\"0004206cyoutubemusic\" restricted=\"true\">
                                    <dc:title>YouTube Music Track</dc:title>
                                    <upnp:class>object.item.audioItem.musicTrack</upnp:class>
                                    <desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">SA_RINCON51463_X_#Svc51463-0-Token</desc>
                                </item>
                             </DIDL-Lite>";
            }

            string soapRequest = $@"
            <s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"
                         s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">
              <s:Body>
                <u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">
                  <InstanceID>0</InstanceID>
                  <CurrentURI>{sonosUri}</CurrentURI>
                  <CurrentURIMetaData>{SecurityElement.Escape(metadata)}</CurrentURIMetaData>
                </u:SetAVTransportURI>
              </s:Body>
            </s:Envelope>";

            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"");

            string url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";
            var client = CreateClient();
            var response = await client.PostAsync(url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error setting YouTube Music playback: {response.ReasonPhrase}");
                return;
            }

            Console.WriteLine("YouTube Music playback started.");

            cancellationToken.ThrowIfCancellationRequested();
            await StartPlaying(ip);
        }


        protected virtual async Task<string?> GetRinconIdAsync(string ip, CancellationToken cancellationToken = default)
        {
            try
            {
                string url = $"http://{ip}:1400/xml/device_description.xml";

                var client = CreateClient();
                using var response = await client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Extract the RINCON ID from the UDN field
                var match = Regex.Match(responseBody, @"<UDN>uuid:RINCON_([A-F0-9]+)</UDN>");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching RINCON ID: {ex.Message}");
            }

            return null;
        }

        public async Task ClearQueue(string ip, CancellationToken cancellationToken = default)
        {
            var url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";

            var soapEnvelope = @"
                <s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'
                            s:encodingStyle='http://schemas.xmlsoap.org/soap/encoding/'>
                    <s:Body>
                        <u:RemoveAllTracksFromQueue xmlns:u='urn:schemas-upnp-org:service:AVTransport:1'>
                            <InstanceID>0</InstanceID>
                        </u:RemoveAllTracksFromQueue>
                    </s:Body>
                </s:Envelope>";

            using var content = new StringContent(soapEnvelope);
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION",
                "\"urn:schemas-upnp-org:service:AVTransport:1#RemoveAllTracksFromQueue\"");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");

            try
            {
                var client = CreateClient();
                var response = await client.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();
                Console.WriteLine("Queue cleared successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error clearing queue: {ex.Message}");
            }
        }


        public async Task<List<string>> GetQueue(string ip, CancellationToken cancellationToken = default)
        {
            var queue = new List<string>();

            var url = $"http://{ip}:1400/MediaRenderer/ContentDirectory/Control";

            var soapEnvelope = @"
                <s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'
                            s:encodingStyle='http://schemas.xmlsoap.org/soap/encoding/'>
                    <s:Body>
                        <u:Browse xmlns:u='urn:schemas-upnp-org:service:ContentDirectory:1'>
                            <ObjectID>Q:0</ObjectID>
                            <BrowseFlag>BrowseDirectChildren</BrowseFlag>
                            <Filter>*</Filter>
                            <StartingIndex>0</StartingIndex>
                            <RequestedCount>100</RequestedCount>
                            <SortCriteria></SortCriteria>
                        </u:Browse>
                    </s:Body>
                </s:Envelope>";

            using var content = new StringContent(soapEnvelope);
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");

            try
            {
                var client = CreateClient();
                var response = await client.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Extract track titles using Regex
                var matches = Regex.Matches(responseBody, @"<dc:title>(.*?)</dc:title>");

                foreach (Match match in matches)
                {
                    if (match.Success)
                    {
                        queue.Add(match.Groups[1].Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching queue: {ex.Message}");
            }

            return queue;
        }


        public async Task PreviousTrack(string ip, CancellationToken cancellationToken = default)
        {
            await PausePlaying(ip);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken); // Small delay to ensure command is processed

            await SendAvTransportCommand(ip, "Previous", cancellationToken);
        }

        public async Task NextTrack(string ip, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendAvTransportCommand(ip, "Next", cancellationToken);
        }

        public async Task RebootDeviceAsync(string ip, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new ArgumentException("IP address must be provided.", nameof(ip));
            }

            var client = CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ip}:1400/reboot");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private async Task SendAvTransportCommand(string ip, string action, CancellationToken cancellationToken)
        {
            var url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";

            var soapEnvelope = $@"
                <s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'
                            s:encodingStyle='http://schemas.xmlsoap.org/soap/encoding/'>
                    <s:Body>
                        <u:{action} xmlns:u='urn:schemas-upnp-org:service:AVTransport:1'>
                            <InstanceID>0</InstanceID>
                        </u:{action}>
                    </s:Body>
                </s:Envelope>";

            using var content = new StringContent(soapEnvelope);
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", $"\"urn:schemas-upnp-org:service:AVTransport:1#{action}\"");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");

            try
            {
                var client = CreateClient();
                var response = await client.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();
                Console.WriteLine($"{action} command sent successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending {action} command: {ex.Message}");
            }
        }


        private async Task<string> SendSoapRequest(string url, string soapRequest, string soapAction, CancellationToken cancellationToken = default)
        {
            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", $"\"{soapAction}\"");

            var client = CreateClient();
            var response = await client.PostAsync(url, content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            else
            {
                Console.WriteLine($"Error: {response.ReasonPhrase}");
                return $"Error: {response.ReasonPhrase}";
            }
        }
    }
}