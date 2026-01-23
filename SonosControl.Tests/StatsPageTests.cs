using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Radzen;
using Radzen.Blazor;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Pages;
using Xunit;

namespace SonosControl.Tests;

public class StatsPageTests
{
    [Fact]
    public void StatsPage_RendersCharts_WhenDataExists()
    {
        using var ctx = new TestContext();

        // Stub Radzen Components to avoid JSInterop issues and isolate unit test
        // By stubbing RadzenChart and not rendering its ChildContent, we avoid instantiating the inner Series components
        ctx.ComponentFactories.AddStub<RadzenChart>();

        // Setup Auth
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("admin");

        // Setup Db
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);

        // Seed Data
        dbContext.Logs.Add(new LogEntry { Action = "Playback Started", PerformedBy = "TestUser", Timestamp = DateTime.UtcNow });
        dbContext.PlaybackStats.Add(new PlaybackHistory { MediaType = "Spotify", DurationSeconds = 120, TrackName = "Track1", Artist = "Artist1" });
        // Add Station data for the 4th chart
        dbContext.PlaybackStats.Add(new PlaybackHistory { MediaType = "Station", DurationSeconds = 300, TrackName = "Radio 1", Artist = "BBC" });
        dbContext.SaveChanges();

        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);

        // Setup Radzen Services (still needed for some internal logic maybe, but stubs might bypass them)
        ctx.Services.AddScoped<DialogService>();
        ctx.Services.AddScoped<NotificationService>();
        ctx.Services.AddScoped<TooltipService>();
        ctx.Services.AddScoped<ContextMenuService>();
        ctx.Services.AddScoped<Radzen.ThemeService>();

        // IUnitOfWork is injected in _Imports.razor
        var unitOfWork = new Mock<IUnitOfWork>();
        ctx.Services.AddSingleton<IUnitOfWork>(unitOfWork.Object);

        // JSInterop for Radzen charts - might not be needed with stubs, but safer to keep loose
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<StatsPage>();

        cut.WaitForAssertion(() =>
        {
            // Verify Headers exist
            Assert.Contains("Activity Trends (Last 30 Days)", cut.Markup);
            Assert.Contains("Peak Usage Times", cut.Markup);
            Assert.Contains("Media Consumption", cut.Markup);
        });

        // Verify that RadzenCharts are present (stubbed)
        var charts = cut.FindComponents<Stub<RadzenChart>>();
        Assert.NotEmpty(charts);
        // We expect 3 charts (Trends, Peak, Media) + 1 Top Stations = 4 charts
        Assert.Equal(4, charts.Count);
    }
}
