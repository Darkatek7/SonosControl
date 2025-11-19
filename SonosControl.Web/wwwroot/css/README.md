# CSS primitives

Use `site.css` to share design tokens, surface layers, and component primitives across Blazor pages and MVC views.

## Tokens
- Brand hues: `--brand-primary`, `--brand-accent`, `--brand-info`, `--brand-warning`, `--brand-danger`, plus hover-friendly `--brand-primary-strong`.
- Surfaces: `--surface-0` through `--surface-3` for page, card, panel, and overlay layers.
- Typography: font families (`--font-family-base`, `--font-family-display`, `--font-family-mono`), size scale (`--font-size-xs` … `--font-size-3xl`), and line heights.
- Sizing helpers: spacing (`--spacing-2xs` … `--spacing-2xl`), radii (`--radius-xs` … `--radius-xl`), and shadows (`--shadow-xs` … `--shadow-lg`).
- Theme swap: add `data-theme="dark"` to `html` or `body` to consume the dark token set. Bootstrap variables such as `--bs-body-bg`, `--bs-primary`, and `--bs-card-bg` are derived from the tokens so built-in components follow the palette automatically.

## Base styles and utilities
- Layout and text: responsive `body` sizing, heading scale with clamps, and focus rings from `--focus-ring`.
- Surfaces: `.surface-0` through `.surface-3` mirror the layered palette. `.stack-xs`, `.stack-sm`, and `.stack-md` add vertical rhythm between siblings.
- Cards and panels: `.card` or `.panel` with matching headers/footers use surface colors, rounded corners, and shadows. Tables inherit the same layering with striped and hover states based on the tokens.
- Forms and buttons: `.form-control`, `.form-select`, `.form-check-input`, and `.btn` consume token borders, radii, and focus rings; `.btn-primary` maps to `--brand-primary`.
- Alerts and modals: `.alert-*` shades come from the brand hues; `.modal-content` and its header/footer use layered surfaces and rounded edges.

## Auth shell
- Wrap standalone auth pages in `.auth-shell` to get the gradient background and center alignment, and apply `.auth-card` to the form container for consistent padding, radius, and shadow.
- Remove inline dark-mode CSS from Razor views; the shared theme handles light and dark contexts via tokens.
