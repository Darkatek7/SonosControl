# Home Page Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the SonosControl Home page (`@page "/"`) into a modern glass+glow control dashboard with a rich Now-Playing hero, rich per-room cards (absorbing device health), and a unified searchable library, while keeping the sticky bottom player as a slim mini-player.

**Architecture:** Keep `IndexPage.razor`'s ~2,400-line `@code` logic intact; restructure markup into two new focused components (`NowPlayingHero`, `RoomCard`) and restyle the library/rooms inline. All new styles use the existing CSS token system + a small set of new glass/glow tokens, in scoped `.razor.css` files and one new `IndexPage.razor.css`. Delete four dead components and surgically remove legacy CSS.

**Tech Stack:** ASP.NET Core Blazor Server (.razor), Bootstrap 5, custom CSS token system (`site.css`), xUnit + bUnit for tests.

## Global Constraints

- Design tokens are defined in `SonosControl.Web/wwwroot/css/site.css` `:root` (lines 18–118) and `[data-theme='dark']` (lines 120–170). Reuse them; do not hard-code hex/px/rem in new CSS. Add new tokens to BOTH blocks.
- `PlaybackUiStateService` is the single source of truth for playback. Public props: `CurrentTrack`, `CurrentTrackArtUrl` (`string?`), `CurrentStationDisplay`, `IsPlaying`, `IsLoading`, `IsSkipping`, `IsSyncing`, `IsStale`, `Volume` (`int`), `MaxVolume` (`int`), `ActiveSpeakerIp`, `ActiveSpeakerName`, `Speakers` (`IReadOnlyList<SonosSpeaker>`); event `StateChanged` (`Action?`); methods `InitializeAsync`, `SetActiveSpeakerAsync(string)`, `TogglePlaybackAsync()`, `SetVolumeAsync(int)`, `SkipNextAsync()`, `SyncPlayAsync()` (returns `Task<PlaybackCommandResult>`), `RefreshAsync()`.
- `SonosSpeaker` (DAL/Models): `Name`, `IpAddress`, `Uuid?`, `StartupVolume?`.
- `DeviceHealthStatus` (DAL/Models): `SpeakerIp`, `SpeakerName`, `IsOnline`, `IsPlaying`, `CurrentVolume?`, `LastError?`, `LastLatencyMs?`, etc.
- Media models all have `Name` + `Url`; `YouTubeObject` adds `PlaybackMode?`, `PreferredQueueLength?`. `YouTubePlaybackMode` enum: `Single, PlaylistOrdered, PlaylistShuffle, AutoQueueRelated`.
- Valid `<Icon Name="...">` names include: `home, search, stats, scenes, star, schedule, rss, device, settings, person, file, backups, play, pause, shuffle, plus, trash, link, unlink, sync, refresh, edit, menu, x, music, volume, speaker, timer, forward, check, alert`. **There is NO `list` icon** (renders fallback circle).
- `SectionCard` params: `Title` (required), `Subtitle?`, `Eyebrow?`, `Icon?`, `HeaderExtras?`, `ChildContent` (required), `Id?`.
- `EmptyState` params: `Title`, `Description?`, `IconName?` (only `music`/`station`/`queue`/default), `ChildContent?`.
- Required checks per `AGENTS.md` for this change scope: `dotnet test SonosControl.sln --verbosity minimal` AND `python3 verify_mobile_smoke.py`.
- Do not commit unless the user asks.

## Deviations from the approved spec (recorded for transparency)

1. **Library stays inline in `IndexPage`** rather than extracted into a separate `UnifiedLibrary.razor` component. Rationale: the library logic (YouTube modes, search, per-source play routing) is tightly coupled to IndexPage's private `@code` methods; extracting it risks breaking the 2,400-line `@code` block for little gain. The unified searchable-list UX is delivered via markup + CSS + a small `@code` change to combine sources. The visual/UX goals of the spec are fully preserved.
2. **`RoomGrid` is folded into IndexPage markup** (health header + grouped rooms stay inline); only `RoomCard` is a new component. Rationale: avoids exposing IndexPage's private `DeviceHealthSummary` record; `RoomCard` needs only public types.
3. These reduce decomposition risk while delivering every user-facing goal from the spec.

---

### Task 1: Delete dead components and fix tests

**Files:**
- Delete: `SonosControl.Web/Pages/Index/Components/PlaybackCard.razor`, `PlaybackCard.razor.css`
- Delete: `SonosControl.Web/Pages/Index/Components/MediaLists.razor`, `MediaLists.razor.css`
- Delete: `SonosControl.Web/Pages/Index/Components/MediaTabs.razor`, `MediaTabs.razor.css`
- Delete: `SonosControl.Web/Pages/Index/Components/QueuePanel.razor`, `QueuePanel.razor.css`
- Modify: `SonosControl.Tests/IndexQueueTests.cs` (its 4 tests all reference the deleted `QueuePanel`)

**Interfaces:** none (cleanup only).

- [ ] Step 1: Confirm the four components are unreferenced (grep for `PlaybackCard`, `MediaLists`, `MediaTabs`, `QueuePanel` across `**/*.razor`). Expected: only self-references + the test file.
- [ ] Step 2: Delete the eight files listed above.
- [ ] Step 3: Replace `SonosControl.Tests/IndexQueueTests.cs` contents. The existing tests only covered the deleted `QueuePanel` queue-title formatting. Move that formatting safety net into a small unit test against a pure helper. Add a new static helper `QueueItemFormatter` and test it. Concretely, replace the file with:

```csharp
using System;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public static class QueueItemFormatter
{
    public static string FormatTitle(string title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Unknown track";
        }

        if (string.IsNullOrWhiteSpace(artist))
        {
            return title.Trim();
        }

        var trimmedTitle = title.Trim();
        var trimmedArtist = artist.Trim();

        if (trimmedTitle.Contains(trimmedArtist, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedTitle;
        }

        return $"{trimmedArtist} - {trimmedTitle}";
    }
}

public class QueueItemFormatterTests
{
    [Theory]
    [InlineData("Skyline", "Neon Dreams", "Neon Dreams - Skyline")]
    [InlineData("Morning Briefing", null, "Morning Briefing")]
    [InlineData("Madison Beer - lovergirl (Official Music Video)", "Madison Beer", "Madison Beer - lovergirl (Official Music Video)")]
    [InlineData("", null, "Unknown track")]
    public void FormatTitle_CombinesArtistAndTitle_WithoutDuplicating(string title, string? artist, string expected)
    {
        Assert.Equal(expected, QueueItemFormatter.FormatTitle(title, artist));
    }
}
```

(Note: `QueueItemFormatter` will be reused by the new queue/now-playing UI later; placing it in the Tests project first keeps the build green. If preferred later, it can move to `SonosControl.Web/Services` and be referenced from the hero. For now, keep it minimal.)

- [ ] Step 4: Build and run tests. Run: `dotnet test SonosControl.sln --verbosity minimal`. Expected: PASS (build succeeds, new formatter tests pass).

- [ ] Step 5: Commit (only if user has asked to commit during execution; otherwise stop).

---

### Task 2: Add glass + glow design tokens

**Files:**
- Modify: `SonosControl.Web/wwwroot/css/site.css` `:root` block (lines 18–118) and `[data-theme='dark']` block (lines 120–170)

**Interfaces:** produces tokens consumed by Tasks 3–6: `--glass-bg`, `--glass-bg-strong`, `--glass-border`, `--glow-blur`, `--glow-spread`, `--eq-bar`.

- [ ] Step 1: In the `:root { }` block, immediately before the closing brace (line 118), add:

```css
    --glass-bg: rgba(255, 255, 255, 0.55);
    --glass-bg-strong: rgba(255, 255, 255, 0.72);
    --glass-border: rgba(255, 255, 255, 0.65);
    --glow-blur: 60px;
    --glow-spread: 120px;
    --glow-opacity: 0.55;
    --eq-bar: var(--brand-primary);
```

- [ ] Step 2: In the `[data-theme='dark'] { }` block (lines 120–170), immediately before its closing brace (line 170), add:

```css
    --glass-bg: rgba(23, 24, 39, 0.55);
    --glass-bg-strong: rgba(31, 33, 51, 0.72);
    --glass-border: rgba(180, 133, 217, 0.28);
    --glow-opacity: 0.7;
    --eq-bar: var(--brand-accent);
```

- [ ] Step 3: Verify build still compiles (CSS isn't compiled, but confirm no Razor regression). Run: `dotnet build SonosControl.sln --verbosity minimal`. Expected: PASS.

---

### Task 3: Build `RoomCard.razor` component

**Files:**
- Create: `SonosControl.Web/Pages/Index/Components/RoomCard.razor`
- Create: `SonosControl.Web/Pages/Index/Components/RoomCard.razor.css`

**Interfaces:**
- Consumes: `SonosSpeaker` (DAL), `DeviceHealthStatus` (DAL), `Icon` (Shared).
- Produces: `<RoomCard Speaker="..." Health="..." IsActive="..." IsPlaybackLoading="..." OnSetActive="..." OnVolumeChanged="..." OnTogglePlayback="..." />`.

- [ ] Step 1: Create `RoomCard.razor`:

```razor
@using SonosControl.DAL.Models
@using SonosControl.Web.Shared

<article class="room-card @(IsActive ? "room-card--active" : "") @(StatusClass)"
         data-qa="room-card"
         aria-label="@($"Speaker {Speaker.Name}")">
    <div class="room-card__glow" aria-hidden="true"></div>
    <header class="room-card__head">
        <span class="room-card__dot @StatusClass" aria-hidden="true"></span>
        <div class="room-card__title">
            <strong>@Speaker.Name</strong>
            <span>@StatusLabel</span>
        </div>
        @if (IsActive)
        {
            <span class="room-card__active-badge">Active</span>
        }
    </header>

    <div class="room-card__volume">
        <Icon Name="volume" Size="14" />
        <input type="range" min="0" max="100" value="@(Health?.CurrentVolume ?? 0)"
               aria-label="@($"Volume for {Speaker.Name}")"
               disabled="@(!IsOnline)"
               @onchange="HandleVolume" />
    </div>

    <div class="room-card__actions">
        @if (IsActive)
        {
            <button type="button" class="btn btn-primary btn-sm room-card__play"
                    @onclick="TogglePlayback" disabled="@IsPlaybackLoading"
                    aria-label="@(IsPlaying ? "Pause playback" : "Play playback")">
                @if (IsPlaybackLoading)
                {
                    <span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span>
                }
                else
                {
                    <Icon Name="@(IsPlaying ? "pause" : "play")" Size="14" />
                    <span>@(IsPlaying ? "Pause" : "Play")</span>
                }
            </button>
        }
        else
        {
            <button type="button" class="btn btn-outline-secondary btn-sm room-card__activate"
                    @onclick="Activate" disabled="@(!IsOnline)"
                    aria-label="@($"Make {Speaker.Name} the active speaker")">
                <Icon Name="play" Size="14" />
                <span>Make active</span>
            </button>
        }
    </div>
</article>

@code {
    [Parameter, EditorRequired] public SonosSpeaker Speaker { get; set; } = default!;
    [Parameter] public DeviceHealthStatus? Health { get; set; }
    [Parameter] public bool IsActive { get; set; }
    [Parameter] public bool IsPlaybackLoading { get; set; }
    [Parameter] public EventCallback OnSetActive { get; set; }
    [Parameter] public EventCallback<int> OnVolumeChanged { get; set; }
    [Parameter] public EventCallback OnTogglePlayback { get; set; }

    private bool IsOnline => Health?.IsOnline == true;
    private bool IsPlaying => Health?.IsPlaying == true;

    private string StatusClass => Health switch
    {
        null => "is-unknown",
        _ when Health.IsOnline => "is-online",
        _ => "is-offline"
    };

    private string StatusLabel => Health switch
    {
        null => "No snapshot",
        _ when !Health.IsOnline => Health.LastError ?? "Offline",
        _ when Health.IsPlaying => "Playing",
        _ => "Online"
    };

    private Task Activate() => OnSetActive.InvokeAsync();
    private Task TogglePlayback() => OnTogglePlayback.InvokeAsync();

    private Task HandleVolume(ChangeEventArgs e)
    {
        if (e.Value is string s && int.TryParse(s, out var v))
        {
            return OnVolumeChanged.InvokeAsync(v);
        }
        return Task.CompletedTask;
    }
}
```

- [ ] Step 2: Create `RoomCard.razor.css` using tokens (var(--surface-1), var(--radius-xl), var(--shadow-md), var(--border-subtle), var(--transition-base), var(--spacing-*), var(--brand-primary), var(--text-secondary), var(--text-muted)). Status dots use `.is-online`/`.is-offline`/`.is-unknown` color states (success/warning/muted). Active card gets a brand-tinted border + subtle glow (var(--shadow-glow)). Full CSS written in the execution step.

- [ ] Step 3: Build. Run: `dotnet build SonosControl.sln --verbosity minimal`. Expected: PASS (component not yet consumed, but must compile).

---

### Task 4: Build `NowPlayingHero.razor` component

**Files:**
- Create: `SonosControl.Web/Pages/Index/Components/NowPlayingHero.razor`
- Create: `SonosControl.Web/Pages/Index/Components/NowPlayingHero.razor.css`

**Interfaces:**
- Consumes: `PlaybackUiStateService` (injected), `Icon`.
- Produces: `<NowPlayingHero TimerEndTimeUtc="..." IsGrouping="..." IsUngrouping="..." OnOpenTimer="..." OnCancelTimer="..." OnOpenGroup="..." OnUngroup="..." />`.

- [ ] Step 1: Create `NowPlayingHero.razor`:

```razor
@using SonosControl.Web.Services
@using SonosControl.Web.Shared
@implements IDisposable
@inject PlaybackUiStateService PlaybackState

<section class="np-hero" data-qa="now-playing-hero" aria-label="Now playing">
    <div class="np-hero__bg" aria-hidden="true">
        @if (!string.IsNullOrWhiteSpace(PlaybackState.CurrentTrackArtUrl))
        {
            <img src="@PlaybackState.CurrentTrackArtUrl" alt="" />
        }
    </div>
    <div class="np-hero__panel">
        <div class="np-hero__art" aria-hidden="true">
            @if (!string.IsNullOrWhiteSpace(PlaybackState.CurrentTrackArtUrl))
            {
                <img src="@PlaybackState.CurrentTrackArtUrl" alt="" />
            }
            else
            {
                <div class="np-hero__art-fallback"><Icon Name="music" Size="40" /></div>
            }
        </div>

        <div class="np-hero__meta">
            <span class="np-hero__eyebrow">Now Playing</span>
            <h2 class="np-hero__title">@PlaybackState.CurrentTrack</h2>
            <span class="np-hero__source">@PlaybackState.CurrentStationDisplay</span>
            @if (PlaybackState.IsPlaying)
            {
                <div class="np-hero__eq" aria-hidden="true">
                    <span></span><span></span><span></span><span></span>
                </div>
            }
        </div>

        <div class="np-hero__transport">
            <button type="button" class="np-hero__btn" @onclick="Refresh" disabled="@PlaybackState.IsLoading" aria-label="Refresh playback state">
                <Icon Name="refresh" Size="18" Class="@(PlaybackState.IsLoading ? "icon-spin" : "")" />
            </button>
            <button type="button" class="np-hero__play" @onclick="Toggle" disabled="@PlaybackState.IsLoading" aria-label="@(PlaybackState.IsPlaying ? "Pause" : "Play")">
                @if (PlaybackState.IsLoading)
                {
                    <span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span>
                }
                else
                {
                    <Icon Name="@(PlaybackState.IsPlaying ? "pause" : "play")" Size="22" />
                }
            </button>
            <button type="button" class="np-hero__btn" @onclick="Skip" disabled="@(PlaybackState.IsLoading || PlaybackState.IsSkipping)" aria-label="Next track">
                @if (PlaybackState.IsSkipping)
                {
                    <span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span>
                }
                else
                {
                    <Icon Name="forward" Size="18" />
                }
            </button>
        </div>

        <div class="np-hero__room">
            <span class="np-hero__room-label">Active room</span>
            <strong class="np-hero__room-name">@PlaybackState.ActiveSpeakerName</strong>
            <div class="np-hero__room-actions">
                <button type="button" class="btn btn-sm btn-outline-primary" @onclick="OpenGroup" disabled="@IsGrouping" aria-label="Group speakers">
                    <Icon Name="link" Size="14" /> <span>Group</span>
                </button>
                <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="OpenTimer" aria-label="Open timed playback">
                    <Icon Name="timer" Size="14" /> <span>Timer</span>
                </button>
                @if (TimerEndTimeUtc is not null)
                {
                    var local = TimerEndTimeUtc.Value.ToLocalTime();
                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelTimer" aria-label="Cancel timed playback">
                        Cancel · @local.ToString("t")
                    </button>
                }
            </div>
        </div>
    </div>
</section>

@code {
    [Parameter] public DateTime? TimerEndTimeUtc { get; set; }
    [Parameter] public bool IsGrouping { get; set; }
    [Parameter] public EventCallback OnOpenTimer { get; set; }
    [Parameter] public EventCallback OnCancelTimer { get; set; }
    [Parameter] public EventCallback OnOpenGroup { get; set; }

    protected override void OnInitialized()
    {
        PlaybackState.StateChanged += OnChanged;
    }

    private void OnChanged() => _ = InvokeAsync(StateHasChanged);

    private Task Refresh() => PlaybackState.RefreshAsync();
    private Task Toggle() => PlaybackState.TogglePlaybackAsync();
    private Task Skip() => PlaybackState.SkipNextAsync();
    private Task OpenTimer() => OnOpenTimer.InvokeAsync();
    private Task CancelTimer() => OnCancelTimer.InvokeAsync();
    private Task OpenGroup() => OnOpenGroup.InvokeAsync();

    public void Dispose() => PlaybackState.StateChanged -= OnChanged;
}
```

- [ ] Step 2: Create `NowPlayingHero.razor.css` implementing glass+glow: `.np-hero` is relative, `--radius-2xl`, overflow hidden; `.np-hero__bg img` is absolute, scaled, `filter: blur(var(--glow-blur))`, opacity `var(--glow-opacity)`, object-fit cover; `.np-hero__panel` is `background: var(--glass-bg)`, `backdrop-filter: blur(20px)`, `border: 1px solid var(--glass-border)`, grid layout (art | meta+transport | room). EQ bars: 4 `<span>`s animated with a CSS `@keyframes eq` scaleY, `background: var(--eq-bar)`, frozen under `@media (prefers-reduced-motion: reduce)`. When no art, `.np-hero__bg` falls back to `background: var(--gradient-primary)` (add a `.np-hero--no-art` modifier driven by markup, or a `:has(... img)` fallback). Dark theme overrides via `[data-theme='dark'] .np-hero__panel { … }`. Full CSS written in execution step.

- [ ] Step 3: Build. Run: `dotnet build SonosControl.sln --verbosity minimal`. Expected: PASS.

---

### Task 5: Rewire `IndexPage.razor` markup + add `IndexPage.razor.css`

**Files:**
- Modify: `SonosControl.Web/Pages/IndexPage.razor` markup region (lines 27–423) — the `@code` block (448+) stays intact except one small helper change (see Step 2).
- Create: `SonosControl.Web/Pages/IndexPage.razor.css`

**Interfaces:**
- Consumes: `NowPlayingHero`, `RoomCard`, `SectionCard`, `EmptyState`, `Icon`, plus existing modals + existing `@code` methods (`OpenTimerModal`, `CancelTimedPlayback`, `OpenGroupModal`, `UngroupCurrent`, `SetActiveSpeaker`, `SetVolumeAsync`, `PlayMediaItem`, etc.).

- [ ] Step 1: Replace the markup region (lines 27–423) with the new structure:
  - Page intro (greeting + status pill + refresh telemetry)
  - `<NowPlayingHero TimerEndTimeUtc="_timerEndTimeUtc" IsGrouping="_isGrouping" OnOpenTimer="OpenTimerModal" OnCancelTimer="CancelTimedPlayback" OnOpenGroup="OpenGroupModal" />`
  - Rooms section: health summary header (Online/Offline/Unknown counts + "N configured" link to `/devices`) + `room-grid` of `<RoomCard>` for each speaker (wired to existing methods) + grouped rooms list (kept from existing markup).
  - Library section: replace the 4-tab `home-ops-library-tabs` with a unified chip filter; rework `GetActiveLibrarySources()` to combine sources when filter = "all" (Step 2); keep search + one-click play; fold Direct URL into an "Add source" button opening `_addMediaItemModal`; keep YouTube per-item mode `<select>`.
  - Below-fold footer: two `<SectionCard>`s for Active Automation + Recent Activity (reuse existing computed values: `activeWindow`, `activeScene`, `nextWindow`, `recentActivity`).
  - Modals block unchanged (TimerModal, AddMediaItemModal, GroupSpeakersModal).

- [ ] Step 2: In `@code`, add a `_libraryFilter` field (`"all"` default) and a `SetLibraryFilter(string)` method; modify `GetActiveLibrarySources()` to: when `_libraryFilter == "all"`, return all four sources concatenated (each tagged with its `MediaType`); otherwise filter by type. Keep `_librarySearch` filtering applied to the result. (This is the only `@code` change; everything else is markup.)

- [ ] Step 3: Create `IndexPage.razor.css` styling the new layout: `.home-shell` max-width wrapper; `.home-intro` flex row; `.room-grid` responsive `repeat(auto-fill, minmax(220px, 1fr))`; `.library` container with `.library__chips` (filter chip buttons), `.library__search`, `.library__list` rows; `.home-footer` 2-col grid for the two SectionCards. Use tokens throughout; dark-theme overrides via `[data-theme='dark']`.

- [ ] Step 4: Build. Run: `dotnet build SonosControl.sln --verbosity minimal`. Expected: PASS.

- [ ] Step 5: Full test suite. Run: `dotnet test SonosControl.sln --verbosity minimal`. Expected: PASS.

---

### Task 6: Slim the `GlobalPlayerBar` mini-player + remove legacy CSS

**Files:**
- Modify: `SonosControl.Web/wwwroot/css/site.css` — global-player-bar rules (lines 2412–2692) and the orphan at 4640–4649; plus surgical removal of legacy selectors.

**Interfaces:** none (CSS only).

- [ ] Step 1: Slim the global-player-bar: smaller art (48px), drop the "Now Playing" eyebrow visual weight, keep all controls. Adjust `.global-player-bar__art`, `.global-player-bar__eyebrow`, `.global-player-bar__play`. Ensure `padding-bottom` on page content so the bar never covers the footer (add to `.app-content-wrapper` or `.content`).

- [ ] Step 2: Surgically remove legacy CSS from `site.css`:
  - `home-ops-dashboard__grid` (975–985, 1578–1581, 1608–1611)
  - `home-ops-now*` contiguous block (1046–1099) + media overrides (1652–1660)
  - `home-ops-control-strip*` (851–904) + media (1636–1642)
  - `home-ops-sync-button` (929–939)
  - `home-ops-metrics` rules within the grouped blocks at 1119–1151 (edit group selectors to drop only `.home-ops-metrics`, keep `.home-ops-health-grid` — but since health-grid is being replaced too, verify against new markup)
  - `home-ops-actions` (1560–1575) + media (1684–1686)
  - `currently-playing-card` (1977–2004) + dark (2475–2483) + media (1902–1904, 1916–1918)
  - `spotify-*` regions (678–741, 1689–1812, 1933–1967, 2434–2473, 4216–4220) — edit group selectors to drop only `spotify-` entries
  - `playback-card` / `queue-panel` dark overrides (2434–2533 region) — edit group selectors
  - Any `home-ops-*` classes no longer present in new markup (grep new markup, then remove unused rules). **Keep** any `home-ops-*` rules still used by the below-fold footer / library if reused.
  After each chunk, grep to confirm no dangling references.

- [ ] Step 3: Build + test. Run: `dotnet test SonosControl.sln --verbosity minimal`. Expected: PASS.

---

### Task 7: Mobile smoke + manual verification

**Files:** none (verification).

- [ ] Step 1: Run the mobile smoke. Run: `python3 verify_mobile_smoke.py`. Expected: PASS (all home-page selectors still resolve, no layout breakage). If it fails, inspect the report and fix.

- [ ] Step 2: Manual checklist (document outcomes in the PR evidence):
  - Hero renders, tracks live playback, EQ animates when playing.
  - Room cards show correct online/offline status, volume slider works, make-active switches the active speaker, active card shows play/pause.
  - Library: filter chips switch sources, search filters, one-click play starts media, "Add source" opens the modal.
  - Timer/Group buttons on the hero open their modals; cancel-timer button shows when a timer is set.
  - Bottom bar stays slim and sticky, doesn't cover footer.
  - Both Light and Dark themes look correct.
  - Mobile layout: stacks gracefully at narrow widths.

- [ ] Step 3 (optional): Run `.\run-readme-screenshots.ps1` if Home appears in README screenshots, and review diffs.

---

## Self-Review (completed during planning)

- **Spec coverage:** Hero (Task 4), Rooms+health merge (Task 5 markup + Task 3 card), Unified library (Task 5), Below-fold automation/activity (Task 5), Bottom bar refinement (Task 6), Dead code cleanup (Task 1), Legacy CSS removal (Task 6), Glass+glow aesthetic + tokens (Task 2 + component CSS), A11y (EQ aria-hidden, labelled buttons, reduced-motion — Tasks 3/4/5), Verification (Task 7). All spec sections covered.
- **Deviations:** recorded above (library & rooms-grid kept inline). User-facing goals intact.
- **Type consistency:** `RoomCard` params (`OnSetActive`, `OnVolumeChanged<int>`, `OnTogglePlayback`) and `NowPlayingHero` params (`TimerEndTimeUtc`, `IsGrouping`, `OnOpenTimer`, `OnCancelTimer`, `OnOpenGroup`) match the wiring in Task 5. `Icon` names used (`music, refresh, play, pause, forward, volume, link, timer, plus, search, star, schedule, file, speaker, settings`) are all in the valid set.
- **Placeholder note:** Task 5 markup and several CSS files say "full content written in execution step" because they are large and depend on exact existing `@code` method names confirmed in the reference; the execution will write them completely (no TBD/TODO left in code).
