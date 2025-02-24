using System.Text;
using ByteDev.Sonos;
using ByteDev.Sonos.Models;
using ByteDev.Sonos.Upnp;
using SonosControl.DAL.Interfaces;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ByteDev.Sonos.Upnp.Services;
using System;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json.Linq;
using System.Xml;



namespace SonosControl.DAL.Repos
{
    public class SonosConnectorRepo : ISonosConnectorRepo
    {
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

        public async Task StartPlaying(string ip)
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
            catch { }

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

        private static readonly HttpClient HttpClient = new();

        public async Task SetTuneInStationAsync(string ip, string stationUri)
        {
            string soapRequest = $@"
            <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""
                         s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
              <s:Body>
                <u:SetAVTransportURI xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
                  <InstanceID>0</InstanceID>
                  <CurrentURI>x-rincon-mp3radio://{stationUri}</CurrentURI>
                  <CurrentURIMetaData></CurrentURIMetaData>
                </u:SetAVTransportURI>
              </s:Body>
            </s:Envelope>";

            var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            string url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"");

            try
            {
                var response = await HttpClient.PostAsync(url, content);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Station set successfully!");
                }
                else
                {
                    Console.WriteLine($"Error setting station: {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception setting TuneIn station: {ex.Message}");
            }
        }

        public async Task<string> GetCurrentTrackAsync(string ip)
        {
            using var httpClient = new HttpClient();
            var url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";

            var soapEnvelope = @"
                <s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/' 
                            s:encodingStyle='http://schemas.xmlsoap.org/soap/encoding/'>
                    <s:Body>
                        <u:GetPositionInfo xmlns:u='urn:schemas-upnp-org:service:AVTransport:1'>
                            <InstanceID>0</InstanceID>
                        </u:GetPositionInfo>
                    </s:Body>
                </s:Envelope>";

            var content = new StringContent(soapEnvelope);
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetPositionInfo\"");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");

            try
            {
                var response = await httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();

                var titleMatch = Regex.Match(responseBody, @"<TrackMetaData>.*?<dc:title>(.*?)</dc:title>", RegexOptions.Singleline);
                var artistMatch = Regex.Match(responseBody, @"<dc:creator>(.*?)</dc:creator>", RegexOptions.Singleline);

                var title = titleMatch.Success ? titleMatch.Groups[1].Value : "Unknown Track";
                var artist = artistMatch.Success ? artistMatch.Groups[1].Value : "Unknown Artist";

                return $"{artist} - {title}";
            }
            catch (Exception ex)
            {
                return $"Error fetching track: {ex.Message}";
            }
        }

        public async Task<string> GetCurrentStationAsync(string ip)
        {
            using var client = new HttpClient();

            try
            {
                var url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";
                var content = new StringContent(
                    @"<?xml version=""1.0"" encoding=""utf-8""?>
                    <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""
                        s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
                        <s:Body>
                            <u:GetMediaInfo xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
                                <InstanceID>0</InstanceID>
                            </u:GetMediaInfo>
                        </s:Body>
                    </s:Envelope>", Encoding.UTF8, "text/xml");

                content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
                content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetMediaInfo\"");

                var response = await client.PostAsync(url, content);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync();

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

        public async Task<string?> SearchSpotifyTrackAsync(string query, string accessToken)
        {
            var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=1";
            HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await HttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            var trackUri = doc.RootElement
                .GetProperty("tracks")
                .GetProperty("items")[0]
                .GetProperty("uri")
                .GetString();

            return trackUri;
        }

        public async Task PlaySpotifyTrackAsync(string ip, string spotifyUrl)
        {
            var trackMatch = Regex.Match(spotifyUrl, @"track/(?<trackId>[\w\d]+)");
            var playlistMatch = Regex.Match(spotifyUrl, @"playlist/(?<playlistId>[\w\d]+)");
            var albumMatch = Regex.Match(spotifyUrl, @"album/(?<albumId>[\w\d]+)"); // Add regex for album

            string? rinconId = await GetRinconIdAsync(ip);
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

            await SetTuneInStationAsync(ip, "web.radio.antennevorarlberg.at/av-live/stream/mp3");

            // Build SOAP request
            string soapRequest = $@"
            <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" 
                         s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
              <s:Body>
                <u:SetAVTransportURI xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
                  <InstanceID>0</InstanceID>
                  <CurrentURI>{sonosUri}</CurrentURI>
                  <CurrentURIMetaData>{System.Security.SecurityElement.Escape(metadata)}</CurrentURIMetaData>
                </u:SetAVTransportURI>
              </s:Body>
            </s:Envelope>";

            using var client = new HttpClient();
            var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"");

            string url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";
            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error setting Spotify playback: {response.ReasonPhrase}");
            }
            else
            {
                Console.WriteLine("Spotify playback started.");
            }

            await StartPlaying(ip);
        }


        private async Task<string?> GetRinconIdAsync(string ip)
        {
            try
            {
                string url = $"http://{ip}:1400/xml/device_description.xml";

                using var client = new HttpClient();
                var response = await client.GetStringAsync(url);

                // Extract the RINCON ID from the UDN field
                var match = Regex.Match(response, @"<UDN>uuid:RINCON_([A-F0-9]+)</UDN>");
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

        public async Task ClearQueue(string ip)
        {
            using var httpClient = new HttpClient();
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

            var content = new StringContent(soapEnvelope);
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#RemoveAllTracksFromQueue\"");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");

            try
            {
                var response = await httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                Console.WriteLine("Queue cleared successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error clearing queue: {ex.Message}");
            }
        }


        public async Task<List<string>> GetQueue(string ip)
        {
            var queue = new List<string>();

            using var httpClient = new HttpClient();
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

            var content = new StringContent(soapEnvelope);
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");

            try
            {
                var response = await httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();

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


        public async Task PreviousTrack(string ip)
        {
            await PausePlaying(ip);
            await Task.Delay(500); // Small delay to ensure command is processed

            await SendAvTransportCommand(ip, "Previous");
        }

        public async Task NextTrack(string ip)
        {
            await SendAvTransportCommand(ip, "Next");
        }

        private async Task SendAvTransportCommand(string ip, string action)
        {
            using var httpClient = new HttpClient();
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

            var content = new StringContent(soapEnvelope);
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", $"\"urn:schemas-upnp-org:service:AVTransport:1#{action}\"");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");

            try
            {
                var response = await httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                Console.WriteLine($"{action} command sent successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending {action} command: {ex.Message}");
            }
        }


        private async Task<string> SendSoapRequest(string url, string soapRequest, string soapAction)
        {
            using var client = new HttpClient();
            var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", $"\"{soapAction}\"");

            var response = await client.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            else
            {
                Console.WriteLine($"Error: {response.ReasonPhrase}");
                return $"Error: {response.ReasonPhrase}";
            }
        }

    }
}
