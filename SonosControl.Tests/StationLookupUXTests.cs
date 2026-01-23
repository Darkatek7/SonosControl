using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Pages;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace SonosControl.Tests;

public class StationLookupUXTests
{
    [Fact]
    public void StationLookup_HasAccessibleSearchInput()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(ctx);

        var cut = ctx.RenderComponent<StationLookup>();

        // Check for label
        var label = cut.Find("label[for='stationSearch']");
        Assert.NotNull(label);
        Assert.Contains("visually-hidden", label.ClassList);
        Assert.Equal("Search for a radio station", label.TextContent);

        // Check for input
        var input = cut.Find("input#stationSearch");
        Assert.NotNull(input);
    }

    private sealed class TestResources : IDisposable
    {
        public ApplicationDbContext DbContext { get; }

        public TestResources(ApplicationDbContext dbContext)
        {
            DbContext = dbContext;
        }

        public void Dispose()
        {
            DbContext.Dispose();
        }
    }

    private static TestResources ConfigureServices(TestContext ctx)
    {
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);

        var settings = new SonosSettings();
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        ctx.Services.AddSingleton<IUnitOfWork>(unitOfWork.Object);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        return new TestResources(dbContext);
    }
}
