using SonosControl.DAL.Models;

namespace SonosControl.DAL.Interfaces;

public interface ISettingsRepo
{
    Task<SonosSettings?> GetSettings();
    Task<SonosSettings> UpdateSettingsAsync(Action<SonosSettings> update, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(SonosSettings? settings);
    Task<string?> ReplaceSettingsAsync(SonosSettings settings, string label = "replacement", CancellationToken cancellationToken = default);
    Task<string?> CreateVersionedBackupAsync(string label, CancellationToken cancellationToken = default);
}
