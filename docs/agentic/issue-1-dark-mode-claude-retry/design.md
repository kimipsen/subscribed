# Design: System / Light / Dark theme (issue-1-dark-mode-claude-retry)

Status: **design for a distinct retry run**, for Copilot review and then human approval. Scope: **Angular frontend plus workflow docs (§5.7)**.
Companion mockup: [`preview.html`](preview.html). It is a static HTML mockup with synthetic data, not working UI.

## 0. Provenance and review-stage boundary

- This run implements the design and preview the owner approved in run `issue-1-dark-mode-approved`. That run's frontend stage used all three attempts on one failing check: `i18n.spec.ts` requires every Danish string to differ from its English one, and Danish `theme.system` was `System`. The earlier runs keep their worktrees, state, feedback and counters, and this run does not touch them.
- **Carried forward unchanged:** the architecture, tokens, contrast pairs, runtime feature contracts and visual design. `preview.html` is still the approved visual mockup. The test plan was revised after publication to avoid dynamic execution (§4, §9).
- **Post-publication Sonar remediation of `preview.html`:** its source now contains **semantic-only accessibility fixes**, so it **differs in bytes** from the approved file and a `cmp` against it is no longer expected to match. These fixes have **no intended visual change**: layout, colours, copy and states are as approved. Any visual difference counts as a regression, not as a design change.
- **Changed in this run:** Danish `theme.system` becomes **`Følg systemet`** (§7). This is a copy change only, which approved assumption 5 allows. The preview still shows `System`, because its visible content stays as approved. **Where they differ, §7 wins.** Also new: the provider note (§5.7(e), §8) and risk R5.
- **This review covers** `design.md` and `preview.html`. Implementation files, tests and build evidence come in the implementation stage, so their absence here is not a defect.
- **If anything is still unresolved after three rounds,** keep the run and its feedback and show the owner the working preview. Do not infer approval. Any change to the UI or a contract needs the owner's input.

## 1. Summary

There are three modes: `system` (the default), `light` and `dark`. The user picks one from a native `<select>` next to the language selector in the shared header. The design has six parts: semantic CSS tokens keyed on `<html data-theme="light|dark">`, with a `prefers-color-scheme` fallback (§5.4); a signal-based `ThemeService` (§5.3); a same-origin, render-blocking `public/theme-init.js` that needs no CSP change (§5.2); en and da copy (§7); dependency-free `node:test` checks (§9); and workflow docs (§5.7). The backend, API and CSP do not change.

## 2. Current state (observed in this worktree)

- Angular `21.2.26` (zoneless, signals). Vitest `4.1.12` runs in jsdom, which has no `matchMedia`. CI runs `npm ci`, `lint`, `test:ci` and `build` on Node `24.22.2`.
- Colours are hex literals (and `white`) in `styles.scss` and `app.scss`. There are no CSS variables.
- The CSP in `RequestGuards.cs` is `script-src 'self'`, with a nonce for styles only. An inline `<script>` is blocked, and a same-origin file is allowed ([S2], [S3]). `SpaHost` replaces `__CSP_NONCE__` and serves non-hashed files as `no-cache`. `public/**` is copied as-is ([S6]) to `dist/subscribed/browser`.
- `LanguageService` (`i18n/i18n.ts`) is the pattern to mirror: a storage token, `persistFailed`, and an `<output class="notice">`.
- `i18n.spec.ts:115` requires `da[key] !== en[key]` for every key except `shell.menu`.
- The workflow docs and `scripts/agentic_flow.py:217` name Codex as the frontend implementer. This base has no text about a Claude implementation provider.

## 3. Assumptions

1. The effective theme is `light` or `dark`. No preference, or a missing or throwing `matchMedia`, gives light ([S1]).
2. Choosing System stores `"system"`. A missing or invalid value acts as `system` and is not rewritten.
3. The theme and language notices are independent.
4. Disabled controls keep `opacity: .6`, because inactive components are exempt ([S4]).
5. The Danish copy (§7) needs human approval. Wording may change, but the keys may not.

## 4. Alternatives considered

| Topic | Option | Verdict |
| --- | --- | --- |
| Pre-render | Inline `<script>` | Rejected. It needs a nonce or a hash ([S2]), which means changing the CSP. |
| | **External `public/theme-init.js`, blocking, in `<head>`** | **Chosen.** `'self'` allows it ([S3]), and it runs before `<body>`. |
| | `angular.json` `scripts` | Rejected. The docs do not specify when these scripts run ([S6]). |
| | CSS media query only | Not enough for explicit overrides. Kept as the fallback. |
| `data-theme` | **Resolved `light`/`dark`** | **Chosen.** Two CSS branches. Storing the mode instead would duplicate the dark tokens. |
| Selector | **Native `<select>`** | **Chosen.** It matches the language selector and is accessible. |
| | Radios or a toggle | Rejected. Radios are too wide at 320 px, and a toggle cannot express System. |
| Danish `theme.system` | **`Følg systemet`** | **Chosen.** Natural Danish with the same meaning, and it passes the differs-from-English rule. |
| | `System` plus a spec exemption | Rejected. It weakens a localization guard. `Automatisk` was also rejected because it changes the meaning. |
| Init test | **`node:test` + static text checks (`scripts/theme-init-contract.mjs`)** | **Chosen.** Checks the shipped source text with no dependency and no `node:vm` or other dynamic execution ([S8]). Runtime mode and persistence behaviour is tested in `theme.spec.ts`. Vitest cannot bundle `public/`, and Playwright adds a dependency. |
| | `node:test` + `node:vm` fake-global harness | Replaced after publication (Sonar remediation). It executed the script dynamically. |

## 5. Architecture

### 5.1 Files (all under `frontend/`)

```
public/theme-init.js            NEW  pre-render IIFE (plain classic script, let/const, no imports)
src/index.html                  EDIT <script src="theme-init.js"></script> after <base>
src/app/theme/theme.ts          NEW  types, constants, tokens, helpers, ThemeService
src/app/theme/theme.spec.ts     NEW  service unit tests (Vitest)
src/styles.scss                 EDIT token mixins (light/dark) and their use
src/app/app.scss                EDIT use tokens; .preferences layout
src/app/app.html / app.ts       EDIT theme <select>, notice, onTheme()
src/app/app.spec.ts             EDIT shell theme tests; provide theme tokens
src/app/i18n/catalog.{en,da}.ts EDIT theme.* keys (§7)
scripts/theme-init-contract.mjs NEW  shared static text checks for theme-init.js (T-I, T-D)
scripts/theme.test.mjs          NEW  node:test – init script contract, contrast, no hex literals
scripts/theme-dist.test.mjs     NEW  node:test – production dist checks
package.json                    EDIT test:ci and build chain the node tests (§9)
README.md                       EDIT "Themes" section
```

`src/app/i18n/i18n.spec.ts` is **not** edited. Workflow docs at the repository root (§5.7): `docs/agentic-flow.md` (EDIT) and `.agents/skills/agentic-flow/SKILL.md` (EDIT).

### 5.2 Pre-render script contract (`public/theme-init.js`)

```js
// Keep in sync with src/app/theme/theme.ts (isThemeMode/resolveTheme). Checked statically by scripts/theme.test.mjs.
(function () {
  let mode = 'system';
  try {
    const stored = window.localStorage.getItem('subscribed.theme');
    if (stored === 'light' || stored === 'dark' || stored === 'system') mode = stored;
  } catch { /* storage unavailable: behave as system */ }
  let theme = mode;
  if (mode === 'system') {
    try { theme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'; }
    catch { theme = 'light'; }
  }
  document.documentElement.dataset.theme = theme;
})();
```

This sample matches the shipped `public/theme-init.js`. The DOM result remains the `data-theme` attribute on `<html>`.

- In `index.html`, the script tag goes directly after `<base href="/">`, with no `defer`, `async` or `type="module"`. Because of `<base>`, deep links resolve it to `/theme-init.js`.
- The script sets that one attribute and never throws. It never writes storage or inline `style`.

### 5.3 `ThemeService` API contract (`src/app/theme/theme.ts`)

```ts
export const THEME_MODES = ['system', 'light', 'dark'] as const;
export type ThemeMode = (typeof THEME_MODES)[number];
export type Theme = 'light' | 'dark';
export const THEME_STORAGE_KEY = 'subscribed.theme';
export const DEFAULT_THEME_MODE: ThemeMode = 'system';
export function isThemeMode(value: unknown): value is ThemeMode;
export function resolveTheme(mode: ThemeMode, systemDark: boolean): Theme;
export const THEME_STORAGE: InjectionToken<Pick<Storage, 'getItem' | 'setItem'> | null>;
  // root factory: try { DOCUMENT.defaultView?.localStorage ?? null } catch { null }
export const COLOR_SCHEME_QUERY: InjectionToken<MediaQueryList | null>;
  // root factory: try { defaultView?.matchMedia?.('(prefers-color-scheme: dark)') ?? null } catch { null }

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly mode: Signal<ThemeMode>;       // default 'system'
  readonly systemDark: Signal<boolean>;   // live; false without a query
  readonly theme: Signal<Theme>;          // computed(resolveTheme(mode, systemDark))
  readonly persistFailed: Signal<boolean>;
  setMode(mode: ThemeMode): void;
  dismissPersistNotice(): void;
}
```

| Situation | Result |
| --- | --- |
| Stored value is `system`, `light` or `dark` | `mode` is set to that value. |
| Stored value is missing or invalid | `mode` = `system`. Nothing is written and no notice is shown. |
| Storage token is `null`, or `getItem` throws | `mode` = `system`, and `persistFailed` = true. |
| `setMode(m)` | Sets `mode` first, then calls `setItem`. A successful write clears `persistFailed`. A `null` token or a throw sets it. The choice applies either way. |
| System `change` event | Updates `systemDark`. `theme` changes only while the mode is `system`. |
| `effect` | Writes `document.documentElement.dataset['theme'] = theme()`, which sets the `data-theme` attribute. |

- The listener uses `addEventListener('change', …)` ([S7]) and is removed through `DestroyRef`. The service makes no HTTP calls and does not depend on the language, session or account.

### 5.4 Semantic palette (`styles.scss`)

The light values are the current colours, so the light theme looks unchanged. **The parseable format is a contract** that T-C depends on:

```scss
@mixin light-tokens {
  color-scheme: light;
  --color-bg: #f4f5f7;   // one `--color-<name>: #rrggbb;` per line, lowercase 6-digit hex
}
@mixin dark-tokens { color-scheme: dark; … }
:root, :root[data-theme='light'] { @include light-tokens; }
:root[data-theme='dark'] { @include dark-tokens; }
@media (prefers-color-scheme: dark) { :root:not([data-theme]) { @include dark-tokens; } }
```

| Token | Use | Light | Dark |
| --- | --- | --- | --- |
| `--color-bg` | page | `#f4f5f7` | `#12151b` |
| `--color-surface` | header, nav, panel, empty state, input, select, secondary button | `#ffffff` | `#1b2029` |
| `--color-text` | body text, nav links | `#1d2330` | `#e6e9ef` |
| `--color-text-muted` | `.muted`, `.eyebrow` | `#505a6b` | `#aab2c0` |
| `--color-border` | decorative dividers | `#d6dae1` | `#343b48` |
| `--color-control-border` | input and select borders | `#667080` | `#8a94a6` |
| `--color-primary` | brand, links, primary button, secondary button text and border | `#2453a6` | `#8ab0ff` |
| `--color-on-primary` | primary button text | `#ffffff` | `#0b1630` |
| `--color-nav-active-bg` / `-text` | active nav item | `#e7eefb` / `#2453a6` | `#24324d` / `#b9cfff` |
| `--color-focus` | `:focus-visible` outline | `#876000` | `#f5c451` |
| `--color-error` | `.error` text | `#8d201d` | `#ff9e97` |
| `--color-invalid` | `[aria-invalid=true]` border | `#a3231f` | `#ff8a80` |
| `--color-notice-bg` / `-border` | `.notice` | `#fff4dc` / `#c4943b` | `#352a12` / `#c99a3e` |
| `--color-skeleton` | `.skeleton` (decorative) | `#e3e6eb` | `#2b313c` |
| `--color-empty-border` | `.empty` dashed border (decorative) | `#b6bdc7` | `#6b7486` |

Rules: no hex literal and no `white` or `black` keyword in `app.scss`, or in `styles.scss` outside the two mixins (T-H checks this). `button:disabled` keeps `opacity: .6`. The skip link, both selects and the notice use tokens.

### 5.5 Contrast pairs (enforced by T-C)

The thresholds are 4.5:1 for text ([S5]) and 3:1 for UI components and focus indicators ([S4]). The values below were **hand-computed** with the WCAG formula and rounded down. No tool has checked them yet. T-C checks every pair and fails if one falls below its threshold.

| Pair (fg / bg) | Min | Light | Dark |
| --- | --- | --- | --- |
| text / bg, text / surface | 4.5 | 14+, 15+ | 15.0, 13.4 |
| text / nav-active-bg, text / notice-bg | 4.5 | 13+, 14+ | 10+, 11.5 |
| text-muted / bg, surface | 4.5 | 6.3, 6.9 | 8.5, 7.6 |
| primary / surface, bg | 4.5 | 7.3, 6.7 | 7.5, 8.4 |
| on-primary / primary | 4.5 | 7.3 | 8.3 |
| nav-active-text / nav-active-bg | 4.5 | 6.2 | 8.2 |
| error / surface, bg | 4.5 | 8.9, 8.2 | 8.2, 9.2 |
| control-border / surface, bg | 3 | 5.0, 4.5 | 5.3, 5.9 |
| invalid / surface | 3 | 7.4 | 7.1 |
| focus / bg, surface, nav-active-bg, notice-bg | 3 | 5.2, 5.6, 4.8, 5.1 | 11.2, 10.0, 7.8, 8.6 |

The 3 px offset keeps the focus ring next to one of the four backgrounds listed. If T-C fails, change only the **dark** value, update this table in the PR, and never lower a threshold.

### 5.6 Shell UI (`app.html`)

```html
<div class="preferences">
  <label for="theme-select" class="pref-label">{{ 'theme.label' | t }}</label>
  <select id="theme-select" [ngModel]="theme.mode()" (ngModelChange)="onTheme($event)">
    @for (mode of themeModes; track mode) { <option [value]="mode">{{ themeLabels[mode] | t }}</option> }
  </select>
</div>
```

- The selector goes in `.header-actions` after the language selector. That container renders in **every** session state, route and width, outside the member-only `<nav>`.
- `themeLabels: Record<ThemeMode, MessageKey>`. `onTheme(value)` ignores anything that fails `isThemeMode`.
- Below 900 px, `.pref-label` is visually hidden but stays the programmatic label. Targets are at least 44 px tall, and nothing overflows at 320 px, even with `Følg systemet`.
- The notice goes in `main`, after the language notice: `@if (theme.persistFailed()) { <output class="notice">{{ 'theme.saveFailed' | t }} <button type="button" class="secondary" (click)="theme.dismissPersistNotice()">{{ 'theme.dismiss' | t }}</button></output> }`. `<output>` has an implicit polite `status` role.
- Switching changes only `data-theme`: no navigation, no `session.load()`, no focus move, and inputs keep their values.

### 5.7 Workflow docs: UI-design escalation and implementation provider

These elements were accepted in the earlier runs. The wording may be tightened, but elements (a) to (e) must appear in both files, and nothing else changes. Keep the `SKILL.md` frontmatter and links intact, because `tests/test_agentic_flow.py` checks them. Keep the existing screenshot guidance for normal approval.

- In `docs/agentic-flow.md`, add after "A backend-only task has no UI preview requirement.":
  > If a UI design is unresolved after three review rounds, the run stops. (a) Keep the run state and all feedback. (b) Show the owner the working `preview.html` and the open feedback. (c) Use a screenshot only if the HTML cannot render or the owner asks for one. (d) Do not infer approval or reset or bypass the limit. Only explicit owner approval, or a genuinely revised new run, moves the work forward.

  Also add to the "Three unsuccessful attempts" bullet: "For UI designs, follow the escalation in *Run a feature*."
- In `.agents/skills/agentic-flow/SKILL.md`, step 4, after "Unresolved or malformed reviews stop the workflow.", add: "After three unresolved UI-design rounds, keep the run and feedback, show the owner the working HTML preview (a screenshot only if it cannot render or the owner asks), and never infer approval or reset the limit."
- (e) **Implementation provider.** If the target text already says that Claude can implement frontend work (for example in an owner-directed retry such as this one), keep it word for word. If the base still says only "Codex implements frontend work", add next to that sentence in both files: "The owner may direct a distinct retry run to use Claude as the frontend implementation provider. Codex remains the default." This task does **not** change `scripts/agentic_flow.py` or its provider selection.

## 6. Error behaviour

- Storage throws or holds an invalid value: as in §5.3. The init script falls back to the system theme.
- `matchMedia` is missing or throws: Light, with no listener.
- `theme-init.js` fails to load: the CSS fallback gives System. An explicit choice then applies at bootstrap, and a brief flash is accepted.

## 7. Catalog additions

| Key | en | da |
| --- | --- | --- |
| `theme.label` | Theme | Tema |
| `theme.system` | System | **Følg systemet** |
| `theme.light` | Light | Lys |
| `theme.dark` | Dark | Mørk |
| `theme.saveFailed` | Your theme choice couldn't be saved in this browser. It applies until you reload the page. | Dit valg af tema kunne ikke gemmes i denne browser. Det gælder, indtil du genindlæser siden. |
| `theme.dismiss` | Dismiss | Luk |

Every Danish value differs from its English value, so the existing parity test and the differs-from-English test (`i18n.spec.ts`) cover these keys with no change to the spec. The `shell.menu` exemption is not extended.

## 8. Ownership

| Area | Owner | Files |
| --- | --- | --- |
| Frontend | **Frontend implementation role.** In this retry the provider is **Claude**, as the owner directed. | Every file in §5.1, including the two workflow docs in §5.7. No other edits under `docs/`, `.agents/`, `.claude/`, `scripts/agentic_flow.py` or `tests/`. |
| Backend | **None.** This run has no backend task. | Must not edit `RequestGuards.cs`, `SpaHost.cs`, anything under `backend/`, or `.github/workflows/`. The existing CI steps already run the new checks through the npm scripts. |
| Shared contracts | Settled here | The key `subscribed.theme`; the values `system`, `light`, `dark`; `<html data-theme="light|dark">`; the asset `/theme-init.js`; the token-mixin format (§5.4). There is no HTTP API and no migration. |
| Design artifacts | Claude (design) | `docs/agentic/issue-1-dark-mode-claude-retry/*` only. The earlier runs' directories are not touched. |

**Auth boundary:** none. The theme is browser-local and never sent to the backend. It survives sign-out and is independent of the account and of `subscribed.language`.

## 9. Test strategy

**Required checks (implementation stage):** `npm run lint`, `npm run test:ci` and `npm run build`. Their real output is recorded in the PR, and a failure is reported as a failure. The `package.json` scripts become:

```json
"test:ci": "ng test --watch=false && node --test scripts/theme.test.mjs",
"build": "ng build --configuration production && node --test scripts/theme-dist.test.mjs"
```

`node --test` fails on a non-zero exit ([S8]). The `.mjs` files are dependency-free ESM using only `node:` built-ins (`eslint src` does not lint them).

### Vitest (`ng test`, jsdom)

Fakes for `MediaQueryList` (with a `fire(matches)` helper) and for storage are injected through `COLOR_SCHEME_QUERY` and `THEME_STORAGE`. `theme.spec.ts`:

1. **Default:** with nothing stored, the mode is `system`. The theme follows the system (light or dark), and nothing is written.
2. **No preference:** a `null` query gives `light`.
3. **Live:** in `system` mode, `fire(true)` and `fire(false)` switch the theme and the attribute.
4. **Overrides:** `light` and `dark` ignore system changes. Going back to `system` follows the system again.
5. **Persistence:** each mode is written to `subscribed.theme`, and a new instance restores it.
6. **Invalid values:** `'Dark'`, `'auto'`, `''`, `'null'` and `'{"a":1}'` give `system`, with no write and no notice.
7. **Storage failures:** a `null` token, or a throwing `getItem` or `setItem`, still applies the choice and shows the notice. A later successful write, or a dismiss, clears it.
8. **Cleanup:** destroying the injector removes the listener. 9. **Helpers:** truth tables for `isThemeMode` and `resolveTheme`.

`app.spec.ts`:

10. **Visibility:** `#theme-select` and its `label[for]` exist in every session state and on `/signed-out`. The options are System, Light and Dark, with System selected by default.
11. **Live apply:** a member on `/household` types into `#household-name` and then picks Dark. `data-theme` changes, and the URL, the input value and focus are kept. There is no navigation, no `session.load` call and no HTTP request.
12. **Localization:** in Danish the label is `Tema` and the options are `Følg systemet`, `Lys` and `Mørk`. The notice text is localized, and Dismiss works.
13. **Independence:** a theme change never writes `subscribed.language`, and a language change never writes `subscribed.theme`.

The unchanged `i18n.spec.ts` (parity and differs-from-English) must pass. This is the check the previous run failed.

### Node (`scripts/theme.test.mjs`, part of `test:ci`)

- **T-I, init script contract:** reads `public/theme-init.js` as text and applies the static checks exported by `scripts/theme-init-contract.mjs`. The checks compare source text against the §5.2 contract: the storage key `subscribed.theme`, strict comparison against exactly `system`, `light` and `dark`, the `(prefers-color-scheme: dark)` query with a light fallback, storage and `matchMedia` access guarded by `try`/`catch`, one `dataset.theme` write, and no storage write, inline `style`, `import` or `export`. **No `node:vm`, `eval`, `Function` or other dynamic execution is used.** Runtime mode resolution and persistence behaviour (stored, missing and invalid values, system changes, storage failures) is tested in `theme.spec.ts` (tests 1–9), not by executing the init script.
- **T-C, contrast:** the test extracts each `--color-*: #rrggbb` from both mixins, asserts that the two mixins define the same token set and cover every token in §5.4, computes WCAG 2.x relative luminance and contrast ([S5]), and checks every §5.5 pair in both themes. A failure message names the token, the theme and the ratio.
- **T-H, no hard-coded colours:** `src/app/app.scss` contains no `#[0-9a-fA-F]{3,8}\b`, `white` or `black` colour value. The same holds for `src/styles.scss` once the two mixin bodies are removed.

### Node (`scripts/theme-dist.test.mjs`, part of `build`)

- **T-D, build output:** runs against `dist/subscribed/browser/` and fails clearly if `index.html` is missing. It asserts that (1) there is exactly one `<script src="theme-init.js">`, it is inside `<head>`, and it has no `async`, `defer` or `type="module"`; (2) every `<script>` has a `src`, so there is no inline script; (3) `ngCspNonce="__CSP_NONCE__"` is still present; (4) `theme-init.js` is byte-identical to `public/theme-init.js`; and (5) the dist copy's text passes the same static checks from `scripts/theme-init-contract.mjs` as T-I. Like T-I, T-D never executes the script.

**Workflow docs (§5.7):** `python3 -m unittest discover -s tests -p 'test_agentic_flow.py'` still passes. Copilot checks the diff for (a) to (e).

**Manual, recorded in the PR (not a gate):** with the build served through the API, reload with each stored value and with an invalid one. Check the first paint, check that the console shows no CSP violation, and check the focus ring in both themes.

## 10. Acceptance criteria (measurable)

1. A labelled theme `<select>` with System, Light and Dark is visible in the header in every session state and at 320, 480, 900 and 1280 px, and it works with the keyboard (tests 10 and 11).
2. With no stored value, the theme follows `prefers-color-scheme` live. Without `matchMedia`, it is Light (tests 1–3; T-I checks that the init script has the same fallback in its source).
3. Light and Dark override the system setting and apply immediately, with no reload, navigation or session reload, and no lost input or focus (tests 4 and 11).
4. `subscribed.theme` ∈ {`system`, `light`, `dark`}. Invalid values are ignored and left as they are. If storage fails, the selector keeps working and a dismissible, localized `<output>` appears (tests 5–7 and 12).
5. The first paint after a reload matches the stored mode, using only `/theme-init.js` from `'self'`. The backend files and the CSP are unchanged. T-I and T-D check the script's source and its placement statically; first paint itself is confirmed by the manual check in §9.
6. Every app-owned colour comes from a token (T-H). Every §5.5 pair meets its threshold in both themes (T-C), and focus is visible in both themes.
7. The theme is independent of the account, sign-out and the language (test 13).
8. The en and da catalogs contain the §7 keys with Danish `theme.system` = `Følg systemet`. `i18n.spec.ts` passes unmodified.
9. The README "Themes" section covers the modes and their precedence, `subscribed.theme`, the notice, `theme-init.js` and the CSP, adding a token, and the node checks.
10. `npm run lint`, `npm run test:ci` and `npm run build` pass locally and in CI, and the PR reports their actual results. The `anyComponentStyle` budget (4 kB warning) holds.
11. Both workflow docs contain elements (a) to (e) from §5.7, and the agentic-flow unit tests pass.
12. `preview.html` looks the same as the approved preview. Its source carries only the post-publication semantic accessibility fixes (§0), so it differs in bytes but has no intended visual change.

## 11. Risks

- **R1, the build strips the head script:** T-D fails. Report a blocker and never inline the script.
- **R2, the init script and the service drift apart:** tests 1–7 cover runtime behaviour in the service. T-I statically checks that the init script's source keeps the same key, mode values and fallbacks, but does not execute it, so keep the "Keep in sync" comment and review both files together.
- **R3, a contrast value is wrong:** T-C gates it. Change dark values only.
- **R4, a reviewer asks for UI changes:** get the owner's approval again first (§0).
- **R5, a Danish string matches English again:** run `test:ci` before review and never extend the exemption. The preview's Danish `System` is explained in §0.

## 12. Out of scope

Backend, CSP and CI changes; the orchestrator and its counters; account sync; extra themes; IdP pages; e2e tooling; later milestones.

## Sources (fetched 2026-11-13)

- [S1] MDN `prefers-color-scheme`: `light` covers "has not expressed an active preference". https://developer.mozilla.org/en-US/docs/Web/CSS/@media/prefers-color-scheme
- [S2] MDN CSP `script-src`: inline scripts need `'unsafe-inline'`, a nonce or a hash. https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Security-Policy/script-src
- [S3] MDN CSP `'self'`: allows only same-origin loads. https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Security-Policy
- [S4] W3C SC 1.4.11: "at least 3:1"; does "not apply to inactive user interface components". https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html
- [S5] W3C SC 1.4.3: "at least 4.5:1"; luminance defined from sRGB. https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html
- [S6] Angular workspace config: assets are copied "as-is"; `scripts` timing is not defined. https://angular.dev/reference/configs/workspace-config
- [S7] MDN `MediaQueryList` `change`: "Baseline: Widely available" since September 2020. https://developer.mozilla.org/en-US/docs/Web/API/MediaQueryList/change_event
- [S8] Node test runner: "Stability: 2 - Stable". https://nodejs.org/api/test.html
- [S9] Node VM: Stable; "not a security mechanism". https://nodejs.org/api/vm.html (No longer used: T-I and T-D are static text checks. Kept for history.)

**Versions:** no dependency is added. Angular `21.2.26`, build `21.2.29` and Vitest `4.1.12` come from `frontend/package.json`, and Node `24.22.2` from `ci.yml`. The pages for [S8] and [S9] showed **v26.13.1**, not v24. Their behaviour on Node 24.22.2 is declared, not runtime-tested here; CI at the implementation stage is the runtime test. [S4]–[S7] and [S9] were re-fetched today. [S1]–[S3] and [S8] were verified in the approved run on the same day.
