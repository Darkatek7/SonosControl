using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public sealed class SettingsEditException(string message, int statusCode = 400) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class SettingsEditingService(ISettingsRepo repository)
{
    public async Task<SonosSettings> SaveSceneAsync(Scene draft, bool create, Scene? original = null)
    {
        return await repository.UpdateSettingsAsync(settings =>
        {
            var index = settings.Scenes.FindIndex(s => Equal(s.Id, draft.Id));
            var template = settings.ScheduleWindows.FindIndex(window => !window.HasTimeSchedule
                && window.Playback is not null && Equal(window.Playback.Id, draft.Id));
            var exists = index >= 0 || template >= 0;
            Check(create ? !exists : exists, create ? "Scene already exists." : "Scene was removed. Reload before editing it.", create ? 409 : 404);
            var current = index >= 0 ? settings.Scenes[index] : template >= 0 ? settings.ScheduleWindows[template].Playback : null;
            var candidate = original is not null && current is not null
                ? SettingsChanges.MergeEntity(current, original, draft)
                : Clone(draft);
            ValidateScene(candidate, settings);
            candidate.LastModifiedUtc = DateTime.UtcNow;
            if (index < 0) settings.Scenes.Add(candidate); else settings.Scenes[index] = candidate;
            var templateWindow = template >= 0 ? Clone(settings.ScheduleWindows[template])
                : new ScheduleWindow { Id = $"scene-template-{candidate.Id}", HasTimeSchedule = false };
            templateWindow.Name = candidate.Name;
            templateWindow.IsEnabled = candidate.Enabled;
            templateWindow.Playback = Clone(candidate);
            templateWindow.LastModifiedUtc = candidate.LastModifiedUtc;
            if (template >= 0) settings.ScheduleWindows[template] = templateWindow;
            else settings.ScheduleWindows.Add(templateWindow);
        });
    }

    public async Task<SonosSettings> SaveWindowAsync(ScheduleWindow draft, bool create, ScheduleWindow? original = null)
    {
        return await repository.UpdateSettingsAsync(settings =>
        {
            var index = settings.ScheduleWindows.FindIndex(w => Equal(w.Id, draft.Id));
            Check(create ? index < 0 : index >= 0, create ? "Schedule already exists." : "Schedule was removed. Reload before editing it.", create ? 409 : 404);
            var candidate = original is not null && index >= 0
                ? SettingsChanges.MergeEntity(settings.ScheduleWindows[index], original, draft)
                : Clone(draft);
            ValidateWindow(candidate, settings);
            candidate.LastModifiedUtc = DateTime.UtcNow;
            if (create) settings.ScheduleWindows.Add(candidate); else settings.ScheduleWindows[index] = candidate;
            if (!candidate.HasTimeSchedule && candidate.Playback is not null)
            {
                var legacyIndex = settings.Scenes.FindIndex(scene => Equal(scene.Id, candidate.Playback.Id));
                if (legacyIndex >= 0) settings.Scenes[legacyIndex] = Clone(candidate.Playback);
            }
        });
    }

    public Task<SonosSettings> DeleteSceneAsync(string id) => repository.UpdateSettingsAsync(settings =>
    {
        var removedScenes = settings.Scenes.RemoveAll(s => Equal(s.Id, id));
        var removedTemplates = settings.ScheduleWindows.RemoveAll(window => !window.HasTimeSchedule
            && window.Playback is not null && Equal(window.Playback.Id, id));
        Check(removedScenes + removedTemplates > 0, "Scene not found.", 404);
        foreach (var window in settings.ScheduleWindows.Where(w => Equal(w.SceneId, id)))
        {
            window.SceneId = null;
            window.IsEnabled = false;
            window.LastModifiedUtc = DateTime.UtcNow;
        }
    });

    public Task<SonosSettings> DeleteWindowAsync(string id) => repository.UpdateSettingsAsync(settings =>
    {
        var window = settings.ScheduleWindows.FirstOrDefault(w => Equal(w.Id, id));
        Check(window is not null, "Schedule not found.", 404);
        settings.ScheduleWindows.Remove(window!);
        if (window is { HasTimeSchedule: false, Playback: not null })
            settings.Scenes.RemoveAll(scene => Equal(scene.Id, window.Playback.Id));
    });

    public Task<SonosSettings> MoveWindowAsync(string id, int direction) => repository.UpdateSettingsAsync(settings =>
    {
        Check(direction is -1 or 1, "Move direction must be -1 or 1.");
        var ordered = ScheduleWindowOrdering.Order(settings.ScheduleWindows).ToList();
        var index = ordered.FindIndex(window => Equal(window.Id, id));
        Check(index >= 0, "Schedule was removed. Reload before moving it.", 404);
        var target = index + direction;
        if (target < 0 || target >= ordered.Count) return;
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        ScheduleWindowOrdering.AssignPriorities(ordered);
        foreach (var window in ordered)
        {
            ValidateWindow(window, settings);
            window.LastModifiedUtc = DateTime.UtcNow;
        }
    });

    public static void ValidateScene(Scene scene, SonosSettings settings, bool checkUniqueName = true)
    {
        Check(!string.IsNullOrWhiteSpace(scene.Name), "Scene name is required.");
        scene.Name = scene.Name.Trim();
        if (checkUniqueName)
            Check(!settings.Scenes.Any(s => !Equal(s.Id, scene.Id) && Equal(s.Name.Trim(), scene.Name)), $"A scene named '{scene.Name}' already exists.", 409);
        Check(Enum.IsDefined(scene.SourceType) && Enum.IsDefined(scene.SourceSelectionMode), "Invalid scene source selection.");
        Check(scene.TimerMinutes is null or >= 0, "Timer minutes cannot be negative.");
        if (scene.TimerMinutes == 0) scene.TimerMinutes = null;
        if (scene.SourceSelectionMode == SceneSourceSelectionMode.Resume || scene.SourceType == SceneSourceType.None)
        {
            scene.SourceType = SceneSourceType.None;
            scene.SourceSelectionMode = SceneSourceSelectionMode.Resume;
            scene.SourceUrl = null;
        }
        else if (scene.SourceSelectionMode == SceneSourceSelectionMode.Specific)
            Check(!string.IsNullOrWhiteSpace(scene.SourceUrl), "Source URL is required for a specific source.");
        else if (scene.SourceSelectionMode == SceneSourceSelectionMode.Random)
            scene.SourceUrl = null;
        scene.SpeakerIps ??= [];
        scene.Actions ??= [];
        Check(scene.Actions.Count == 0 || SceneTargetResolver.Resolve(scene, settings).Count > 0, "Select at least one speaker.");
        foreach (var action in scene.Actions)
            Check(action.Volume is null || action.Volume >= 0 && action.Volume <= settings.MaxVolume, $"Speaker volume must be between 0 and {settings.MaxVolume}.");
    }

    public static void ValidateWindow(ScheduleWindow window, SonosSettings settings)
    {
        Check(!string.IsNullOrWhiteSpace(window.Name), "Schedule window name is required.");
        Check(window.Priority is >= 0 and <= 1000, "Schedule window priority must be between 0 and 1000.");
        Check(window.FadeInSeconds is >= 0 and <= 600 && window.FadeOutSeconds is >= 0 and <= 600, "Fade values must be between 0 and 600 seconds.");
        Check(Enum.IsDefined(window.RecurrenceType), "Invalid recurrence type.");
        window.DaysOfWeek ??= [];
        Check(window.DaysOfWeek.All(Enum.IsDefined), "Invalid recurrence day.");
        Check(window.RecurrenceType != ScheduleRecurrenceType.CustomDays || window.DaysOfWeek.Count > 0, "Custom day recurrence requires at least one day.");
        Check(!window.StartDate.HasValue || !window.EndDate.HasValue || window.EndDate >= window.StartDate, "EndDate must be on or after StartDate.");
        if (window.Playback is null && !string.IsNullOrWhiteSpace(window.SceneId))
        {
            var scene = settings.Scenes.FirstOrDefault(s => Equal(s.Id, window.SceneId));
            Check(scene is not null, "Schedule references a scene that does not exist.");
            window.Playback = Clone(scene!);
            if (window.HasTimeSchedule) window.Playback.Id = $"{window.Id}-playback";
        }
        Check(!window.IsEnabled || window.Playback is { Enabled: true }, "An enabled schedule must include enabled playback.");
        if (window.Playback is not null)
        {
            Check(window.Playback.SpeakerIps?.Count > 0 || window.Playback.Actions?.Any(a => !string.IsNullOrWhiteSpace(a.SpeakerIp)) == true, "Select at least one speaker for schedule playback.");
            ValidateScene(window.Playback, settings, checkUniqueName: false);
            window.SceneId = null;
        }
        window.ExcludedDates ??= [];
        window.AnnualExcludedDates ??= [];
    }

    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static void Check(bool valid, string message, int status = 400) { if (!valid) throw new SettingsEditException(message, status); }
    private static T Clone<T>(T value) where T : class => SettingsChanges.MergeEntity(value, value, value);
}
