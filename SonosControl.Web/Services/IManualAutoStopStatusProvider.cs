namespace SonosControl.Web.Services;

public interface IManualAutoStopStatusProvider
{
    Task<ManualAutoStopStatus?> GetNextStopAsync(string speakerIp, CancellationToken cancellationToken = default);
}
