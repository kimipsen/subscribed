# Subscribed frontend

Angular standalone application: OIDC session states, shared household settings, membership
visibility and optimistic rename, and the subscription screens. The member home page is the
subscription overview, with per-currency monthly and yearly totals and a list. Lazy-loaded routes
add, edit and show a subscription (`/subscriptions/new`, `/subscriptions/:id/edit`,
`/subscriptions/:id`); the detail page shows the price history and manages price entries, and ends,
reinstates or removes a subscription.

Money is text in the browser. Amounts arrive from the API as exact decimal strings and are formatted
with `Intl.NumberFormat` from the string, never through `Number` or `parseFloat`. Amount input accepts
only the active language's decimal separator and is sent to the API as a canonical decimal string. The
server computes every derived value; the only browser arithmetic is the exact BigInt sum of canonical
strings behind per-category group totals (`sumDecimalStrings`). The shared golden
fixtures in `src/app/subscriptions/testing/` are flushed by the specs and compared with the live API
responses by the backend tests.

The overview can be filtered by category and grouped by category, entirely in the browser: categories
stay free text, and are matched ignoring case and extra whitespace (the first-seen spelling is shown).
The add and edit forms suggest the existing categories through a `datalist`, and still accept any text.
Group totals are the exact sum of the displayed per-row monthly and yearly amounts of the group's active
subscriptions, per currency. They can differ by a minor unit from the server's round-once household
totals above them, and the group totals need not add up to those. The filter and grouping are view state
only and are not remembered.

Use Node 24.22.2 and npm 11.19.1:

```sh
npm ci
npm start
npm run test:ci
npm run lint
npm run build
npm audit --omit=dev
```

By default, build with `npm --prefix frontend run build` from the repository
root, then run the backend and open `http://localhost:5080`. The backend serves
the built frontend; login and logout callbacks use the same port. Rebuild after
frontend changes (or use `npm --prefix frontend run watch`).

For optional Angular hot reload, set the backend's `App__PublicOrigin` to
`http://localhost:4200`, restart it, and run `npm --prefix frontend start`.
Open port 4200 and keep both servers running. Angular proxies `/api`, `/bff`,
`/signin-oidc` and `/health` to port 5080, including for direct `ng serve`
and IDE launches. The local Keycloak client permits both explicit callback
origins. Begin a fresh sign-in after switching origins; authorization codes are
single-use.
Production output is `dist/subscribed/browser`, served by the backend on the
same origin. The backend replaces `__CSP_NONCE__` in the root element before
serving HTML; do not serve production HTML without that substitution.
Critical CSS inlining is disabled so the build does not introduce an inline
stylesheet-loader script that would violate the backend's `script-src 'self'`.

The browser never receives OIDC access or refresh tokens. Requests to relative
API/BFF paths carry `X-CSRF: 1`; session failures render an explicit unavailable
state, not an anonymous session. Household updates send the expected version in
`If-Match`. Backend authorization remains authoritative for every request.

The draft's original Angular 21.0.0/Vitest 4.0.8 pins had known high/critical
advisories. Implementation uses patched Angular 21.2.26, CLI/build 21.2.29 and
Vitest 4.1.12 within the same major releases. Exact dependencies and the npm
lockfile are tracked. Production dependency audit passes without findings.
Development-only lint/build dependencies still have moderate audit findings;
do not expose the development server publicly.

## Themes

The header's **Theme** selector offers **System** (the default; Danish
*Følg systemet*), **Light** and **Dark**. It is visible in every session state
and at every width, and switching applies immediately without navigation,
session reload, or lost input and focus.

Precedence: an explicit `light` or `dark` choice wins. `system` (or no stored
value) follows `prefers-color-scheme` live; without `matchMedia` support, or
with no expressed preference, the app is Light. The choice is stored in
`localStorage` as `subscribed.theme` (`system`/`light`/`dark`). Invalid values
are ignored and left untouched. The preference is browser-local, survives
sign-out, and is independent of the account and of `subscribed.language`. If
storage fails, the choice still applies until reload and a dismissible,
localized notice is shown.

`public/theme-init.js` is a render-blocking, same-origin script loaded from
`<head>`. It sets `<html data-theme="light|dark">` before first paint, never
throws and never writes storage. `script-src 'self'` already allows it, so the
CSP is unchanged; do **not** inline it. If it fails to load, the CSS
`prefers-color-scheme` fallback applies until `ThemeService`
(`src/app/theme/theme.ts`) starts. Keep the script's logic in sync with
`isThemeMode`/`resolveTheme`.

Colours come from the two daisyUI 5 themes, `light` and `dark`, which daisyUI applies through
`[data-theme="light"|"dark"]`: the attribute `theme-init.js` and `ThemeService` already write. Templates use
daisyUI semantic colours (`bg-base-100`, `text-base-content`, `btn-primary`, ...) and never palette colours, `dark:`
variants, arbitrary colour values or hex literals. `src/styles.css` registers both themes with
`@plugin "daisyui/theme"`. Each block restates the stock daisyUI 5.8.1 theme and changes only the colours that fail
a WCAG pair checked by `scripts/theme.test.mjs`: `error`/`error-content` in `light`, and `primary`/`primary-content` in
`dark`. Do not add another override unless the contrast test shows a failure.

To add or change a colour pair:

1. Change the colour in the matching `@plugin "daisyui/theme"` block in `src/styles.css`. Keep the value inside the
   sRGB gamut and the rest of the block identical to the stock theme file in `node_modules/daisyui/theme/`.
2. Add the foreground/background pair to `pairsFor()` in `scripts/theme.test.mjs` as
   `add('<description>', c('<fg token>'), c('<bg token>'), <minimum ratio>)`: 4.5 for text, 3 for UI and focus.
   `pairsFor()` runs for both themes. A translucent text or border colour (`text-base-content/70`) must also be a row
   of `ALPHA_TABLE` in `scripts/theme-palette.mjs`; templates may only use the alpha values in that table.
3. Run `npm run test:ci`. Hex, `rgb()` and named colours outside the override blocks in `src/styles.css` fail the
   checks.

Dependency-free `node:test` checks run as part of the npm scripts:

- `npm run test:ci` runs `scripts/theme.test.mjs`: the init-script contract (`theme-init.js` mirrors `theme.ts`, read as
  text), WCAG contrast of 4.5:1 for text and 3:1 for UI/focus in both themes (stock daisyUI colours plus the overrides,
  including the button, menu and table hover and alpha states), a guard that the daisyUI 5.8.1 expressions behind those
  state pairs have not changed, and no hard-coded colours in `src/styles.css`.
- `npm run test:ci` also runs `scripts/styles-contract.test.mjs`, which parses every template with the Angular template
  parser and enforces the styling rules: no `.scss` or component styles, no removed semantic class from the old
  stylesheet, no inline `style`, only allow-listed daisyUI components, only the approved alpha utilities, complete class
  literals, the sidebar rules and the control-border rules.
- `npm run build` runs `scripts/theme-dist.test.mjs`: the built `index.html` keeps one blocking `theme-init.js` in `<head>`,
  no inline scripts, the CSP nonce placeholder and a byte-identical script copy, and the built CSS holds both themes.

Appearance has no automated check: jsdom applies no CSS, so specs assert DOM, attributes and text. Wrapping, overflow,
focus-ring visibility, tooltips and colour rendering are checked by hand in a browser.

### Class names must be complete literals

Tailwind finds classes by scanning source text for complete class names (`src/styles.css` limits the scan to the
templates and `.ts` files under `src`, excluding specs, `testing/` and `i18n/`). Write every class as a whole literal
in a template or a class string. Never build one by concatenation or interpolation (`'bg-' + tone`,
`` `text-${tone}` ``): the stylesheet would not contain it and the contract check fails. To vary a class, choose between
complete literals (`[class]="open() ? 'btn-primary' : 'btn-ghost'"` or a lookup of literals).

### Browser support

Tailwind CSS 4 and daisyUI 5 need current evergreen browsers (cascade layers, `color-mix()`, `oklch()` and
`@property`). Older browsers are not supported and are not tested.

## Languages

**English** and **Dansk** switch live from the header, including before sign-in
and on mobile, preserving routes, sessions, and input.

Precedence: explicit `subscribed.language` in `localStorage` (`en`/`da`), first
supported browser language (regional variants included), then English. Only
explicit choices persist. This browser-local setting survives sign-out and
does not affect other members. Storage failures show a dismissible notice;
selection still works until reload.

Bundled, typed catalogs in `src/app/i18n/` use the `t` pipe and message descriptors
for live status/error translation. No extra dependencies, network loading, or
HTML interpolation; CSP is unchanged. Missing messages fall back to English
with development warnings. Document language follows selection; dates use
`en-GB`/`da-DK` with unchanged timezone behavior. User content and references
stay verbatim. API errors map status/type/field keys, never backend prose.

To add a language:

1. Add its code, native name, and date locale to `SUPPORTED_LANGUAGES`.
2. Add a catalog typed as `Catalog` and register it in `CATALOGS`.
3. Register its Angular date locale in `app.config.ts`.
4. Translate messages/accessibility labels with matching `{named}` parameters.
5. Run `npm run test:ci`, `npm run lint`, `npm run build` (catalog parity,
   interpolation, switching, errors, storage failure, and locale coverage).

## Footer version

The footer shows a link to the GitHub repository and the application version. The version is injected
at build time by `scripts/build.mjs` through `ng build --define` from the `APP_VERSION` environment
variable. The release workflow sets it through the Docker build-arg `APP_VERSION` (the release tag with
the leading `v` removed). When `APP_VERSION` is unset, the footer shows the `package.json` version.

## Sidebar

At 900px and wider, a member's navigation has a toggle that collapses the sidebar to an icons-only rail
(4rem wide). The main content widens to use the space. In the rail, each link and the toggle show their
label as a tooltip on hover and keyboard focus; the label stays in the DOM, so the accessible names do not
change. The tooltip is CSS-only and cannot be dismissed with Escape (a known limitation).

The 900px breakpoint is the `nav` breakpoint (`--breakpoint-nav` in `src/styles.css`, used as the `nav:` and `max-nav:`
variants), not Tailwind's `lg`. There is no layout-test script: the sidebar's DOM behaviour is covered by the Angular
specs and its class rules by `scripts/styles-contract.test.mjs`; the rendered layout is checked by hand.

The choice is stored in `localStorage` under `subscribed.sidebar` as `collapsed` or `expanded`. Any other
value is ignored and the sidebar starts expanded. It is browser-local and independent of the account. If
storage is unavailable, the toggle still works until reload and shows no notice (unlike the theme and
language choices). Below 900px there is no toggle and no rail: the labels stay visible and the Menu button
works as before.

## Third-party licences

| Package | Licence | Copyright | Source | Version | Date added |
| --- | --- | --- | --- | --- | --- |
| lucide-angular | ISC | Lucide Contributors; portions from Cole Bemis / Feather, MIT | https://github.com/lucide-icons/lucide | 1.0.0 | 2026-11-17 |

The production build writes the full licence texts to `dist/subscribed/3rdpartylicenses.txt`. Logo files
are tracked separately in `public/logos/LICENSES.md`.
