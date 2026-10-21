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
    private const string SaveButton = ".responsive-editor-dialog .app-dialog__actions .btn-primary";

    private static ScheduleWindow TemplateWithScene(Scene scene, bool timed = false) =>
        new() { Id = "window", Name = "Window", HasTimeSchedule = timed, IsEnabled = timed, Playback = scene };

    [Fact]
    public void ScheduleEditor_LabelsEveryField_AndOpeningIsClean()
    {
        using var ctx = CreateContext(new SonosSettings
        {
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }]
        });
        var cut = ctx.Render<ScheduleWindowsPage>();
        cut.Find(".schedule-new-button").Click();
        AssertLabelledFields(cut);
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#schedule-name").Input("Changed");
        Assert.True(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#schedule-name").Input("Schedule 1");
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
    }

    [Fact]
    public void ScheduleEditor_SeedsLegacySceneSpeakerIpsWithoutOpeningDirty()
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
            Scenes = [legacy],
            ScheduleWindows = [new() { Id = "window", Name = "Legacy template", HasTimeSchedule = false, IsEnabled = false, SceneId = "legacy" }]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScheduleWindowsPage>();

        cut.Find(".schedule-window-select-link").Click();

        Assert.True(cut.Find("input[aria-label='Include Kitchen in playback']").HasAttribute("checked"));
        Assert.False(cut.Find("input[aria-label='Include Office in playback']").HasAttribute("checked"));
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
    }

    [Fact]
    public void ScheduleEditor_ClampsExistingVolumeToCurrentLimitWithoutOpeningDirty()
    {
        var settings = new SonosSettings
        {
            MaxVolume = 40,
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }, new() { Name = "Office", IpAddress = "1.2.3.5" }],
            ScheduleWindows = [TemplateWithScene(new()
            {
                Id = "high-volume", Name = "High volume", IsSyncedPlayback = false,
                Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = true, Volume = 80 }, new() { SpeakerIp = "1.2.3.5", IncludeInPlayback = true, Volume = null }]
            })]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScheduleWindowsPage>();

        cut.Find(".schedule-window-select-link").Click();

        Assert.Equal("40", cut.Find("#payload-speaker-1-2-3-4-volume").GetAttribute("value"));
        Assert.Equal("", cut.Find("#payload-speaker-1-2-3-5-volume").GetAttribute("value"));
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
    }

    [Fact]
    public void ScheduleEditor_AllExcludedSpeakersCannotBeSaved()
    {
        var settings = new SonosSettings
        {
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }],
            ScheduleWindows = [TemplateWithScene(new()
            {
                Id = "empty-selection", Name = "Empty selection", SpeakerIps = ["1.2.3.4"],
                Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = false }]
            })]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScheduleWindowsPage>();

        cut.Find(".schedule-window-select-link").Click();
        cut.Find(SaveButton).Click();
        cut.WaitForAssertion(() => Assert.Contains("Select at least one speaker", cut.Markup));
        Assert.Single(settings.ScheduleWindows);
        Assert.False(settings.ScheduleWindows[0].Playback!.Actions[0].IncludeInPlayback);
    }

    [Fact]
    public async Task ScheduleEditor_KeepEditingPreservesDraft_DiscardClosesIt()
    {
        using var ctx = CreateContext(new SonosSettings());
        var cut = ctx.Render<ScheduleWindowsPage>();
        cut.Find(".schedule-new-button").Click();
        cut.Find("#schedule-name").Input("Unsaved schedule");
        var closing = cut.Find(".app-dialog__close").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role='alertdialog']")));
        cut.FindAll("button").Single(button => button.TextContent == "Keep editing").Click();
        await closing;
        Assert.Equal("Unsaved schedule", cut.Find("#schedule-name").GetAttribute("value"));
        closing = cut.Find(".app-dialog__close").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role='alertdialog']")));
        cut.FindAll("button").Single(button => button.TextContent == "Discard changes").Click();
        await closing;
        Assert.Empty(cut.FindAll("#schedule-name"));
    }

    [Fact]
    public void ScheduleEditor_SaveClearsChanges_FailureDoesNot()
    {
        var settings = new SonosSettings { Speakers = [new() { Name = "Test room", IpAddress = "192.0.2.1" }] };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScheduleWindowsPage>();
        cut.Find(".schedule-new-button").Click();
        cut.Find("#schedule-name").Input("Saved schedule");
        cut.Find(SaveButton).Click();
        cut.WaitForAssertion(() => Assert.Single(settings.ScheduleWindows));
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#schedule-name").Input("");
        cut.Find(SaveButton).Click();
        cut.WaitForAssertion(() => Assert.Contains("Enter a name", cut.Markup));
        Assert.True(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        Assert.Equal("Saved schedule", settings.ScheduleWindows[0].Name);
    }

    [Fact]
    public void TimeControl_RoundTripsScheduleToTemplateAndBack_KeepingHiddenData()
    {
        var settings = new SonosSettings
        {
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }],
            ScheduleWindows = [new()
            {
                Id = "window", Name = "Morning", IsEnabled = true, HasTimeSchedule = true,
                StartTime = new TimeOnly(7, 15), StopTime = new TimeOnly(9, 30), FadeInSeconds = 12,
                ExcludedDates = [new DateOnly(2026, 12, 25)],
                Playback = new() { Id = "morning-playback", Name = "Morning", Enabled = true, Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = true }] }
            }]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScheduleWindowsPage>();
        cut.Find(".schedule-window-select-link").Click();
        var guard = cut.FindComponent<UnsavedChangesGuard>();
        Assert.Equal("07:15", cut.Find("#schedule-start").GetAttribute("value"));
        Assert.NotEmpty(cut.FindAll("#schedule-fade-in"));

        cut.Find("#schedule-time-control").Change(false);

        Assert.Empty(cut.FindAll("#schedule-start"));
        Assert.Empty(cut.FindAll("#schedule-recurrence"));
        Assert.Empty(cut.FindAll("#schedule-fade-in"));
        Assert.Contains("Save template", cut.Find(SaveButton).TextContent);
        Assert.True(guard.Instance.HasChanges);
        cut.Find(SaveButton).Click();
        cut.WaitForAssertion(() => Assert.False(settings.ScheduleWindows[0].HasTimeSchedule));
        Assert.Equal(new TimeOnly(7, 15), settings.ScheduleWindows[0].StartTime);
        Assert.Equal(12, settings.ScheduleWindows[0].FadeInSeconds);
        Assert.Single(settings.ScheduleWindows[0].ExcludedDates);
        Assert.Contains("Start now", cut.Markup);

        cut.Find("#schedule-time-control").Change(true);

        Assert.Equal("07:15", cut.Find("#schedule-start").GetAttribute("value"));
        Assert.Equal("12", cut.Find("#schedule-fade-in").GetAttribute("value"));
        Assert.Contains("Save schedule", cut.Find(SaveButton).TextContent);
        cut.Find(SaveButton).Click();
        cut.WaitForAssertion(() => Assert.True(settings.ScheduleWindows[0].HasTimeSchedule));
        Assert.Equal(new DateOnly(2026, 12, 25), Assert.Single(settings.ScheduleWindows[0].ExcludedDates));
    }

    [Fact]
    public void TimeControl_ValidationAppliesToTimedSchedulesButNotHiddenTemplateTiming()
    {
        var settings = new SonosSettings
        {
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }],
            ScheduleWindows = [new()
            {
                Id = "window", Name = "Custom", IsEnabled = true, HasTimeSchedule = true,
                RecurrenceType = ScheduleRecurrenceType.CustomDays, DaysOfWeek = [],
                Playback = new() { Id = "custom-playback", Name = "Custom", Enabled = true, Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = true }] }
            }]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScheduleWindowsPage>();
        cut.Find(".schedule-window-select-link").Click();

        cut.Find(SaveButton).Click();
        cut.WaitForAssertion(() => Assert.Contains("Choose at least one day", cut.Markup));
        Assert.True(settings.ScheduleWindows[0].HasTimeSchedule);

        cut.Find("#schedule-time-control").Change(false);
        cut.Find(SaveButton).Click();

        cut.WaitForAssertion(() => Assert.False(settings.ScheduleWindows[0].HasTimeSchedule));
    }

    [Fact]
    public void AdvancedSection_GroupsPriorityDatesFadesAndExclusions_AndEditsWindowExclusions()
    {
        var settings = new SonosSettings
        {
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }],
            ScheduleWindows = [new()
            {
                Id = "window", Name = "Morning", IsEnabled = true, HasTimeSchedule = true,
                Playback = new() { Id = "morning-playback", Name = "Morning", Enabled = true, Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = true }] }
            }]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScheduleWindowsPage>();
        cut.Find(".schedule-window-select-link").Click();

        var advanced = cut.Find("[data-qa='schedule-advanced']");
        foreach (var id in new[] { "schedule-priority", "schedule-start-date", "schedule-end-date", "schedule-fade-in", "schedule-fade-out", "schedule-exclusion-date" })
            Assert.NotNull(advanced.QuerySelector("#" + id));
        Assert.Null(cut.Find(".schedule-editor-grid > section").QuerySelector("#schedule-priority"));

        cut.Find("#schedule-exclusion-date").Change("2026-12-25");
        cut.Find("#schedule-exclusion-repeat").Change("yearly");
        cut.FindAll("[data-qa='schedule-advanced'] button").Single(button => button.TextContent == "Add exclusion").Click();
        cut.Find("#schedule-exclusion-repeat").Change("once");
        cut.Find("#schedule-exclusion-date").Change("2026-12-26");
        cut.FindAll("[data-qa='schedule-advanced'] button").Single(button => button.TextContent == "Add exclusion").Click();
        Assert.True(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("[aria-label='Remove excluded date 26/12/2026']").Click();
        cut.Find(SaveButton).Click();

        cut.WaitForAssertion(() => Assert.Single(settings.ScheduleWindows[0].AnnualExcludedDates));
        Assert.Empty(settings.ScheduleWindows[0].ExcludedDates);
    }

    [Fact]
    public void ManualAutoStopSettings_RefreshesCleanDraftButKeepsDirtyDraft()
    {
        using var ctx = CreateContext(new SonosSettings());
        var speaker = new SonosSpeaker { Name = "Kitchen", IpAddress = "1.2.3.4", ManualAutoStopMinutes = 30 };
        var cut = ctx.Render<ManualAutoStopSettings>(parameters => parameters.Add(p => p.Speaker, speaker));
        Assert.Equal("30", cut.Find("input[type='number']").GetAttribute("value"));

        cut.Render(parameters => parameters.Add(p => p.Speaker, new SonosSpeaker { Name = "Kitchen", IpAddress = "1.2.3.4", ManualAutoStopMinutes = 45 }));
        Assert.Equal("45", cut.Find("input[type='number']").GetAttribute("value"));

        cut.Find("input[type='number']").Change("50");
        cut.Render(parameters => parameters.Add(p => p.Speaker, new SonosSpeaker { Name = "Kitchen", IpAddress = "1.2.3.4", ManualAutoStopMinutes = 60 }));
        Assert.Equal("50", cut.Find("input[type='number']").GetAttribute("value"));
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("operator", false)]
    public void Schedules_CanBeCreatedWithoutEnabledScene_AndPreserveExistingSchedules(string role, bool canManage)
    {
        var settings = new SonosSettings
        {
            Scenes = [new() { Id = "scene", Enabled = false }],
            ScheduleWindows = [new() { Name = "Existing", SceneId = "scene", IsEnabled = false }]
        };
        using var ctx = CreateContext(settings, role);
        var cut = ctx.Render<ScheduleWindowsPage>();
        if (canManage)
        {
            Assert.Single(cut.FindAll(".schedule-new-button"));
            cut.Find(".schedule-new-button").Click();
            Assert.NotNull(cut.Find("#schedule-name"));
            Assert.NotNull(cut.Find("#payload-source-mode"));
        }
        else
        {
            cut.Find(".schedule-window-card__main").Click();
        }
        AssertLabelledFields(cut);
        Assert.Equal(!canManage, cut.Find("#schedule-name").HasAttribute("disabled"));
    }

    [Fact]
    public void FlyDjPoolAndPrewarmingRoundTripAndEmptyPoolCannotBeSaved()
    {
        var settings = new SonosSettings
        {
            Speakers = [new() { Name = "Kitchen", IpAddress = "1.2.3.4" }],
            Stations = [new() { Name = "One", Url = "https://one.example/live" }, new() { Name = "Two", Url = "https://two.example/live" }],
            ScheduleWindows = [TemplateWithScene(new() { Id = "dj", Name = "FlyDJ", SourceType = SceneSourceType.FlyDj,
                Actions = [new() { SpeakerIp = "1.2.3.4", IncludeInPlayback = true }] }, timed: true)]
        };
        using var ctx = CreateContext(settings);
        var cut = ctx.Render<ScheduleWindowsPage>();
        cut.Find(".schedule-window-select-link").Click();
        Assert.Null(settings.ScheduleWindows[0].FlyDjCandidateStationUrls);
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#schedule-flydj-prewarm").Change(true);
        Assert.True(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#schedule-flydj-prewarm").Change(false);
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        cut.Find("#schedule-flydj-custom-pool").Change(true);
        cut.Find("#schedule-flydj-prewarm").Change(true);
        cut.FindAll(".schedule-flydj-stations input")[0].Change(false);
        cut.FindAll(".schedule-flydj-stations input")[1].Change(false);
        cut.Find(SaveButton).Click();
        cut.WaitForAssertion(() => Assert.Contains("Select at least one available station", cut.Markup));
        Assert.Null(settings.ScheduleWindows[0].FlyDjCandidateStationUrls);
        cut.FindAll(".schedule-flydj-stations input")[1].Change(true);
        cut.Find(SaveButton).Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "https://two.example/live" }, settings.ScheduleWindows[0].FlyDjCandidateStationUrls));
        Assert.True(settings.ScheduleWindows[0].FlyDjPrewarm);
        Assert.False(cut.FindComponent<UnsavedChangesGuard>().Instance.HasChanges);
        AssertLabelledFields(cut);
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
