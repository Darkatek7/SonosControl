using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Pages;
using SonosControl.Web.Services;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public class EditorWorkflowTests
{
    [Fact]
    public void SceneEditor_LabelsEveryField_AndOpeningIsClean()
    {
        using var ctx = CreateContext(new SonosSettings
        {
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }]
        });
        var cut = ctx.Render<ScenesPage>();
        cut.Find(".scenes-new-button").Click();
        AssertLabelledFields(cut);
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#scene-name").Input("Changed");
        Assert.True(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#scene-name").Input("Scene 1");
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
    }

    [Fact]
    public void SceneEditor_SeedsLegacySpeakerIpsWithoutOpeningDirty()
    {
        var legacy = new Scene
        {
            Id = "legacy",
            Name = "Legacy",
            SpeakerIps = ["1.2.3.4"],
            Actions = [],
            IsSyncedPlayback = false
        };
        var settings = new SonosSettings
        {
            MaxVolume = 40,
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }, new() { Name = "Office", IpAddress = "1.2.3.5" }],
            Scenes = [legacy]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScenesPage>();

        cut.Find(".scene-select-link").Click();

        Assert.True(cut.Find("#scene-speaker-1-2-3-4-include").HasAttribute("checked"));
        Assert.False(cut.Find("#scene-speaker-1-2-3-5-include").HasAttribute("checked"));
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
    }

    [Fact]
    public void SceneEditor_ClampsExistingVolumeToCurrentLimitWithoutOpeningDirty()
    {
        var settings = new SonosSettings
        {
            MaxVolume = 40,
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }, new() { Name = "Office", IpAddress = "1.2.3.5" }],
            Scenes = [new()
            {
                Id = "high-volume", Name = "High volume", IsSyncedPlayback = false,
                Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = true, Volume = 80 }, new() { SpeakerIp = "1.2.3.5", IncludeInPlayback = true, Volume = null }]
            }]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScenesPage>();

        cut.Find(".scene-select-link").Click();

        Assert.Equal("40", cut.Find("#scene-speaker-1-2-3-4-volume").GetAttribute("value"));
        Assert.Equal("", cut.Find("#scene-speaker-1-2-3-5-volume").GetAttribute("value"));
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
    }

    [Fact]
    public void SceneEditor_ConcurrentMaximumReductionRefreshesCapAndRetainsDirtyDraft()
    {
        var store = new SonosSettings
        {
            MaxVolume = 100,
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }],
            Scenes = [new()
            {
                Id = "high-volume", Name = "High volume", IsSyncedPlayback = false,
                Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = true, Volume = 80 }]
            }]
        };
        using var ctx = CreateSnapshotContext(store);
        var cut = ctx.Render<ScenesPage>();
        cut.Find(".scene-select-link").Click();
        store.MaxVolume = 40;

        cut.Find(".responsive-editor-dialog .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Contains("latest maximum volume was applied", cut.Markup));
        Assert.Equal("40", cut.Find("#scene-speaker-1-2-3-4-volume").GetAttribute("value"));
        Assert.True(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        Assert.Equal(80, store.Scenes[0].Actions[0].Volume);
    }

    [Fact]
    public void SceneEditor_SettingsChangeEventClampsDraftAndPreservesDirtyBaseline()
    {
        var store = new SonosSettings
        {
            MaxVolume = 100,
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }],
            Scenes = [new()
            {
                Id = "high-volume", Name = "High volume", IsSyncedPlayback = false,
                Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = true, Volume = 80 }]
            }]
        };
        using var ctx = CreateSnapshotContext(store);
        var cut = ctx.Render<ScenesPage>();
        cut.Find(".scene-select-link").Click();
        cut.Find("#scene-name").Input("User edit");
        store.MaxVolume = 40;
        ctx.Services.GetRequiredService<PlaybackSnapshotStore>().NotifySettingsChanged("reduced-limit");

        cut.WaitForAssertion(() => Assert.Contains("Maximum volume changed to 40", cut.Markup));

        Assert.Equal("40", cut.Find("#scene-speaker-1-2-3-4-volume").GetAttribute("value"));
        Assert.Equal("User edit", cut.Find("#scene-name").GetAttribute("value"));
        Assert.True(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
    }

    [Fact]
    public void SceneEditor_AllExcludedActionsCannotBeSavedOrApplied()
    {
        var settings = new SonosSettings
        {
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }],
            Scenes = [new()
            {
                Id = "empty-selection", Name = "Empty selection", SpeakerIps = ["1.2.3.4"],
                Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = false }]
            }]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScenesPage>();

        cut.Find(".scene-select-link").Click();
        cut.Find(".responsive-editor-dialog .btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("Select at least one speaker", cut.Markup));
        cut.Find(".responsive-editor-dialog .btn-outline-success").Click();
        cut.WaitForAssertion(() => Assert.Contains("Select at least one speaker", cut.Markup));
        Assert.Single(settings.Scenes);
    }

    [Fact]
    public async Task SceneEditor_KeepEditingPreservesDraft_DiscardClosesIt()
    {
        using var ctx = CreateContext(new SonosSettings());
        var cut = ctx.Render<ScenesPage>();
        cut.Find(".scenes-new-button").Click();
        cut.Find("#scene-name").Input("Unsaved scene");
        var closing = cut.Find(".app-dialog__close").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role='alertdialog']")));
        cut.FindAll("button").Single(button => button.TextContent == "Keep editing").Click();
        await closing;
        Assert.Equal("Unsaved scene", cut.Find("#scene-name").GetAttribute("value"));
        closing = cut.Find(".app-dialog__close").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role='alertdialog']")));
        cut.FindAll("button").Single(button => button.TextContent == "Discard changes").Click();
        await closing;
        Assert.Empty(cut.FindAll("#scene-name"));
    }

    [Fact]
    public void SceneEditor_SaveClearsChanges_FailureAndApplyDoNot()
    {
        var settings = new SonosSettings { Speakers = [new() { Name = "Test room", IpAddress = "192.0.2.1" }] };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScenesPage>();
        cut.Find(".scenes-new-button").Click();
        cut.Find("#scene-name").Input("Saved scene");
        cut.Find(".responsive-editor-dialog .btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Single(settings.Scenes));
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#scene-name").Input("Changed scene");
        cut.Find(".responsive-editor-dialog .btn-outline-success").Click();
        cut.WaitForAssertion(() => Assert.Contains("Applied", cut.Markup));
        Assert.True(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#scene-name").Input("");
        cut.Find(".responsive-editor-dialog .btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("Failed to save scene", cut.Markup));
        Assert.True(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        Assert.Equal("Saved scene", settings.Scenes[0].Name);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("operator", false)]
    public void Schedules_ExplainMissingEnabledScene_AndPreserveExistingSchedules(string role, bool canManage)
    {
        var settings = new SonosSettings
        {
            Scenes = [new() { Id = "scene", Enabled = false }],
            ScheduleWindows = [new() { Name = "Existing", SceneId = "scene", IsEnabled = false }]
        };
        using var ctx = CreateContext(settings, role);
        var requested = false;
        var cut = ctx.Render<ScheduleWindowsPage>(parameters => parameters.Add(component => component.OnCreateSceneRequested,
            EventCallback.Factory.Create(this, () => requested = true)));
        Assert.Empty(cut.FindAll(".schedule-new-button"));
        if (canManage)
        {
            Assert.Contains("Create or enable a scene", cut.Markup);
            cut.Find(".schedule-prerequisite button").Click();
            Assert.True(requested);
        }
        cut.Find(".schedule-window-card__main").Click();
        AssertLabelledFields(cut);
        Assert.Equal(!canManage, cut.Find("#schedule-name").HasAttribute("disabled"));
    }

    private static BunitContext CreateContext(SonosSettings settings, string role = "admin")
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles(role);
        var repo = new Mock<ISettingsRepo>();
        repo.Setup(repository => repository.GetSettings()).ReturnsAsync(settings);
        repo.Setup(repository => repository.UpdateSettingsAsync(It.IsAny<Action<SonosSettings>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action<SonosSettings> update, CancellationToken token) => { update(settings); return settings; });
        RepositoryTestServices.Register(ctx.Services, repo.Object);
        ctx.Services.AddSingleton(new PlaybackSnapshotStore());
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneAsync(It.IsAny<Scene>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", null, Array.Empty<string>()));
        ctx.Services.AddSingleton(scenes.Object);
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        return ctx;
    }

    private static BunitContext CreateSnapshotContext(SonosSettings store)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");
        var repo = new Mock<ISettingsRepo>();
        repo.Setup(repository => repository.GetSettings()).ReturnsAsync(() => SettingsChanges.MergeEntity(store, store, store));
        repo.Setup(repository => repository.UpdateSettingsAsync(It.IsAny<Action<SonosSettings>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action<SonosSettings> update, CancellationToken token) => { update(store); return SettingsChanges.MergeEntity(store, store, store); });
        RepositoryTestServices.Register(ctx.Services, repo.Object);
        ctx.Services.AddSingleton(new PlaybackSnapshotStore());
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneAsync(It.IsAny<Scene>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", null, Array.Empty<string>()));
        ctx.Services.AddSingleton(scenes.Object);
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        return ctx;
    }

    private static void AssertLabelledFields<T>(IRenderedComponent<T> cut) where T : IComponent
    {
        foreach (var field in cut.FindAll("input, select"))
        {
            var id = field.Id;
            Assert.True(!string.IsNullOrWhiteSpace(field.GetAttribute("aria-label"))
                || field.Closest("label") is not null
                || (!string.IsNullOrWhiteSpace(id) && cut.FindAll("label").Any(label => label.GetAttribute("for") == id)),
                $"Unlabelled field: {field.OuterHtml}");
        }
    }
}
