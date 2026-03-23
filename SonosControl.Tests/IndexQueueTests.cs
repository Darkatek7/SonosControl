using System;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Pages.Index.Components;
using Xunit;

namespace SonosControl.Tests;

public class IndexQueueTests
{
    [Fact]
    public void QueuePanel_RendersRadioQueueItems()
    {
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        using var cut = ctx.RenderComponent<QueuePanel>(parameters => parameters
            .Add(p => p.Items, new[]
            {
                new SonosQueueItem(0, "Morning Briefing", null, null, null),
                new SonosQueueItem(1, "City Updates", null, null, null)
            })
            .Add(p => p.RefreshIntervals, new[] { 15, 30 })
            .Add(p => p.RefreshIntervalSeconds, 15)
            .Add(p => p.FormatItem, item => item.DisplayTitle));

        var items = cut.FindAll(".queue-panel__item");
        Assert.Equal(2, items.Count);
        Assert.Contains("Morning Briefing", items[0].TextContent);
    }

    [Fact]
    public void QueuePanel_RendersSpotifyQueueItemsWithArtist()
    {
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        using var cut = ctx.RenderComponent<QueuePanel>(parameters => parameters
            .Add(p => p.Items, new[]
            {
                new SonosQueueItem(0, "Skyline", "Neon Dreams", "Night Drive", null)
            })
            .Add(p => p.RefreshIntervals, new[] { 15, 30 })
            .Add(p => p.RefreshIntervalSeconds, 15)
            .Add(p => p.FormatItem, item => item.DisplayTitle));

        var items = cut.FindAll(".queue-panel__item");
        Assert.Single(items);
        Assert.Contains("Neon Dreams", items[0].TextContent);
        Assert.Contains("Skyline", items[0].TextContent);
    }

    [Fact]
    public void QueuePanel_ShowsEmptyStateWhenQueueEmpty()
    {
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        using var cut = ctx.RenderComponent<QueuePanel>(parameters => parameters
            .Add(p => p.Items, Array.Empty<SonosQueueItem>())
            .Add(p => p.RefreshIntervals, new[] { 15, 30 })
            .Add(p => p.RefreshIntervalSeconds, 15)
            .Add(p => p.FormatItem, item => item.DisplayTitle));

        Assert.Contains("Queue is empty.", cut.Markup);
        Assert.Single(cut.FindAll("[data-qa='queue-card']"));
    }
}
