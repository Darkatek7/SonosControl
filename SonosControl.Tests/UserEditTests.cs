using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Models;
using SonosControl.Web.Pages;
using Xunit;

namespace SonosControl.Tests;

public class UserEditTests
{
    [Fact]
    public void Account_MissingResolvedUserStopsWithVisibleRecoveryAction()
    {
        using var ctx = new TestContext();
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("missing-user");
        auth.SetRoles("operator");
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());

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
        userManager.Setup(manager => manager.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser?)null);
        ctx.Services.AddSingleton(userManager.Object);

        var cut = ctx.RenderComponent<UserEdit>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("could not be resolved", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(cut.FindAll(".spinner-border"));
            Assert.Equal(
                "/auth/login",
                cut.Find("a[href='/auth/login']").GetAttribute("href"));
        });
    }
}
