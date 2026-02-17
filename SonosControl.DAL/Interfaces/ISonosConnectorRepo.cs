using System.Collections.Generic;
using System.Threading;
using SonosControl.DAL.Models;

namespace SonosControl.DAL.Interfaces
{
    public interface ISonosConnectorRepo
    {
        Task<int> GetVolume(string ip);
        Task<bool> IsPlaying(string ip);
        Task PausePlaying(string ip);
        Task SetVolume(string ip, int volume);
        Task StartPlaying(string ip);
        Task StopPlaying(string ip);
        Task<string> GetCurrentTrackAsync(string ip, CancellationToken cancellationToken = default);
        Task<SonosTrackInfo?> GetTrackInfoAsync(string ip, CancellationToken cancellationToken = default);
        Task<(TimeSpan Position, TimeSpan Duration)> GetTrackProgressAsync(string ip, CancellationToken cancellationToken = default);
        Task SetTuneInStationAsync(string ip, string stationUri, CancellationToken cancellationToken = default);
        Task<string> GetCurrentStationAsync(string ip, CancellationToken cancellationToken = default);
        Task<string?> SearchSpotifyTrackAsync(string query, string accessToken, CancellationToken cancellationToken = default);
        Task PlaySpotifyTrackAsync(string ip, string spotifyUri, string? fallbackStationUri = null, CancellationToken cancellationToken = default);
        Task PlayYouTubeMusicTrackAsync(string ip, string youtubeMusicUrl, string? fallbackStationUri = null, CancellationToken cancellationToken = default);
        Task ClearQueue(string ip, CancellationToken cancellationToken = default);
        Task AddUriToQueue(string ip, string uri, string? metadata = null, bool enqueueAsNext = false, CancellationToken cancellationToken = default);
        Task<SonosQueuePage> GetQueue(string ip, int startIndex = 0, int count = 100, CancellationToken cancellationToken = default);
        Task PreviousTrack(string ip, CancellationToken cancellationToken = default);
        Task NextTrack(string ip, CancellationToken cancellationToken = default);
        Task RebootDeviceAsync(string ip, CancellationToken cancellationToken = default);
        Task<string?> GetSpeakerUUID(string ip, CancellationToken cancellationToken = default);
        Task<bool> CreateGroup(string masterIp, IEnumerable<string> slaveIps, CancellationToken cancellationToken = default);
        Task UngroupSpeaker(string ip, CancellationToken cancellationToken = default);
        Task<IEnumerable<string>> GetAllSpeakersInGroup(string ip, CancellationToken cancellationToken = default);
        Task SetSpeakerVolume(string ip, int volume, CancellationToken cancellationToken = default);
    }
}
