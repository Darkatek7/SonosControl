using System.Collections.Generic;
using System.Threading;

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
        Task<(TimeSpan Position, TimeSpan Duration)> GetTrackProgressAsync(string ip, CancellationToken cancellationToken = default);
        Task SetTuneInStationAsync(string ip, string stationUri, CancellationToken cancellationToken = default);
        Task<string> GetCurrentStationAsync(string ip, CancellationToken cancellationToken = default);
        Task<string?> SearchSpotifyTrackAsync(string query, string accessToken, CancellationToken cancellationToken = default);
        Task PlaySpotifyTrackAsync(string ip, string spotifyUri, string? fallbackStationUri = null, CancellationToken cancellationToken = default);
        Task ClearQueue(string ip, CancellationToken cancellationToken = default);
        Task<List<string>> GetQueue(string ip, CancellationToken cancellationToken = default);
        Task PreviousTrack(string ip, CancellationToken cancellationToken = default);
        Task NextTrack(string ip, CancellationToken cancellationToken = default);
    }
}
