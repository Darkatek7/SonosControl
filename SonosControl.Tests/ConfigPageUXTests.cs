using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Pages;
using Xunit;

namespace SonosControl.Tests;

public class ConfigPageUXTests
{
    [Fact]
    public void ConfigPage_WebhookInputs_HaveAccessibilityAttributes()
    {
        using var ctx = new TestContext();

        // --- Setup Dependencies ---

        // Auth
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("admin");

        // DB
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);

        // Settings Mock
        var settings = new SonosSettings
        {
            DiscordWebhookUrl = "https://discord.com/existing",
            TeamsWebhookUrl = "https://teams.com/existing",
            Speakers = new List<SonosSpeaker>()
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.WriteSettings(It.IsAny<SonosSettings>())).Returns(Task.CompletedTask);

        // UoW Mock
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);
        // We don't need connector repo for this specific test part, but good to have if init runs
        unitOfWork.SetupGet(u => u.ISonosConnectorRepo).Returns(Mock.Of<ISonosConnectorRepo>());
        unitOfWork.SetupGet(u => u.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        ctx.Services.AddSingleton<IUnitOfWork>(unitOfWork.Object);

        // --- Render ---
        var cut = ctx.RenderComponent<ConfigPage>();

        // --- Verify Discord Webhook UX ---

        // Initial state: View mode
        var editDiscordBtn = cut.Find("button[aria-label='Edit Discord Webhook']");
        Assert.NotNull(editDiscordBtn);

        // Enter edit mode
        editDiscordBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[aria-label='Discord Webhook URL']");
            Assert.NotNull(input);
            Assert.Equal("url", input.GetAttribute("type"));
            Assert.True(input.HasAttribute("autofocus"));

            var doneBtn = cut.Find("button[aria-label='Save Discord Webhook']");
            Assert.NotNull(doneBtn);
        });

        // --- Verify Teams Webhook UX ---

        // Initial state: View mode
        var editTeamsBtn = cut.Find("button[aria-label='Edit Teams Webhook']");
        Assert.NotNull(editTeamsBtn);

        // Enter edit mode
        editTeamsBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[aria-label='Teams Webhook URL']");
            Assert.NotNull(input);
            Assert.Equal("url", input.GetAttribute("type"));
            Assert.True(input.HasAttribute("autofocus"));

            var doneBtn = cut.Find("button[aria-label='Save Teams Webhook']");
            Assert.NotNull(doneBtn);
        });
    }
}
