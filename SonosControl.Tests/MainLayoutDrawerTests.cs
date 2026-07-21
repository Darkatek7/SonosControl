using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public class MainLayoutDrawerTests
{
    [Fact]
    public void MainLayout_DesktopHeader_ShowsPageContextAndConsolidatedProfileMenu()
    {
        using var ctx = new TestContext();
        ConfigureAuthAndTheme(ctx);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<MainLayout>(parameters => parameters
            .Add(p => p.Body, builder => builder.AddMarkupContent(0, "<p>Body</p>"))
            .Add(p => p.AntiforgeryToken, "token"));

        Assert.Equal("Home", cut.Find(".app-top-bar__page-title").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("details.top-bar-profile"));
        Assert.Empty(cut.FindAll(".top-bar-greeting"));
        Assert.Empty(cut.FindAll(".app-top-bar .theme-toggle"));
    }

    [Fact]
    public void NavMenu_GroupsLinksIntoMainAdministrationAndThemeSections()
    {
        using var ctx = new TestContext();
        ConfigureAuthAndTheme(ctx);

        var cut = ctx.RenderComponent<NavMenu>();

        var labels = cut.FindAll(".nav-section-label").Select(label => label.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "Main", "Administration", "Theme" }, labels);
    }

    [Fact]
    public void MainLayout_MobileMenuButton_TogglesDrawerState()
    {
        using var ctx = new TestContext();
        ConfigureAuthAndTheme(ctx);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<MainLayout>(parameters => parameters
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
        using var ctx = new TestContext();
        ConfigureAuthAndTheme(ctx);

        var navigateCalls = 0;
        var cut = ctx.RenderComponent<NavMenu>(parameters => parameters
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
        using var ctx = new TestContext();
        ConfigureAuthAndTheme(ctx);

        var cut = ctx.RenderComponent<NavMenu>(parameters => parameters
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
        using var ctx = new TestContext();
        ConfigureAuthAndTheme(ctx, role);

        var cut = ctx.RenderComponent<NavMenu>();
        var links = cut.FindAll("a.nav-link").Select(link => link.TextContent.Trim()).ToList();

        Assert.Contains("Home", links);
        Assert.Contains("Library", links);
        Assert.Contains("Automation", links);
        Assert.Contains("Insights", links);
        Assert.Equal(expectsAdministration, links.Contains("Administration"));
    }

    private static void ConfigureAuthAndTheme(TestContext ctx, params string[] roles)
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo
            .Setup(repo => repo.GetSettings())
            .ReturnsAsync(new SonosSettings());

        var sonosRepo = new Mock<ISonosConnectorRepo>();

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(uow => uow.ISettingsRepo).Returns(settingsRepo.Object);
        unitOfWork.SetupGet(uow => uow.ISonosConnectorRepo).Returns(sonosRepo.Object);

        ctx.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"main-layout-tests-{Guid.NewGuid()}"));
        ctx.Services.AddSingleton(unitOfWork.Object);
        ctx.Services.AddSingleton(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        ctx.Services.AddSingleton(userManager.Object);
        ctx.Services.AddSingleton<ThemeService>(serviceProvider =>
            new ThemeService(
                serviceProvider.GetRequiredService<AuthenticationStateProvider>(),
                userManager.Object,
                Mock.Of<ILogger<ThemeService>>()));

        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles(roles.Length == 0 ? ["admin"] : roles);
    }
}
