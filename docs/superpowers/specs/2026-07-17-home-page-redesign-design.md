# Home Page Redesign — Design Spec

- **Date:** 2026-07-17
- **Scope:** `SonosControl.Web/Pages/IndexPage.razor`, `SonosControl.Web/Pages/Index/Components/**`, `SonosControl.Web/Shared/GlobalPlayerBar.razor`, `SonosControl.Web/wwwroot/css/site.css`
- **Out of scope:** other pages, backend services, data models, routing

## 1. Problem

The current Home page (`@page "/"`, `IndexPage.razor`) is an information-dense
"command center" with five flat panels in a tight 2-column grid. Now-Playing
lives only in the sticky bottom bar, so the page itself feels static. The Library
panel is busy (four source tabs, a Direct-URL disclosure, YouTube queue modes,
search, add and manage affordances). Four sibling components under
`Index/Components/` (`PlaybackCard`, `MediaTabs`, `MediaLists`, `QueuePanel`)
and a large block of legacy `home-ops-*` CSS in `site.css` are dead code. The
result reads as cluttered and dated rather than as a fast playback control
surface.

## 2. Goals

- Make **what is playing right now** the visual hero of the page.
- Make **rooms/speakers** first-class, controllable tiles (absorbing device
  health into the same surface).
- Make **switching stations/tracks** a single click from a unified, searchable
  library.
- Adopt a modern **glass + glow** visual language on the existing token system,
  in both light and dark themes.
- Remove dead code and legacy CSS; keep Home-specific styles scoped to the page.

## 3. Non-Goals

- No change to the backend, services, or data models.
- No new pages or routes. Secondary surfaces (Active Automation, Recent
  Activity) remain on Home in a compact below-the-fold footer; they are not
  deleted from the app.
- No replacement of the design-token palette; existing tokens are reused and a
  small set of glass/glow tokens are added.
- No change to authentication or roles.

## 4. Page Structure (top to bottom)

| # | Section | Contents |
|---|---|---|
| 1 | Page intro | Slim greeting + Manual/Automation status pill + "Updated Ns ago" telemetry. Replaces the generic "Today at a glance" header. |
| 2 | **Now-Playing Hero** *(new)* | Large album art with color-glow bleed, track/station title, animated EQ bars while playing, large transport (refresh / play-pause / next), active-room chip with Timer / Group / Sync actions. |
| 3 | **Rooms grid** *(new; absorbs Device Health)* | Health summary (Online / Offline / Unknown counts) followed by rich per-room cards. |
| 4 | **Unified Library** *(rebuild)* | One searchable list, source filter chips (All / Stations / Spotify / YouTube / YT Music), one-click play per row, "Add source" affordance. |
| 5 | Below-the-fold footer | Compact two-column row using `SectionCard`: Active Automation + Recent Activity. |
| 6 | Modals | `TimerModal`, `AddMediaItemModal` (reused as "Add source"), `GroupSpeakersModal`. Behavior unchanged. |

The current standalone **"Active room" context bar is removed**. Its Group /
Timer / Ungroup actions move onto the hero's active-room chip (and the active
`RoomCard`), eliminating redundancy.

## 5. Component Breakdown

`IndexPage.razor` keeps its existing `@code` logic and data loading; only the
markup is restructured into focused units. All new components live under
`SonosControl.Web/Pages/Index/Components/` and carry scoped `.razor.css` files.

- **`NowPlayingHero.razor`** — subscribes to `PlaybackUiStateService` (the same
  source as `GlobalPlayerBar`). Renders the glass hero, transport buttons, and
  the active-room chip with Timer / Group / Sync actions. Exposes no parameters;
  pulls state directly from the injected service and raises
  `PlaybackState.StateChanged` re-renders.
- **`RoomCard.razor`** — parameters: a speaker model, the active speaker IP, and
  grouped-pair information. Renders name, status dot (online / offline /
  unknown), grouped-pair badge, and a mini volume slider. Because playback is
  shared system-wide (one active speaker drives the session), the **active**
  card additionally shows a play/pause toggle and a highlighted "active"
  treatment; non-active cards show a "make active" button instead. Raises
  `EventCallback`s (`OnSetActive`, `OnVolumeChanged`, `OnTogglePlayback`) that
  the page wires to existing service methods. (Timer / Group / Sync actions live
  on the hero's active-room chip, not on the card, to avoid duplication.)
- **`RoomGrid.razor`** — parameter: the speakers collection plus health counts.
  Renders the health summary header (with a "Manage devices" link to `/devices`)
  and maps each speaker to a `RoomCard`, forwarding the callbacks.
- **`UnifiedLibrary.razor`** — parameters: the existing per-source media lists
  plus the current filter/search state. Renders the search input, source filter
  chips, and one-click-play rows (source-type icon + name + meta + play button).
  The YouTube modes (Auto Queue / Single / Ordered / Shuffle) collapse into a
  single dropdown on the YouTube filter. Direct-URL play and the "Add media"
  affordance both open the existing `AddMediaItemModal`.
- **Kept unchanged:** `TimerModal`, `AddMediaItemModal`, `GroupSpeakersModal`.
- **Reused shared components:** `SectionCard`, `EmptyState`, `Icon`,
  `SkeletonLoader`.
- **New:** `IndexPage.razor.css` (scoped) holds the new Home layout, so most new
  Home CSS leaves the global `site.css`.

## 6. Visual Aesthetic — Glass + Glow

- **Hero:** the album art image is rendered twice — once large and blurred behind
  the panel as a pure-CSS color glow (no color extraction, no backend work), and
  once at full sharpness in the foreground. The blurred image sits behind a
  frosted glass panel (`backdrop-filter: blur()`, a semi-transparent
  `--surface-1`, `--radius-2xl`). When no art is available, the glow falls back
  to the brand `--gradient-primary`. Animated CSS EQ bars run while `IsPlaying`
  and freeze under `prefers-reduced-motion`.
- **Cards:** `--surface-1`, soft `--shadow-md`, `--radius-xl`, hairline
  `--border-subtle`, subtle hover lift via `--transition-base`.
- **Color discipline:** neutral surfaces; teal/cyan reserved for active and play
  states and small accents. The album-art glow is the only source of color
  dynamism.
- **Typography:** display weight for the hero track title (clamp-scaled), Inter
  everywhere else, uppercase tracked eyebrows in `--text-muted`.
- **New tokens** (added to both `:root` and `[data-theme='dark']`): `--glass-bg`,
  `--glass-border`, `--glow-blur`, `--glow-spread`. Existing palette, spacing,
  radii, shadow, and transition tokens are reused.
- **Themes:** both light and dark are tuned. Glass + glow reads especially well
  against the dark/purple palette.

## 7. Bottom Bar (`GlobalPlayerBar`)

Stays sticky as the **mini-player** while scrolling. All current controls are
retained (art, track, refresh / play / next, speaker select, volume, sync).
Visual slimming only: smaller art, drop the now-redundant "Now Playing" eyebrow,
and ensure page bottom padding so the bar never covers the footer. No logic
changes.

## 8. Cleanup (Dead Code)

- **Delete components and their scoped CSS:** `PlaybackCard`, `MediaTabs`,
  `MediaLists`, `QueuePanel` (each with its `.razor.css`).
- **Remove legacy CSS** from `site.css`: the old three-column
  `home-ops-dashboard__grid`, `home-ops-now*`, `home-ops-metrics`,
  `home-ops-control-strip`, `home-ops-sync-button`, `home-ops-recommendations`,
  `home-ops-actions`, `playback-card`, `queue-panel`, `currently-playing-card`,
  and `spotify-*` rules.
- **Audit `SonosControl.Tests/IndexQueueTests.cs`.** If its cases reference only
  the deleted components, remove those cases (or replace them with tests against
  the new components). Keep the build green.

## 9. Accessibility

- Preserve existing ARIA patterns: roving `tabindex` on filters, focus traps on
  the reused modals, labelled transport buttons.
- EQ animation is `aria-hidden`.
- Room status is conveyed via `aria-label` text, not color alone.
- Color contrast is maintained in both themes; `prefers-reduced-motion` is
  honored (EQ bars and hover lifts are reduced or removed).
- Hero transport buttons and room-card actions expose clear
  `aria-label` / `title` text.

## 10. Data & Interactions

- All existing services are preserved. `IndexPage.razor` continues to load
  speakers, schedule windows, recent activity, and media lists.
- `NowPlayingHero` subscribes to `PlaybackUiStateService` exactly as
  `GlobalPlayerBar` does today.
- `RoomCard` interactions (set active, volume, toggle playback) are forwarded
  via `EventCallback` to the page, which calls the existing
  `PlaybackUiStateService` methods. No new service surface is required.
- `UnifiedLibrary` one-click play reuses the existing per-source play methods.
  Filter chips and search are client-side.
- The "Add source" affordance and Direct-URL play open the existing
  `AddMediaItemModal`.

## 11. Verification (per `AGENTS.md` routing)

- `dotnet test SonosControl.sln --verbosity minimal` — `.cs` / `.razor` changed.
- `python3 verify_mobile_smoke.py` — `Pages/**` changed.
- Manual: hero tracks live playback; room cards reflect online / grouped /
  volume state; library one-click play works; both themes render correctly;
  mobile layout holds.
- Optional: `.\run-readme-screenshots.ps1` if Home appears in README shots.

## 12. Risks and Mitigations

- **Big-file risk:** `IndexPage.razor` is ~2,400 lines. Mitigation: only the
  markup is restructured; the `@code` block is moved verbatim into the new
  orchestrating page, with new presentational components extracting markup.
- **Scoped CSS migration:** moving Home CSS out of `site.css` may miss a rule.
  Mitigation: grep for each removed class before deleting and re-run the mobile
  smoke after each chunk.
- **Test breakage from deleted components:** Mitigation: audit
  `IndexQueueTests.cs` first and replace cases before deleting the components.
- **Bottom-bar/hero duplication confusion:** Mitigation: the hero is the rich
  view; the bottom bar is explicitly restyled as a slim mini-player so the two
  read as primary vs. persistent.
