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
    }
}