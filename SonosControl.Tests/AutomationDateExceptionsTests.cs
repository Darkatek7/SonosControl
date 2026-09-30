using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Pages;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class AutomationDateExceptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddException_SavesSelectedRepeatOption(bool isAnnual)
    {
        using var ctx = new BunitContext();
        var settings = CreateSettings();
        var repo = ConfigureServices(ctx, settings);
        var cut = RenderExceptions(ctx);

        cut.Find("#exception-recurrence").Change(isAnnual ? "Yearly" : "Once");
        cut.Find("#exception-date").Change("2026-12-25");
        cut.Find(".exception-editor button").Click();

        cut.WaitForAssertion(() =>
        {
            var window = settings.ScheduleWindows[0];
            Assert.Equal(new DateOnly(2026, 12, 25), Assert.Single(isAnnual ? window.AnnualExcludedDates : window.ExcludedDates));
            Assert.Empty(isAnnual ? window.ExcludedDates : window.AnnualExcludedDates);
            var card = cut.Find(".exception-card");
            Assert.Equal(isAnnual ? "Every year" : "Once", card.QuerySelector("span")!.TextContent);
            Assert.Equal(isAnnual ? "25/12" : "25/12/2026", card.QuerySelector("strong")!.TextContent);
            repo.Verify(repository => repository.WriteSettings(settings), Times.Once);
        });
    }

    [Fact]
    public void AddAnnualException_RejectsSameDayAndMonthInAnotherYear()
    {
        using var ctx = new BunitContext();
        var settings = CreateSettings();
        settings.ScheduleWindows[0].AnnualExcludedDates = [new DateOnly(2026, 12, 25)];
        var repo = ConfigureServices(ctx, settings);
        var cut = RenderExceptions(ctx);

        cut.Find("#exception-recurrence").Change("Yearly");
        cut.Find("#exception-date").Change("2027-12-25");
        cut.Find(".exception-editor button").Click();

        Assert.Single(settings.ScheduleWindows[0].AnnualExcludedDates);
        Assert.Contains("already has an exception", cut.Find(".alert-danger").TextContent);
        repo.Verify(repository => repository.WriteSettings(It.IsAny<SonosSettings?>()), Times.Never);
    }

    [Fact]
    public void RemoveAnnualException_PreservesOneTimeExceptionForSameDate()
    {
        using var ctx = new BunitContext();
        var settings = CreateSettings();
        var date = new DateOnly(2026, 12, 25);
        settings.ScheduleWindows[0].ExcludedDates = [date];
        settings.ScheduleWindows[0].AnnualExcludedDates = [date];
        var repo = ConfigureServices(ctx, settings);
        var cut = RenderExceptions(ctx);

        cut.Find("button[aria-label='Remove exception 25/12 (Every year)']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(settings.ScheduleWindows[0].AnnualExcludedDates);
            Assert.Equal(date, Assert.Single(settings.ScheduleWindows[0].ExcludedDates));
            Assert.Single(cut.FindAll(".exception-card"));
            repo.Verify(repository => repository.WriteSettings(settings), Times.Once);
        });
    }

    [Fact]
    public void Operator_CanViewRepeatOptionsButCannotChangeExceptions()
    {
        using var ctx = new BunitContext();
        var settings = CreateSettings();
        settings.ScheduleWindows[0].AnnualExcludedDates = [new DateOnly(2026, 12, 25)];
        var repo = ConfigureServices(ctx, settings, "operator");
        var cut = RenderExceptions(ctx);

        Assert.True(cut.Find("#exception-recurrence").HasAttribute("disabled"));
        Assert.True(cut.Find("#exception-date").HasAttribute("disabled"));
        Assert.True(cut.Find(".exception-editor button").HasAttribute("disabled"));
        Assert.Contains("Every year", cut.Find(".exception-card").TextContent);
        Assert.Empty(cut.FindAll(".exception-card button"));
        repo.Verify(repository => repository.WriteSettings(It.IsAny<SonosSettings?>()), Times.Never);
    }

    [Fact]
    public void EditSchedule_PreservesBothExceptionTypes()
    {
        using var ctx = new BunitContext();
        var settings = CreateSettings();
        settings.ScheduleWindows[0].IsEnabled = false;
        settings.ScheduleWindows[0].ExcludedDates = [new DateOnly(2026, 12, 24)];
        settings.ScheduleWindows[0].AnnualExcludedDates = [new DateOnly(2026, 12, 25)];
        var repo = ConfigureServices(ctx, settings);
        var cut = ctx.Render<ScheduleWindowsPage>();

        cut.Find(".schedule-window-card__main").Click();
        cut.Find(".schedule-editor-actions button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(new DateOnly(2026, 12, 24), Assert.Single(settings.ScheduleWindows[0].ExcludedDates));
            Assert.Equal(new DateOnly(2026, 12, 25), Assert.Single(settings.ScheduleWindows[0].AnnualExcludedDates));
            repo.Verify(repository => repository.WriteSettings(settings), Times.Once);
        });
    }

    private static SonosSettings CreateSettings() => new()
    {
        ScheduleWindows = [new ScheduleWindow { Name = "Holiday schedule" }]
    };

    private static IRenderedComponent<AutomationPage> RenderExceptions(BunitContext ctx)
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/automation?tab=exceptions");
        return ctx.Render<AutomationPage>();
    }

    private static Mock<ISettingsRepo> ConfigureServices(BunitContext ctx, SonosSettings settings, string role = "admin")
    {
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("test-user");
        auth.SetRoles(role);
        var repo = new Mock<ISettingsRepo>();
        repo.Setup(repository => repository.GetSettings()).ReturnsAsync(settings);
        repo.Setup(repository => repository.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(unit => unit.ISettingsRepo).Returns(repo.Object);
        ctx.Services.AddSingleton(uow.Object);
        ctx.Services.AddSingleton(new AutomationRuntimeStatus());
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        return repo;
    }
}
