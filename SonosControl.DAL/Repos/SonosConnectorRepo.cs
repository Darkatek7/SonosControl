using static SonosControl.DAL.Repos.SonosMetadataParser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.DAL.Repos
{
    public class SonosConnectorRepo : ISonosConnectorRepo
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SonosSoapTransport _transport;
        private readonly ILogger<SonosConnectorRepo> _logger;
        private readonly ISettingsRepo _settingsRepo;
        private readonly IYouTubePlaybackService _youTubePlaybackService;

        public SonosConnectorRepo(IHttpClientFactory httpClientFactory, ISettingsRepo settingsRepo)
            : this(httpClientFactory, settingsRepo, NullYouTubePlaybackService.Instance)
        {
        }

        public SonosConnectorRepo(IHttpClientFactory httpClientFactory, ISettingsRepo settingsRepo, IYouTubePlaybackService youTubePlaybackService, SonosSoapTransport? transport = null, ILogger<SonosConnectorRepo>? logger = null)
        {
            _transport = transport ?? new SonosSoapTransport();
            _logger = logger ?? NullLogger<SonosConnectorRepo>.Instance;
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _settingsRepo = settingsRepo ?? throw new ArgumentNullException(nameof(settingsRepo));
            _youTubePlaybackService = youTubePlaybackService ?? throw new ArgumentNullException(nameof(youTubePlaybackService));
        }

        private HttpClient CreateClient()
        {
            return _httpClientFactory.CreateClient(nameof(SonosConnectorRepo));
        }

        private sealed class NullYouTubePlaybackService : IYouTubePlaybackService
        {
            public static NullYouTubePlaybackService Instance { get; } = new();

            public Task<YouTubePlaybackSession> PreparePlaybackAsync(
                string sourceUrl,
                YouTubePlaybackMode? playbackMode = null,
                int? preferredQueueLength = null,
                CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("YouTube playback service is not configured.");

            public Task ActivateSessionAsync(string sessionId, string speakerIp, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<YouTubePlaybackQueueItem?> GetQueueItemAsync(string sessionId, int itemIndex, CancellationToken cancellationToken = default)
                => Task.FromResult<YouTubePlaybackQueueItem?>(null);

            public Task<YouTubePlaybackOpenResult?> OpenPlaybackAsync(string sessionId, int itemIndex = 0, CancellationToken cancellationToken = default)
                => Task.FromResult<YouTubePlaybackOpenResult?>(null);

            public Task MaintainSessionsAsync(CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }
        public Task PausePlaying(string ip) => PausePlaying(ip, CancellationToken.None);

        public async Task PausePlaying(string ip, CancellationToken cancellationToken)
            => await _transport.SendAsync(CreateClient(), ip, "Pause", string.Empty, cancellationToken);

        public async Task StopPlaying(string ip)
            => await _transport.SendAsync(CreateClient(), ip, "Stop", string.Empty, CancellationToken.None);

        public virtual async Task StartPlaying(string ip, CancellationToken cancellationToken = default)
            => await _transport.SendAsync(CreateClient(), ip, "Play", "<Speed>1</Speed>", cancellationToken);

        public Task<bool> IsPlaying(string ip) => IsPlaying(ip, CancellationToken.None);

        public async Task<bool> IsPlaying(string ip, CancellationToken cancellationToken)
        {
            var response = await _transport.SendAsync(CreateClient(), ip, "GetTransportInfo", string.Empty, cancellationToken);
            return string.Equals(RequiredValue(response, "CurrentTransportState"), "PLAYING", StringComparison.OrdinalIgnoreCase);
        }

        public Task<int> GetVolume(string ip) => GetVolume(ip, CancellationToken.None);

        public async Task<int> GetVolume(string ip, CancellationToken cancellationToken)
        {
            var response = await _transport.SendAsync(CreateClient(), ip, "GetVolume", "<Channel>Master</Channel>", cancellationToken, "RenderingControl");
            if (!int.TryParse(RequiredValue(response, "CurrentVolume"), out var volume) || volume is < 0 or > 100)
                throw new InvalidDataException("The speaker returned an invalid volume.");
            return volume;
        }

        public Task SetVolume(string ip, int volume) => SetSpeakerVolume(ip, volume);

        public async Task SetSpeakerVolume(string ip, int volume, CancellationToken cancellationToken = default)
        {
            if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 100.");
            await _transport.SendAsync(CreateClient(), ip, "SetVolume", $"<Channel>Master</Channel><DesiredVolume>{volume}</DesiredVolume>", cancellationToken, "RenderingControl");
        }

        public async Task PlayAudioFileAsync(string ip, string audioUrl, string title, CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("An HTTP or HTTPS audio URL is required.", nameof(audioUrl));
            await SetTransportUriAsync(ip, audioUrl, YouTubeQueueMetadataBuilder.Build(title, audioUrl), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await StartPlaying(ip, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public virtual async Task SetTuneInStationAsync(string ip, string stationUri, CancellationToken cancellationToken = default)
        {
            var currentUri = SonosMediaSourceBuilder.Radio(stationUri);
            await ClearQueue(ip, cancellationToken);
            await SetTransportUriAsync(ip, currentUri, string.Empty, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await StartPlaying(ip, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
                using var response = await _transport.PostAsync(client, url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(cancellationToken);
                var trackNumber = TryParseTrackNumber(xml);

                // Extract TrackMetaData block
                var match = Regex.Match(xml, @"<TrackMetaData>(.*?)</TrackMetaData>", RegexOptions.Singleline);

                if (match.Success)
                {
                    var metadataXml = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value); // Unescape XML

                    // Extract the title from TrackMetaData
                    var titleMatch = Regex.Match(metadataXml, @"<dc:title>(.*?)</dc:title>");
                    var creatorMatch = Regex.Match(metadataXml, @"<dc:creator>(.*?)</dc:creator>");
                    var artistMatch = Regex.Match(metadataXml, @"<upnp:artist>(.*?)</upnp:artist>");

                    var title = titleMatch.Success ? DecodeMetadataText(titleMatch.Groups[1].Value) : "Unknown Title";
                    var artist = creatorMatch.Success ? DecodeMetadataText(creatorMatch.Groups[1].Value) : "Unknown Artist";

                    return $"{title} — {artist}";
                }

                return "No metadata available";
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { throw; }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public async Task<string> GetCurrentTrackAsync(string ip, CancellationToken cancellationToken = default)
        {
            var info = await GetTrackInfoAsync(ip, cancellationToken);
            if (info != null && info.IsValidMetadata())
            {
                return info.GetDisplayString();
            }
            return "No metadata available";
        }

        public async Task<SonosTrackInfo?> GetTrackInfoAsync(string ip, CancellationToken cancellationToken = default)
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
                using var response = await _transport.PostAsync(client, url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(cancellationToken);
                var trackNumber = TryParseTrackNumber(xml);

                // Extract <TrackMetaData> content
                var match = Regex.Match(xml, @"<TrackMetaData>(.*?)</TrackMetaData>", RegexOptions.Singleline);
                if (!match.Success)
                {
                    return null;
                }

                var trackUriMatch = Regex.Match(xml, @"<TrackURI>(.*?)</TrackURI>", RegexOptions.Singleline);
                var trackUri = trackUriMatch.Success ? DecodeMetadataText(trackUriMatch.Groups[1].Value) : null;
                return await ResolveTrackInfoAsync(ip, WebUtility.HtmlDecode(match.Groups[1].Value), trackUri, trackNumber, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read track metadata from {SpeakerIp}.", ip);
                return null;
            }
        }

        public async Task<SonosTrackInfo?> ResolveTrackInfoAsync(
            string ip,
            string? trackMetadata,
            string? trackUri,
            int? trackNumber,
            CancellationToken cancellationToken = default)
        {
            if (trackMetadata is null)
            {
                return null;
            }

            var trackInfo = ParseTrackMetadata(trackMetadata, ip);
            return await TryGetYouTubeSessionTrackInfoFallbackAsync(trackInfo, trackUri, cancellationToken)
                ?? await TryGetQueueTrackInfoFallbackAsync(ip, trackInfo, trackNumber, cancellationToken)
                ?? trackInfo;
        }

        private async Task<SonosTrackInfo?> TryGetYouTubeSessionTrackInfoFallbackAsync(
            SonosTrackInfo currentInfo,
            string? resourceUri,
            CancellationToken cancellationToken)
        {
            if (currentInfo.IsValidMetadata()
                && !string.Equals(currentInfo.Title, "0", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(currentInfo.Artist, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!TryParseYouTubePlaybackUri(resourceUri, out var sessionId, out var itemIndex))
            {
                return null;
            }

            var queueItem = await _youTubePlaybackService.GetQueueItemAsync(sessionId, itemIndex, cancellationToken);
            if (queueItem is null)
            {
                return null;
            }

            var title = string.IsNullOrWhiteSpace(queueItem.Title) ? currentInfo.Title : queueItem.Title;
            var artist = string.IsNullOrWhiteSpace(queueItem.Artist) ? currentInfo.Artist : queueItem.Artist;

            return new SonosTrackInfo
            {
                Title = title,
                Artist = artist,
                Album = currentInfo.Album,
                AlbumArtUri = string.IsNullOrWhiteSpace(queueItem.AlbumArtUrl) ? currentInfo.AlbumArtUri : queueItem.AlbumArtUrl,
                StreamContent = string.IsNullOrWhiteSpace(queueItem.StreamContent)
                    ? YouTubeQueueMetadataBuilder.FormatStreamContent(title, artist)
                    : queueItem.StreamContent
            };
        }

        private async Task<SonosTrackInfo?> TryGetQueueTrackInfoFallbackAsync(
            string ip,
            SonosTrackInfo currentInfo,
            int? trackNumber,
            CancellationToken cancellationToken)
        {
            if (currentInfo.IsValidMetadata()
                && !string.Equals(currentInfo.Title, "0", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var currentStation = await GetCurrentStationAsync(ip, cancellationToken);
            if (!currentStation.Contains("x-rincon-queue:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var queueIndex = Math.Max(0, (trackNumber ?? 1) - 1);
            var queuePage = await GetQueue(ip, queueIndex, 1, cancellationToken);
            var queueItem = queuePage.Items.FirstOrDefault();
            if (queueItem is null)
            {
                return null;
            }

            var title = string.IsNullOrWhiteSpace(queueItem.Title) ? currentInfo.Title : queueItem.Title;
            var artist = string.IsNullOrWhiteSpace(queueItem.Artist) ? currentInfo.Artist : queueItem.Artist;
            var album = string.IsNullOrWhiteSpace(queueItem.Album) ? currentInfo.Album : queueItem.Album;

            return new SonosTrackInfo
            {
                Title = title,
                Artist = artist,
                Album = album,
                AlbumArtUri = currentInfo.AlbumArtUri,
                StreamContent = YouTubeQueueMetadataBuilder.FormatStreamContent(title, artist)
            };
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
                using var response = await _transport.PostAsync(client, url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(cancellationToken);

                var doc = new XmlDocument();
                doc.LoadXml(xml);

                TimeSpan.TryParse(doc.GetElementsByTagName("RelTime").Item(0)?.InnerText ?? "00:00:00", out var relTime);
                TimeSpan.TryParse(doc.GetElementsByTagName("TrackDuration").Item(0)?.InnerText ?? "00:00:00", out var trackDuration);

                return (relTime, trackDuration);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { throw; }
            catch
            {
                return (TimeSpan.Zero, TimeSpan.Zero);
            }
        }

        public async Task<int?> GetCurrentTrackNumberAsync(string ip, CancellationToken cancellationToken = default)
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
                using var response = await _transport.PostAsync(client, url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = new XmlDocument();
                doc.LoadXml(xml);

                return int.TryParse(doc.GetElementsByTagName("Track").Item(0)?.InnerText, out var trackNumber)
                    ? trackNumber
                    : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { throw; }
            catch
            {
                return null;
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
                using var response = await _transport.PostAsync(client, url, content, cancellationToken);
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
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { throw; }
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

            using var response = await client.SendAsync(request, cancellationToken);
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
            SonosMediaSourceBuilder.Spotify(spotifyUrl, "validation");
            cancellationToken.ThrowIfCancellationRequested();
            var rinconId = await GetRinconIdAsync(ip, cancellationToken)
                ?? throw new InvalidOperationException("Could not retrieve the speaker RINCON ID.");
            if (!string.IsNullOrWhiteSpace(fallbackStationUri)) await SetTuneInStationAsync(ip, fallbackStationUri, cancellationToken);
            else await ClearQueue(ip, cancellationToken);
            var (uri, metadata) = SonosMediaSourceBuilder.Spotify(spotifyUrl, rinconId);
            await SetTransportUriAsync(ip, uri, metadata, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await StartPlaying(ip, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task PlayYouTubeMusicTrackAsync(string ip, string youtubeMusicUrl, string? fallbackStationUri = null, CancellationToken cancellationToken = default)
        {
            SonosMediaSourceBuilder.YouTubeMusic(youtubeMusicUrl, "validation");
            cancellationToken.ThrowIfCancellationRequested();
            var rinconId = await GetRinconIdAsync(ip, cancellationToken)
                ?? throw new InvalidOperationException("Could not retrieve the speaker RINCON ID.");
            if (!string.IsNullOrWhiteSpace(fallbackStationUri)) await SetTuneInStationAsync(ip, fallbackStationUri, cancellationToken);
            else await ClearQueue(ip, cancellationToken);
            var (uri, metadata) = SonosMediaSourceBuilder.YouTubeMusic(youtubeMusicUrl, rinconId);
            await SetTransportUriAsync(ip, uri, metadata, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await StartPlaying(ip, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task PlayYouTubeAudioAsync(
            string ip,
            string youtubeUrl,
            YouTubePlaybackMode? playbackMode = null,
            int? preferredQueueLength = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = await _youTubePlaybackService.PreparePlaybackAsync(youtubeUrl, playbackMode, preferredQueueLength, cancellationToken);
            if (session.QueueItems.Count == 0)
            {
                throw new InvalidOperationException("YouTube playback session did not produce any queue items.");
            }

            await ClearQueue(ip, cancellationToken);
            foreach (var item in session.QueueItems)
            {
                await AddUriToQueue(ip, item.StreamUrl, YouTubeQueueMetadataBuilder.Build(item), false, cancellationToken);
            }

            await SetQueueTransportAsync(ip, cancellationToken);
            await SeekToTrackAsync(ip, 1, cancellationToken);
            await _youTubePlaybackService.ActivateSessionAsync(session.SessionId, ip, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await StartPlaying(ip, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the RINCON ID from {SpeakerIp}.", ip);
            }

            return null;
        }

        public async Task ClearQueue(string ip, CancellationToken cancellationToken = default)
        {
            await _transport.SendAsync(CreateClient(), ip, "RemoveAllTracksFromQueue", string.Empty, cancellationToken);
        }

        public async Task AddUriToQueue(string ip, string uri, string? metadata = null, bool enqueueAsNext = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new ArgumentException("IP address must be provided.", nameof(ip));
            }

            if (string.IsNullOrWhiteSpace(uri))
            {
                throw new ArgumentException("Queue URI must be provided.", nameof(uri));
            }

            var url = $"http://{ip}:1400/MediaRenderer/AVTransport/Control";
            var escapedUri = SecurityElement.Escape(uri.Trim()) ?? string.Empty;
            var escapedMetadata = SecurityElement.Escape(metadata ?? string.Empty) ?? string.Empty;

            var soapEnvelope = $@"
                <s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'
                            s:encodingStyle='http://schemas.xmlsoap.org/soap/encoding/'>
                    <s:Body>
                        <u:AddURIToQueue xmlns:u='urn:schemas-upnp-org:service:AVTransport:1'>
                            <InstanceID>0</InstanceID>
                            <EnqueuedURI>{escapedUri}</EnqueuedURI>
                            <EnqueuedURIMetaData>{escapedMetadata}</EnqueuedURIMetaData>
                            <DesiredFirstTrackNumberEnqueued>0</DesiredFirstTrackNumberEnqueued>
                            <EnqueueAsNext>{(enqueueAsNext ? 1 : 0)}</EnqueueAsNext>
                        </u:AddURIToQueue>
                    </s:Body>
                </s:Envelope>";

            using var content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
            content.Headers.Remove("SOAPACTION");
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#AddURIToQueue\"");

            var client = CreateClient();
            using var response = await _transport.PostAsync(client, url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private async Task SetQueueTransportAsync(string ip, CancellationToken cancellationToken)
        {
            var rinconHex = await GetRinconIdAsync(ip, cancellationToken);
            if (string.IsNullOrWhiteSpace(rinconHex))
            {
                throw new InvalidOperationException($"Could not resolve the Sonos queue transport for speaker {ip}.");
            }

            await SetTransportUriAsync(ip, $"x-rincon-queue:RINCON_{rinconHex}#0", string.Empty, cancellationToken);
        }

        private async Task SeekToTrackAsync(string ip, int trackNumber, CancellationToken cancellationToken)
        {
            if (trackNumber <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(trackNumber), "Track number must be >= 1.");
            }

            var soapRequest = $@"
                <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""
                            s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
                  <s:Body>
                    <u:Seek xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
                      <InstanceID>0</InstanceID>
                      <Unit>TRACK_NR</Unit>
                      <Target>{trackNumber}</Target>
                    </u:Seek>
                  </s:Body>
                </s:Envelope>";

            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#Seek\"");

            var client = CreateClient();
            using var response = await _transport.PostAsync(client, $"http://{ip}:1400/MediaRenderer/AVTransport/Control", content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public async Task<SonosQueuePage> GetQueue(string ip, int startIndex = 0, int count = 100, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new ArgumentException("IP address must be provided.", nameof(ip));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var url = $"http://{ip}:1400/MediaServer/ContentDirectory/Control";

            var soapEnvelope = $@"
                <s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'
                            s:encodingStyle='http://schemas.xmlsoap.org/soap/encoding/'>
                    <s:Body>
                        <u:Browse xmlns:u='urn:schemas-upnp-org:service:ContentDirectory:1'>
                            <ObjectID>Q:0</ObjectID>
                            <BrowseFlag>BrowseDirectChildren</BrowseFlag>
                            <Filter>*</Filter>
                            <StartingIndex>{startIndex}</StartingIndex>
                            <RequestedCount>{count}</RequestedCount>
                            <SortCriteria></SortCriteria>
                        </u:Browse>
                    </s:Body>
                </s:Envelope>";

            using var content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
            content.Headers.Remove("Content-Type");
            content.Headers.Add("Content-Type", "text/xml; charset=utf-8");
            content.Headers.Remove("SOAPACTION");
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");

            try
            {
                var client = CreateClient();
                using var response = await _transport.PostAsync(client, url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var queuePage = ParseQueueResponse(responseBody, startIndex, count);
                return await EnrichQueuePageWithYouTubeSessionMetadataAsync(queuePage, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the queue from {SpeakerIp}.", ip);
                return new SonosQueuePage(Array.Empty<SonosQueueItem>(), startIndex, 0, startIndex);
            }
        }

        private async Task<SonosQueuePage> EnrichQueuePageWithYouTubeSessionMetadataAsync(SonosQueuePage queuePage, CancellationToken cancellationToken)
        {
            if (queuePage.Items.Count == 0)
            {
                return queuePage;
            }

            var enrichedItems = new List<SonosQueueItem>(queuePage.Items.Count);
            foreach (var item in queuePage.Items)
            {
                if (!TryParseYouTubePlaybackUri(item.ResourceUri, out var sessionId, out var itemIndex))
                {
                    enrichedItems.Add(item);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(item.Title)
                    && !string.Equals(item.Title, "0", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(item.Artist))
                {
                    enrichedItems.Add(item);
                    continue;
                }

                var queueItem = await _youTubePlaybackService.GetQueueItemAsync(sessionId, itemIndex, cancellationToken);
                if (queueItem is null)
                {
                    enrichedItems.Add(item);
                    continue;
                }

                enrichedItems.Add(new SonosQueueItem(
                    item.Index,
                    string.IsNullOrWhiteSpace(queueItem.Title) ? item.Title : queueItem.Title,
                    string.IsNullOrWhiteSpace(queueItem.Artist) ? item.Artist : queueItem.Artist,
                    item.Album,
                    item.ResourceUri));
            }

            return new SonosQueuePage(enrichedItems, queuePage.StartIndex, queuePage.NumberReturned, queuePage.TotalMatches);
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

        public virtual async Task<string?> GetSpeakerUUID(string ip, CancellationToken cancellationToken = default)
        {
            var rinconId = await GetRinconIdAsync(ip, cancellationToken);
            return rinconId != null ? $"uuid:RINCON_{rinconId}" : null;
        }

        public async Task<bool> CreateGroup(string masterIp, IEnumerable<string> slaveIps, CancellationToken cancellationToken = default)
        {
            var masterRinconHex = await GetRinconIdAsync(masterIp, cancellationToken)
                ?? throw new InvalidOperationException($"Could not retrieve the RINCON ID for {masterIp}.");
            var groupUri = $"x-rincon:RINCON_{masterRinconHex}";
            foreach (var slaveIp in slaveIps.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(slaveIp, masterIp, StringComparison.OrdinalIgnoreCase)) continue;
                await SetTransportUriAsync(slaveIp, groupUri, string.Empty, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogDebug("Speaker {SpeakerIp} joined master {MasterIp}.", slaveIp, masterIp);
            }
            return true;
        }

        public async Task UngroupSpeaker(string ip, CancellationToken cancellationToken = default)
        {
            await _transport.SendAsync(CreateClient(), ip, "BecomeCoordinatorOfStandaloneGroup", string.Empty, cancellationToken);
            _logger.LogDebug("Speaker {SpeakerIp} ungrouped.", ip);
        }

        public async Task<IEnumerable<string>> GetAllSpeakersInGroup(string ip, CancellationToken cancellationToken = default)
        {
            var response = await _transport.SendAsync(CreateClient(), ip, "GetZoneGroupState", string.Empty, cancellationToken, "ZoneGroupTopology");
            var settings = await _settingsRepo.GetSettings() ?? throw new InvalidOperationException("Settings could not be loaded.");
            var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var discovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var speaker in settings.Speakers)
            {
                var uuid = speaker.Uuid;
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    uuid = await GetSpeakerUUID(speaker.IpAddress, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(uuid)) discovered[speaker.IpAddress] = uuid;
                }
                if (!string.IsNullOrWhiteSpace(uuid)) identities[speaker.IpAddress] = NormalizeUuid(uuid);
            }
            if (discovered.Count > 0)
                await _settingsRepo.UpdateSettingsAsync(current =>
                {
                    foreach (var speaker in current.Speakers)
                        if (string.IsNullOrWhiteSpace(speaker.Uuid) && discovered.TryGetValue(speaker.IpAddress, out var uuid)) speaker.Uuid = uuid;
                }, cancellationToken);

            if (!identities.TryGetValue(ip, out var requestedId))
                requestedId = NormalizeUuid(await GetSpeakerUUID(ip, cancellationToken)
                    ?? throw new InvalidOperationException($"Could not retrieve the RINCON ID for {ip}."));
            var members = ParseGroupMembers(response, requestedId);
            return identities.Where(pair => members.Contains(pair.Value)).Select(pair => pair.Key)
                .Append(ip).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private Task<string> SetTransportUriAsync(string ip, string uri, string metadata, CancellationToken cancellationToken)
            => _transport.SendAsync(CreateClient(), ip, "SetAVTransportURI", $"<CurrentURI>{SecurityElement.Escape(uri)}</CurrentURI><CurrentURIMetaData>{SecurityElement.Escape(metadata)}</CurrentURIMetaData>", cancellationToken);

        private Task<string> SendAvTransportCommand(string ip, string action, CancellationToken cancellationToken)
            => _transport.SendAsync(CreateClient(), ip, action, string.Empty, cancellationToken);

        private async Task<string> SendSoapRequest(string url, string soapRequest, string soapAction, CancellationToken cancellationToken = default)
        {
            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            content.Headers.Clear();
            content.Headers.Add("SOAPACTION", $"\"{soapAction}\"");

            var client = CreateClient();
            using var response = await _transport.PostAsync(client, url, content, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }
}
