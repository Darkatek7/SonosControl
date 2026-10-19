using SonosControl.DAL.Models;

namespace SonosControl.Web.Services.FlyDj;

/// <summary>A winning FlyDJ window, restricted to the rooms it actually owns.</summary>
public sealed record FlyDjSchedulePlayback(
    ScheduleWindow Window, IReadOnlyCollection<string> Targets, DateTimeOffset StartUtc, DateTimeOffset StopUtc)
{
    public string Key => $"{Window.Id}:{StartUtc:O}";
}

public interface IFlyDjScheduleConductor
{
    Task UpdateSchedulesAsync(IReadOnlyList<FlyDjSchedulePlayback> schedules, CancellationToken cancellationToken = default);
}
