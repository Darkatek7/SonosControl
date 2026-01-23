using System.Threading.Tasks;

namespace SonosControl.DAL.Interfaces
{
    public interface INotifier
    {
        Task SendNotificationAsync(string message, string? performedBy = null);
    }
}
