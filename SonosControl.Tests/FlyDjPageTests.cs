using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Models;
using SonosControl.Web.Pages;
using SonosControl.Web.Services;
using SonosControl.Web.Services.FlyDj;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public class FlyDjPageTests
{
    [Fact]
    public void PausedSimulationHidesRetainedMeasurementsAndNamesTheLastChoice()
    {
        using var harness = new Harness();
        var cut = harness.Render();
        harness.Store.Update(state => state with
        {
            Status = FlyDjConductorStatus.Yielded,
            Simulation = Simulation("stopped"),
            CurrentStationName = null,
            LastDecision = Decision()
        });
        cut.WaitForAssertion(() =>
        {
            var activity = cut.Find("[data-qa='flydj-activity']").TextContent;
            Assert.Contains("Simulation paused", activity);
            Assert.DoesNotContain("14.2 Hz", activity);
            Assert.DoesNotContain("1,840", activity);
            Assert.All(cut.FindAll("[data-qa='flydj-activity'] polyline"), line => Assert.True(string.IsNullOrEmpty(line.GetAttribute("points"))));
            Assert.Contains("Last FlyDJ choice", cut.Find("[data-qa='flydj-now']").TextContent);
            Assert.Contains("Radio One", cut.Find("[data-qa='flydj-now']").TextContent);
        });
        harness.Store.Update(state => state with { Simulation = Simulation("continuous"), Status = FlyDjConductorStatus.Playing });
        cut.WaitForAssertion(() => Assert.Contains("14.2 Hz", cut.Find("[data-qa='flydj-activity']").TextContent));
    }

    [Fact]
    public void RollingEvidenceShowsObservationDeadlineSampleCountAgeAndFailures()
    {
        using var harness = new Harness();
        harness.Conductor.SetupGet(conductor => conductor.IsRunning).Returns(true);
        var cut = harness.Render();
        harness.Store.Update(state => state with
        {
            Status = FlyDjConductorStatus.Auditioning,
            IsScheduled = true,
            SidecarReachable = true,
            ObservationWindowSeconds = 600,
            ObservationEndsAtUtc = Moment.AddMinutes(10),
            Evidence = [new FlyDjStationEvidence("Radio One", "radio.example/live", 4, 2, Moment, 1.25, false, "Collecting samples (4/6); last error: timeout")]
        });
        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("[data-qa='flydj-evidence']").TextContent;
            Assert.Contains("Radio One", text);
            Assert.Contains("4 valid samples", text);
            Assert.Contains("2 failed", text);
            Assert.Contains("Insufficient evidence", text);
            Assert.Contains("Initial observation ends", text);
            Assert.Contains("timeout", text);
            Assert.Contains("Observing stations before playback", cut.Markup);
            Assert.DoesNotContain("FlyDJ is playing for a schedule", cut.Markup);
        });
    }

    private static readonly DateTimeOffset Moment = DateTimeOffset.UtcNow;

    [Fact]
    public void ContextSelectionAndMeasurementVariationAreShownWithoutDeviceCommands()
    {
        using var harness = new Harness();
        harness.Conductor.SetupGet(conductor => conductor.IsRunning).Returns(true);
        harness.Conductor.Setup(conductor => conductor.SelectSessionAsync("second", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var cut = harness.Render();
        Publish(harness, FlyDjConductorStatus.Auditioning, state => state with
        {
            IsScheduled = true,
            IsPrewarming = true,
            ScheduledStartUtc = Moment.AddMinutes(5),
            SelectedSessionId = "first",
            Sessions = [new("first", "Morning", FlyDjConductorStatus.Auditioning, true, null, []), new("second", "Office", FlyDjConductorStatus.Playing, false, "Radio Two", ["10.0.0.2"])],
            Evidence = [new("Radio One", "url", 10, 0, Moment, 1.2, true, null) { ScoreVariation = .25 }]
        });
        cut.WaitForAssertion(() => Assert.Contains("Typical score variation", cut.Find("[data-qa='flydj-evidence']").TextContent));
        Assert.Contains("Listening before the schedule starts", cut.Markup);
        cut.Find("#flydj-session").Change("second");
        harness.Conductor.Verify(conductor => conductor.SelectSessionAsync("second", It.IsAny<CancellationToken>()), Times.Once);
        harness.Conductor.Verify(conductor => conductor.StartAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Conductor.Verify(conductor => conductor.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Harness : IDisposable
    {
        public BunitContext Context { get; } = new();
        public Mock<IFlyDjClient> Client { get; } = new();
        public Mock<IFlyDjConductor> Conductor { get; } = new();
        public Mock<ISettingsRepo> Settings { get; } = new();
        public FlyDjStateStore Store { get; }
        public SonosSettings Current { get; }

        public Harness(SonosSettings? settings = null, bool enabled = true, params string[] roles)
        {
            Current = settings ?? DefaultSettings();
            Context.JSInterop.Mode = JSRuntimeMode.Loose;
            Settings.Setup(repo => repo.GetSettings()).ReturnsAsync(Current);
            Settings.Setup(repo => repo.UpdateSettingsAsync(It.IsAny<Action<SonosSettings>>(), It.IsAny<CancellationToken>()))
                .Returns((Action<SonosSettings> update, CancellationToken _) =>
                {
                    update(Current);
                    return Task.FromResult(Current);
                });
            Client.Setup(client => client.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Simulation());
            Client.Setup(client => client.GetModelAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Model());
            Store = new FlyDjStateStore(Client.Object, TimeProvider.System, NullLogger<FlyDjStateStore>.Instance);

            Context.Services.AddSingleton<IOptions<FlyDjOptions>>(Options.Create(new FlyDjOptions { Enabled = enabled }));
            if (enabled)
            {
                Context.Services.AddSingleton(Store);
                Context.Services.AddSingleton(Conductor.Object);
            }
            Context.Services.AddSingleton(Settings.Object);
            Context.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
            Context.Services.AddSingleton(TimeProvider.System);
            var auth = Context.AddAuthorization();
            auth.SetAuthorized("tester");
            auth.SetRoles(roles.Length == 0 ? ["operator"] : roles);
        }

        public IRenderedComponent<FlyDjPage> Render()
        {
            var cut = Context.Render<FlyDjPage>();
            // Wait for user and settings loading to finish; interacting earlier races OnInitializedAsync.
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-qa='flydj-page'][data-ready='true']")));
            // The first-render work (viewer attach, refresh, brain probe) re-renders the page; events sent
            // before it settles can hit handlers that the re-render has already replaced and are dropped.
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-qa='flydj-page'] [data-mode='loading']")));
            return cut;
        }

        public void Dispose()
        {
            Store.Dispose();
            Context.Dispose();
        }
    }

    private static SonosSettings DefaultSettings(bool withRoom = true) => new()
    {
        MaxVolume = 40,
        Speakers = [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }, new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.2" }],
        Stations = [new TuneInStation { Name = "Radio One", Url = "radio1.example.com/live" }, new TuneInStation { Name = "Radio Two", Url = "radio2.example.com/live" }],
        FlyDj = new FlyDjSettings { TargetSpeakerIps = withRoom ? ["10.0.0.1"] : [], Volume = 20 }
    };

    private static FlyDjSimulationState Simulation(string mode = "bursting") => new(
        mode, mode != "stopped", 1, 1234, 0.92, 310.5, 1840, 0.84,
        new Dictionary<string, double> { ["approach"] = 14.2, ["startle"] = 3.1, ["arousal"] = 22.0 },
        null, null, "ready");

    private static FlyDjModelInfo Model(string status = "ready", double progress = 1.0) => new(
        "flywire-783", "auditory", status, progress, null, 7984, 912345, 4567890,
        [
            new FlyDjPopulation(1, "jo_a", "JO-A (high-frequency sound)", "input", 120),
            new FlyDjPopulation(4, "approach", "Approach / courtship song", "output", 40)
        ],
        [new FlyDjAttribution("Dorkenwald et al. 2024", "https://doi.org/10.1038/s41586-024-07558-y")],
        new FlyDjShuffleControl(2.41, 0.37, 6.5, 8));

    private static FlyDjDecisionView Decision(long id = 1, bool overridden = false) => new(
        id, Moment.AddMinutes(-5), "Radio One", "radio1.example.com/live", ["10.0.0.1"],
        "Radio One drew the most approach and the least startle.",
        [
            new FlyDjCandidateScore("Radio One", "radio1.example.com/live", 1.27, 0.2, 0.95, 0.7, null, null),
            new FlyDjCandidateScore("Radio Two", "radio2.example.com/live", null, 0, 0, 0, null, "fetch_failed")
        ],
        600, overridden);

    private static void Publish(Harness harness, FlyDjConductorStatus status, Func<FlyDjDashboardState, FlyDjDashboardState>? more = null)
        => harness.Store.Update(state =>
        {
            var next = state with { Status = status, SidecarReachable = true, Simulation = Simulation(), Model = Model() };
            return more is null ? next : more(next);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NavMenu_ShowsFlyDjOnlyWhenEnabled_AndKeepsSectionLabels(bool enabled)
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var settings = new Mock<ISettingsRepo>();
        settings.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings());
        var userManager = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        ctx.Services.AddSingleton(settings.Object);
        ctx.Services.AddSingleton(userManager.Object);
        ctx.Services.AddSingleton<ThemeService>(provider => new ThemeService(
            provider.GetRequiredService<AuthenticationStateProvider>(), userManager.Object, Mock.Of<ILogger<ThemeService>>()));
        ctx.Services.AddSingleton<IOptions<FlyDjOptions>>(Options.Create(new FlyDjOptions { Enabled = enabled }));
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("operator");

        var cut = ctx.Render<NavMenu>();

        var links = cut.FindAll("a.nav-link").Select(link => link.TextContent.Trim()).ToList();
        Assert.Equal(enabled, links.Contains("FlyDJ"));
        if (enabled) Assert.Equal("flydj", cut.Find("a.nav-link[href='flydj']").GetAttribute("href"));
        var labels = cut.FindAll(".nav-section-label").Select(label => label.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "Main", "Theme" }, labels); // operators never see Administration
    }

    [Theory]
    [InlineData(FlyDjConductorStatus.Stopped, "Stopped")]
    [InlineData(FlyDjConductorStatus.Playing, "Playing Radio One")]
    [InlineData(FlyDjConductorStatus.Yielded, "Yielded to manual control")]
    [InlineData(FlyDjConductorStatus.Error, "Error")]
    public void Page_RendersStatusStatisticsAndDecisionEvidence(FlyDjConductorStatus status, string pill)
    {
        using var harness = new Harness();
        var decision = Decision();
        Publish(harness, status, state => state with
        {
            StatusMessage = status == FlyDjConductorStatus.Error ? "The brain went quiet." : null,
            CurrentStationName = "Radio One",
            CurrentStationUrl = "radio1.example.com/live",
            ActiveSpeakerIps = ["10.0.0.1"],
            YieldedSpeakers = status == FlyDjConductorStatus.Yielded
                ? [new FlyDjYieldedSpeaker("10.0.0.2", "Kitchen", Moment.AddMinutes(30), "Playback was started manually")]
                : [],
            LastDecision = decision,
            RecentDecisions = [decision, Decision(2, overridden: true)],
            NextDecisionAtUtc = Moment.AddMinutes(8)
        });

        var cut = harness.Render();

        Assert.Equal(pill, cut.Find("[data-qa='flydj-status']").TextContent.Trim());
        Assert.NotNull(cut.Find("[data-qa='flydj-page']"));
        var stats = cut.Find("[data-qa='flydj-stats']").TextContent;
        Assert.Contains("7,984", stats);
        Assert.Contains("912,345", stats);
        Assert.Contains("flywire-783", stats);
        Assert.Contains("6.5x", cut.Find("[data-qa='flydj-shuffle']").TextContent);
        Assert.Contains("14.2 Hz", cut.Find("[data-qa='flydj-activity']").TextContent);
        Assert.Contains("Radio One", cut.Find("[data-qa='flydj-now']").TextContent);
        Assert.Contains("Office", cut.Find("[data-qa='flydj-now']").TextContent);

        var rows = cut.FindAll("[data-qa='flydj-candidates'] tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("is-chosen", rows[0].ClassList);
        Assert.Contains("is-failed", rows[1].ClassList);
        Assert.Contains("fetch_failed", rows[1].TextContent);
        Assert.Contains("70%", rows[0].TextContent);
        Assert.Contains("most approach", cut.Find("[data-qa='flydj-reason']").TextContent);
        Assert.Single(cut.FindAll("[data-qa='flydj-recent'] .flydj-badge"));
        Assert.Contains("https://doi.org/10.1038/s41586-024-07558-y", cut.Find("[data-qa='flydj-attribution'] a").GetAttribute("href"));

        if (status == FlyDjConductorStatus.Yielded)
            Assert.Contains("Kitchen", cut.Find("[data-qa='flydj-yielded']").TextContent);
        else
            Assert.Empty(cut.FindAll("[data-qa='flydj-yielded']"));
        if (status == FlyDjConductorStatus.Error)
            Assert.Contains("The brain went quiet.", cut.Find(".alert-danger").TextContent);
    }

    [Fact]
    public void Page_ShowsBuildProgressWhileModelIsBuilding()
    {
        using var harness = new Harness();
        harness.Client.Setup(client => client.GetModelAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Model("building", 0.42));
        Publish(harness, FlyDjConductorStatus.Stopped, state => state with { Model = Model("building", 0.42) });

        var cut = harness.Render();

        Assert.Contains("42%", cut.Find("[data-qa='flydj-build-progress']").TextContent);
        Assert.Equal("42", cut.Find("[data-qa='flydj-build-progress'] progress").GetAttribute("value"));
    }

    [Fact]
    public void Page_FallsBackToPopulationBarsWhenTheBrainViewIsUnavailable()
    {
        using var harness = new Harness();
        Publish(harness, FlyDjConductorStatus.Stopped);

        var cut = harness.Render();

        // Loose JS interop returns nothing for the init call, which the page treats as "no WebGL".
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-qa='flydj-brain-fallback']")));
        Assert.True(cut.Find("[data-qa='flydj-brain-canvas']").HasAttribute("hidden"));
        Assert.Equal(3, cut.FindAll("[data-qa='flydj-brain-fallback'] .flydj-bar-row").Count);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    [InlineData("superadmin")]
    public void EveryRoleCanStartFlyDjAndEditItsSettings(string role)
    {
        using var harness = new Harness(null, true, role);
        harness.Conductor.SetupGet(c => c.IsRunning).Returns(false);
        harness.Conductor.Setup(c => c.StartAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var cut = harness.Render();

        Assert.NotEmpty(cut.FindAll("[data-qa='flydj-settings']"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-qa='flydj-toggle']").HasAttribute("disabled")));
        cut.Find("[data-qa='flydj-toggle']").Click();
        cut.WaitForAssertion(() => harness.Conductor.Verify(c => c.StartAsync("tester", It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void PageControllerAndNavShareOneRoleListCoveringEverySeededRole()
    {
        var roles = FlyDjServiceCollectionExtensions.AllowedRoles.Split(',');
        Assert.Equal(new[] { "admin", "operator", "superadmin" }, roles.Order().ToArray());
        var page = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(
            typeof(FlyDjPage), typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        var api = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(
            typeof(SonosControl.Web.Controllers.FlyDjController), typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(FlyDjServiceCollectionExtensions.AllowedRoles, page.Roles);
        Assert.Equal(FlyDjServiceCollectionExtensions.AllowedRoles, api.Roles);
    }

    [Fact]
    public void Toggle_StartsWhenOff()
    {
        using var harness = new Harness();
        harness.Conductor.SetupGet(c => c.IsRunning).Returns(false);
        harness.Conductor.Setup(c => c.StartAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var cut = harness.Render();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-qa='flydj-toggle']").HasAttribute("disabled")));

        var toggle = cut.Find("[data-qa='flydj-toggle']");
        Assert.Equal("switch", toggle.GetAttribute("role"));
        Assert.Equal("false", toggle.GetAttribute("aria-checked"));
        toggle.Click();

        harness.Conductor.Verify(c => c.StartAsync("tester", It.IsAny<CancellationToken>()), Times.Once);
        harness.Conductor.Verify(c => c.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Toggle_StopsWhenRunning()
    {
        using var harness = new Harness();
        harness.Conductor.SetupGet(c => c.IsRunning).Returns(true);
        harness.Conductor.Setup(c => c.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Publish(harness, FlyDjConductorStatus.Playing);
        var cut = harness.Render();

        var toggle = cut.Find("[data-qa='flydj-toggle']");
        Assert.Equal("true", toggle.GetAttribute("aria-checked"));
        toggle.Click();

        harness.Conductor.Verify(c => c.StopAsync("tester", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Toggle_IsDisabledWithAnExplanationWhenNoRoomIsSelected()
    {
        using var harness = new Harness(DefaultSettings(withRoom: false));
        var cut = harness.Render();

        var toggle = cut.Find("[data-qa='flydj-toggle']");
        Assert.True(toggle.HasAttribute("disabled"));
        Assert.Contains("Choose at least one target room", cut.Find("#flydj-toggle-hint").TextContent);
        Assert.Equal("flydj-toggle-hint", toggle.GetAttribute("aria-describedby"));
        harness.Conductor.Verify(c => c.StartAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Toggle_IsDisabledWhenTheLibraryHasNoStations()
    {
        var settings = DefaultSettings();
        settings.Stations = [];
        using var harness = new Harness(settings);
        var cut = harness.Render();

        Assert.True(cut.Find("[data-qa='flydj-toggle']").HasAttribute("disabled"));
        Assert.Contains("Library", cut.Find("#flydj-toggle-hint").TextContent);
    }

    [Fact]
    public void Toggle_IsDisabledWhileTheSidecarIsUnreachable()
    {
        using var harness = new Harness();
        harness.Client.Setup(client => client.GetStateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new FlyDjUnavailableException("down"));
        var cut = harness.Render();

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("[data-qa='flydj-toggle']").HasAttribute("disabled"));
            Assert.NotEmpty(cut.FindAll("[data-qa='flydj-offline']"));
        });
    }

    [Fact]
    public void Settings_SaveClampsVolumeToTheMaximumAndPersistsSelections()
    {
        using var harness = new Harness();
        var cut = harness.Render();
        Assert.True(cut.Find("[data-qa='flydj-save']").HasAttribute("disabled")); // nothing changed yet

        cut.Find("#flydj-volume-number").Input("90");
        cut.FindAll("[data-qa='flydj-room-option']")[1].Change(true);
        cut.FindAll("[data-qa='flydj-station-option']")[0].Change(true);
        cut.Find("#flydj-max-dwell").Change("30");
        cut.Find("#flydj-auto-resume").Change(true);
        // Renders triggered by events can queue behind a render already in flight.
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-qa='flydj-save']").HasAttribute("disabled")));
        cut.Find("[data-qa='flydj-settings'] form").Submit();

        cut.WaitForAssertion(() =>
        {
            harness.Settings.Verify(repo => repo.UpdateSettingsAsync(It.IsAny<Action<SonosSettings>>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.Contains("FlyDJ settings saved.", cut.Markup);
        });
        var saved = harness.Current.FlyDj;
        Assert.Equal(40, saved.Volume); // settings.MaxVolume
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, saved.TargetSpeakerIps);
        Assert.Equal(new[] { "radio1.example.com/live" }, saved.CandidateStationUrls);
        Assert.Equal(30, saved.MaxDwellMinutes);
        Assert.True(saved.AutoResume);
    }

    [Fact]
    public void Settings_AreHiddenFromUsersWithoutAnEditorRole()
    {
        using var harness = new Harness(null, true, "viewer");
        var cut = harness.Render();

        Assert.Empty(cut.FindAll("[data-qa='flydj-settings']"));
        Assert.True(cut.Find("[data-qa='flydj-toggle']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Page_AttachesAViewerWhileOpenAndReleasesItOnDispose()
    {
        using var harness = new Harness();
        var cut = harness.Render();
        cut.WaitForAssertion(() => Assert.Equal(1, harness.Store.ViewerCount));

        await harness.Context.DisposeComponentsAsync();

        Assert.Equal(0, harness.Store.ViewerCount);
    }

    [Fact]
    public void Page_WithTheFeatureDisabledDoesNotThrowAndLeavesForHome()
    {
        using var harness = new Harness(null, enabled: false);

        var cut = harness.Context.Render<FlyDjPage>();

        Assert.Contains("FlyDJ is not enabled", cut.Markup);
        Assert.Empty(cut.FindAll("[data-qa='flydj-toggle']"));
        var navigation = harness.Context.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.Equal(navigation.BaseUri, navigation.Uri));
    }
}
