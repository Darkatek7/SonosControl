using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public class MainLayoutDrawerTests
{
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

    private static void ConfigureAuthAndTheme(TestContext ctx)
    {
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());

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
        auth.SetRoles("admin");
    }
}
