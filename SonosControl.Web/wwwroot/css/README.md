# CSS primitives

`site.css` provides the Violet Liquid Glass design system for Blazor pages and standalone MVC auth views. Component styles inherit theme tokens rather than targeting the document's `data-theme` attribute from isolated CSS.

## Tokens

- Brand: `--brand-primary` and `--brand-accent` adapt text accents to each theme. `--brand-fill` and `--brand-fill-hover` provide saturated button backgrounds with readable white labels. Semantic success and danger buttons use `--on-success` and `--on-danger`.
- Solid surfaces: `--surface-0` through `--surface-3` provide page, field, table, and inset backgrounds. Use these for nested controls rather than stacking translucent layers.
- Materials: `--glass-bg`, `--glass-bg-strong`, `--glass-border`, and `--material-blur` define floating navigation, toolbars, dialogs, and auth panels. `--material-card` and `--material-card-accent` provide more opaque content cards; `--material-highlight` supplies their top light edge.
- Ambient light: `--ambient-violet`, `--ambient-blue`, and `--ambient-rose` supply static page gradients. `--gradient-primary` styles the brand marks; `--gradient-glass` adds gentle depth to the now-playing panel.
- Typography: platform system fonts serve body and display text. Size, spacing, radius, shadow, and transition scales use shared tokens; large headings have tighter tracking.
- Theme: dark tokens are selected by `data-theme="dark"` on `html`. Bootstrap variables derive from the same palette. `sonos-theme.js` runs before styles load in both shells and preserves `window.sonosTheme.apply("system" | "light" | "dark")`. Signed-in preferences come from the existing user profile; anonymous auth pages follow the operating system.

## Components and layout

- Navigation, the global player dock, menus, and dialogs use translucent materials with 24 px backdrop blur and a WebKit-prefixed equivalent.
- Content cards use the stronger card material; fields, select options, and nested tables remain solid. Do not add backdrop blur to every row or nested control.
- The player dock uses `--player-inset` for its outside margin and `--player-reserve` for content clearance. Mobile safe areas are applied to the dock position and page padding. Drawers and dialogs render above the dock.
- Existing responsive layouts and playback controls remain intact. Buttons respond on press; hover movement is limited to fine pointers.

## Accessibility and fallbacks

- Focus rings remain visible and mobile controls have at least 44 px touch targets.
- `prefers-reduced-motion` removes movement and decorative animation while retaining feedback.
- `prefers-reduced-transparency`, `prefers-contrast: more`, and forced colors use solid materials; increased contrast strengthens text and borders.
- Browsers without either backdrop-filter implementation use solid materials automatically.
- Theme changes retain the existing System, Light, and Dark preference contract without adding a database field or a separate design selection.
