using System.Text.Json;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public sealed class DraftTracker
{
    private string? _baseline;

    public void Accept(object? values) => _baseline = Serialize(values);
    public bool HasChanges(object? values) => _baseline is not null && _baseline != Serialize(values);

    private static string? Serialize(object? values) => values is null ? null : JsonSerializer.Serialize(values);

    public static object? Values(Scene? scene) => scene is null ? null : new
    {
        scene.Name,
        scene.Description,
        scene.Enabled,
        scene.SourceSelectionMode,
        scene.SourceType,
        scene.SourceUrl,
        scene.IsSyncedPlayback,
        scene.MasterSpeakerIp,
        scene.TimerMinutes,
        Actions = scene.Actions.OrderBy(action => action.SpeakerIp, StringComparer.OrdinalIgnoreCase)
            .Select(action => new { action.SpeakerIp, action.IncludeInPlayback, action.Volume, action.IsMaster })
    };

    public static object? Values(ScheduleWindow? window) => window is null ? null : new
    {
        window.Name,
        window.IsEnabled,
        window.Priority,
        window.StartTime,
        window.StopTime,
        window.RecurrenceType,
        Days = window.DaysOfWeek.Order(),
        window.StartDate,
        window.EndDate,
        window.SceneId,
        window.FadeInSeconds,
        window.FadeOutSeconds
    };
}
