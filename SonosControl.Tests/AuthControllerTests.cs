using Microsoft.AspNetCore.Mvc;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Controllers;
using Xunit;

namespace SonosControl.Tests;

public class AuthControllerTests
{
    [Fact]
    public async Task Login_LockedOutErrorShowsActionableMessage()
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repository => repository.GetSettings())
            .ReturnsAsync(new SonosSettings { AllowUserRegistration = true });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(value => value.ISettingsRepo).Returns(settingsRepo.Object);
        var controller = new AuthController(null!, unitOfWork.Object);

        var result = await controller.Login(error: "lockedout");

        Assert.IsType<ViewResult>(result);
        var message = Assert.IsType<string>(controller.ViewBag.Error);
        Assert.Contains("temporarily locked", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("five minutes", message, StringComparison.OrdinalIgnoreCase);
    }
}
