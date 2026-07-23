using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Pages;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class AdministrationPageNavigationTests
{
    [Fact]
    public void AdministrationPage_UpdatesContentImmediately_WhenTabRouteChanges()
    {
        using var ctx = new TestContext();
        ConfigureServices(ctx);

        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/administration/devices");
        var cut = ctx.RenderComponent<AdministrationPage>();

        cut.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Users")
            .Click();

        cut.WaitForAssertion(() =>
        {
            var usersTab = cut.FindAll("button")
                .Single(button => button.TextContent.Trim() == "Users");

            Assert.Equal("http://localhost/administration/users", navigation.Uri);
            Assert.Contains("is-active", usersTab.ClassList);
            Assert.Equal("true", usersTab.GetAttribute("aria-selected"));
            Assert.Single(cut.FindComponents<Stub<UserManagement>>());
            Assert.Empty(cut.FindComponents<Stub<DevicesPage>>());
        });
    }

    [Fact]
    public void AdministrationPage_FollowsExternalLocationChanges()
    {
        using var ctx = new TestContext();
        ConfigureServices(ctx);

        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/administration/devices");
        var cut = ctx.RenderComponent<AdministrationPage>();

        navigation.NavigateTo("/administration/backups");

        cut.WaitForAssertion(() =>
        {
            var backupsTab = cut.FindAll("button")
                .Single(button => button.TextContent.Trim() == "Backups");

            Assert.Contains("is-active", backupsTab.ClassList);
            Assert.Equal("true", backupsTab.GetAttribute("aria-selected"));
            Assert.Single(cut.FindComponents<Stub<SettingsBackupsPage>>());
            Assert.Empty(cut.FindComponents<Stub<DevicesPage>>());
        });
    }

    private static void ConfigureServices(TestContext ctx)
    {
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        ctx.Services.AddSingleton(new AutomationRuntimeStatus());
        ctx.ComponentFactories.AddStub<DevicesPage>();
        ctx.ComponentFactories.AddStub<ConfigPage>();
        ctx.ComponentFactories.AddStub<UserManagement>();
        ctx.ComponentFactories.AddStub<SettingsBackupsPage>();
    }
}
