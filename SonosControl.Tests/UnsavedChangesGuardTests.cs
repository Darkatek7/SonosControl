using Bunit;
using Microsoft.AspNetCore.Components;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public class UnsavedChangesGuardTests
{
    [Fact]
    public async Task CleanDraftDoesNotPrompt()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = ctx.Render<UnsavedChangesGuard>();
        var allowed = await cut.InvokeAsync(() => cut.Instance.ConfirmDiscardAsync());
        Assert.True(allowed);
        Assert.Empty(cut.FindAll("[role='alertdialog']"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecisionControlsDiscard_AndLeavesNoStalePrompt(bool discard)
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var discarded = 0;
        var cut = ctx.Render<UnsavedChangesGuard>(parameters => parameters
            .Add(component => component.HasChanges, true)
            .Add(component => component.OnDiscard, EventCallback.Factory.Create(this, () => discarded++)));
        Task<bool>? decision = null;
        await cut.InvokeAsync(() => { decision = cut.Instance.ConfirmDiscardAsync(); });
        Assert.NotNull(cut.Find("[role='alertdialog']"));
        cut.FindAll("button").Single(button => button.TextContent == (discard ? "Discard changes" : "Keep editing")).Click();
        Assert.Equal(discard, await decision!);
        Assert.Equal(discard ? 1 : 0, discarded);
        Assert.Empty(cut.FindAll("[role='alertdialog']"));
    }
}
