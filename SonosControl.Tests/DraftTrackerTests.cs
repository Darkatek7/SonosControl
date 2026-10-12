using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class DraftTrackerTests
{
    [Fact]
    public void SceneCreationRequestSurvivesNavigationAndIsConsumedOnce()
    {
        var requests = new EditorNavigationRequests();
        Assert.False(requests.TakeNewSceneRequest());
        requests.RequestNewScene();
        Assert.True(requests.TakeNewSceneRequest());
        Assert.False(requests.TakeNewSceneRequest());
    }

    [Fact]
    public void Scene_RevertingFieldsClearsChanges_AndMetadataDoesNotCreateChanges()
    {
        var scene = new Scene { Name = "Morning", Actions = [new() { SpeakerIp = "1.2.3.4", Volume = 20 }] };
        var tracker = new DraftTracker();
        tracker.Accept(DraftTracker.Values(scene));
        Assert.False(tracker.HasChanges(DraftTracker.Values(scene)));
        scene.LastModifiedUtc = DateTime.UtcNow.AddHours(1);
        Assert.False(tracker.HasChanges(DraftTracker.Values(scene)));
        scene.Actions[0].Volume = 30;
        Assert.True(tracker.HasChanges(DraftTracker.Values(scene)));
        scene.Actions[0].Volume = 20;
        Assert.False(tracker.HasChanges(DraftTracker.Values(scene)));
        scene.Name = "Evening";
        tracker.Accept(DraftTracker.Values(scene));
        Assert.False(tracker.HasChanges(DraftTracker.Values(scene)));
    }

    [Fact]
    public void Window_DayOrderingIsNotAnEdit_ButTimingAndSceneChangesAre()
    {
        var window = new ScheduleWindow { SceneId = "a", DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Friday] };
        var tracker = new DraftTracker();
        tracker.Accept(DraftTracker.Values(window));
        window.DaysOfWeek.Reverse();
        window.LastModifiedUtc = DateTime.UtcNow.AddDays(1);
        Assert.False(tracker.HasChanges(DraftTracker.Values(window)));
        window.SceneId = "b";
        Assert.True(tracker.HasChanges(DraftTracker.Values(window)));
        window.SceneId = "a";
        window.StartTime = window.StartTime.AddHours(1);
        Assert.True(tracker.HasChanges(DraftTracker.Values(window)));
    }
}
