using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Pages;
using SonosControl.Web.Services;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public class MainLayoutDrawerTests
{
    [Fact]
    public void MainLayout_DesktopHeader_ShowsPageContextAndConsolidatedProfileMenu()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureAuthAndTheme(ctx);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<MainLayout>(parameters => parameters
            .Add(p => p.Body, builder => builder.AddMarkupContent(0, "<p>Body</p>"))
            .Add(p => p.AntiforgeryToken, "token"));

        Assert.Equal("Home", cut.Find(".app-top-bar__page-title").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("details.top-bar-profile"));
        Assert.Empty(cut.FindAll(".top-bar-greeting"));
        Assert.Empty(cut.FindAll(".app-top-bar .theme-toggle"));
        Assert.Single(cut.FindAll("[data-qa='global-player-bar'].player-surface--compact"));
    }

    [Fact]
    public void NavMenu_GroupsLinksIntoMainAdministrationAndThemeSections()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureAuthAndTheme(ctx);

        var cut = ctx.Render<NavMenu>();

        var labels = cut.FindAll(".nav-section-label").Select(label => label.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "Main", "Administration", "Theme" }, labels);
    }

    [Fact]
    public void MainLayout_MobileMenuButton_TogglesDrawerState()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureAuthAndTheme(ctx);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<MainLayout>(parameters => parameters
            .Add(p => p.Body, builder => builder.AddMarkupContent(0, "<p>Body</p>"))
            .Add(p => p.AntiforgeryToken, "token"));

        var toggle = cut.Find("button.app-mobile-menu-button");
        Assert.Equal("False", toggle.GetAttribute("aria-expanded"), ignoreCase: true);

        toggle.Click();

        cut.WaitForAssertion(() =>
        {
            var updatedToggle = cut.Find("button.app-mobile-menu-button");
            Assert.Equal("True", updatedToggle.GetAttribute("aria-expanded"), ignoreCase: true);
            Assert.Contains("is-open", cut.Find("aside.app-sidebar").ClassList);
            Assert.NotEmpty(cut.FindAll("button.app-drawer-backdrop"));
        });
    }

    [Fact]
    public void NavMenu_NavigationClick_InvokesOnNavigate()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureAuthAndTheme(ctx);

        var navigateCalls = 0;
        var cut = ctx.Render<NavMenu>(parameters => parameters
            .Add(p => p.IsDrawerOpen, true)
            .Add(p => p.OnNavigate, () => { navigateCalls++; return Task.CompletedTask; }));

        var navLink = cut.Find("a.nav-link");
        navLink.Click();

        Assert.Equal(1, navigateCalls);
        var drawerState = cut.Find(".nav-scrollable").GetAttribute("data-drawer-open");
        Assert.Equal("True", drawerState, ignoreCase: true);
    }

    [Fact]
    public void NavMenu_CloseButton_HasAriaLabel()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureAuthAndTheme(ctx);

        var cut = ctx.Render<NavMenu>(parameters => parameters
            .Add(p => p.IsDrawerOpen, true));

        var closeButton = cut.Find("button.nav-drawer-close");
        Assert.Equal("Close navigation menu", closeButton.GetAttribute("aria-label"));
        Assert.Equal("Close navigation menu", closeButton.GetAttribute("title"));
    }

    [Theory]
    [InlineData("operator", false)]
    [InlineData("admin", true)]
    [InlineData("superadmin", true)]
    public void NavMenu_OnlyShowsAdministrationToAdministrativeRoles(string role, bool expectsAdministration)
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureAuthAndTheme(ctx, role);

        var cut = ctx.Render<NavMenu>();
        var links = cut.FindAll("a.nav-link").Select(link => link.TextContent.Trim()).ToList();

        Assert.Contains("Home", links);
        Assert.Contains("Library", links);
        Assert.Contains("Automation", links);
        Assert.Contains("Insights", links);
        Assert.Equal(expectsAdministration, links.Contains("Administration"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/library")]
    [InlineData("/administration/devices")]
    public async Task MainLayout_TogglesDockAndClearance_WhenFirstAndLastSpeakerChange(string route)
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var settings = new SonosSettings { IP_Adress = string.Empty, Speakers = [] };
        ConfigureAuthAndTheme(ctx, settings);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        var cut = ctx.Render<MainLayout>(p => p.Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<p>Body</p>")));
        var playback = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        Assert.Empty(cut.FindAll("[data-qa='global-player-bar']"));
        Assert.Contains("app-shell--without-player", cut.Find(".app-shell").ClassList);
        Assert.NotEmpty(cut.FindAll(".app-sidebar, #account-menu"));

        settings.Speakers.Add(new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" });
        await cut.InvokeAsync(playback.InitializeAsync);
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-qa='global-player-bar']"));
            Assert.DoesNotContain("app-shell--without-player", cut.Find(".app-shell").ClassList);
        });
        cut.Find(".player-surface__open").Click();
        Assert.Single(cut.FindAll(".player-sheet"));

        settings.Speakers.Clear();
        await cut.InvokeAsync(playback.InitializeAsync);
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-qa='global-player-bar'], .player-sheet"));
            Assert.Contains("app-shell--without-player", cut.Find(".app-shell").ClassList);
        });
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("import")]
    [InlineData("import-all")]
    public void DevicesPage_UpdatesMountedLayout_AfterAddingAndRemovingLastSpeaker(string method)
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var settings = new SonosSettings { IP_Adress = string.Empty, Speakers = [] };
        ConfigureAuthAndTheme(ctx, settings);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var discovery = new Mock<ISonosDeviceDiscoveryService>();
        discovery.Setup(service => service.DiscoverAsync(It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SonosSpeaker> { new() { Name = "Office", IpAddress = "192.0.2.10", Uuid = "uuid:office" } });
        ctx.Services.AddSingleton(discovery.Object);
        ctx.Services.AddSingleton<IDeviceHealthSnapshotStore>(new DeviceHealthSnapshotStore());
        var layout = ctx.Render<MainLayout>();
        var devices = ctx.Render<DevicesPage>();
        Assert.Empty(layout.FindAll("[data-qa='global-player-bar']"));

        if (method == "manual")
        {
            devices.Find("#manual-speaker-name").Change("Office");
            devices.Find("#manual-speaker-ip").Change("192.0.2.10");
            devices.Find(".devices-manual-form button").Click();
        }
        else
        {
            devices.Find(".devices-hero__actions .btn-primary").Click();
            devices.Find(method == "import" ? ".devices-table button" : ".devices-import-all").Click();
        }
        layout.WaitForAssertion(() => Assert.Single(layout.FindAll("[data-qa='global-player-bar']")));
        Assert.Single(settings.Speakers);
        devices.Find("button[aria-label='Remove Office']").Click();
        devices.Find(".app-dialog__actions .btn-danger").Click();
        layout.WaitForAssertion(() => Assert.Empty(layout.FindAll("[data-qa='global-player-bar']")));
        Assert.Empty(settings.Speakers);
    }

    [Fact]
    public void DamagedSettingsShowRecoveryLink_AndNavigationAfterRestoreClearsTheError()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureAuthAndTheme(ctx);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var repository = Mock.Get(ctx.Services.GetRequiredService<ISettingsRepo>());
        repository.Setup(repo => repo.GetSettings()).ThrowsAsync(new SonosControl.DAL.Repos.SettingsReadException("Damaged configuration"));
        var layout = ctx.Render<MainLayout>();
        Assert.Contains("Damaged configuration", layout.Find("article.content [role='alert']").TextContent);
        Assert.Equal("/settings-backups", layout.Find("article.content [role='alert'] a").GetAttribute("href"));
        Assert.Empty(layout.FindAll("[data-qa='global-player-bar']"));
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings());
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/library");
        layout.WaitForAssertion(() => Assert.Empty(layout.FindAll("article.content [role='alert']")));
    }

    [Fact]
    public void NavigationPreservesThePlayerAndItsSleepTimer()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureAuthAndTheme(ctx);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var layout = ctx.Render<MainLayout>();
        var player = layout.FindComponent<GlobalPlayerBar>().Instance;
        layout.Find(".player-surface__open").Click();
        layout.Find(".player-sheet__timer .input-group button").Click();
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/library");
        layout.WaitForAssertion(() =>
        {
            Assert.Same(player, layout.FindComponent<GlobalPlayerBar>().Instance);
            Assert.Contains("pauses at", layout.Find(".player-sheet__timer").TextContent);
        });
    }

    private static void ConfigureAuthAndTheme(BunitContext ctx, params string[] roles)
    {
        ConfigureAuthAndTheme(ctx, new SonosSettings
        {
            IP_Adress = "10.0.0.1",
            Speakers = [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }]
        }, roles);
    }

    private static void ConfigureAuthAndTheme(BunitContext ctx, SonosSettings settings, params string[] roles)
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo
            .Setup(repo => repo.GetSettings())
            .ReturnsAsync(settings);

        var sonosRepo = new Mock<ISonosConnectorRepo>();

        ctx.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"main-layout-tests-{Guid.NewGuid()}"));
        RepositoryTestServices.Register(ctx.Services, settingsRepo.Object, sonosRepo.Object, Mock.Of<IHolidayRepo>());
        ctx.Services.AddSingleton(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        ctx.Services.AddSingleton(userManager.Object);
        ctx.Services.AddSingleton<ThemeService>(serviceProvider =>
            new ThemeService(
                serviceProvider.GetRequiredService<AuthenticationStateProvider>(),
                userManager.Object,
                Mock.Of<ILogger<ThemeService>>()));

        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles(roles.Length == 0 ? ["admin"] : roles);
    }
}
