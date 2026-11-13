# Design: English/Danish localization of the frontend (localization-issue-2)

Status: draft for Copilot review and human approval. Scope: **frontend only**.
Companion mockup: [`preview.html`](preview.html). It is a static mockup with synthetic data, not working UI.

## 1. Summary

A small signal-based layer: a typed `en`/`da` registry, two TS catalogs with stable dot IDs and `{named}` parameters, a `LanguageService` (resolve, persist, apply), and an impure `t` pipe. Hard-coded strings in `app.html`, `foundation-page.*` and `session.ts` move into the catalogs. Status/error messages are stored as **message descriptors** (key + params) so they re-translate on a switch. Membership dates use `en-GB` / `da-DK`. The backend and API do not change.

## 2. Current state (observed in this worktree)

- Angular `21.2.26` (`frontend/package.json`), zoneless (no `zone.js`/`provideZoneChangeDetection`); template signal reads drive rendering.
- `foundation-page.ts` shows a 400's `errors.name[0]` verbatim. That is backend English prose and must stop.
- `index.html`: `lang="en"`, `<title>Subscribed</title>` (product name only). `DatePipe` uses `'mediumDate'` with the default `LOCALE_ID` (`en-US`).
- CSP (`Bff/RequestGuards.cs`): `script-src 'self'`, `connect-src 'self'`, no `unsafe-eval`.
- Problem responses (`Problems/Problem.cs`) carry a stable `type: "urn:subscribed:problem:<code>"`, `traceId` and `errors: { <camelCaseField>: [...] }`. That is enough to identify errors without matching prose.

## 3. Assumptions

1. No message contains a count, so ICU plurals are not needed now (§11).
2. The document title stays `Subscribed` (no translatable page text today). Future route titles must use the catalog.
3. Issue #1 (theme) is not implemented. A `.preferences` header slot is reserved for it, not designed.
4. The Danish copy in §6 needs approval. Implementers may make approved wording fixes but must not change keys or parameters.
5. Storage key: `subscribed.language`. Valid values are exactly `en` or `da`. Any other value counts as invalid.

## 4. Alternatives considered

| Option | Runtime switch without reload | CSP | Verdict |
| --- | --- | --- | --- |
| `@angular/localize` (built-in) | No. The docs describe building "a separate distributable copy of the application for each locale" and serving copies by "server-side language detection or different subdirectories" [A1]. Switching means a full navigation, which loses unsaved input. | OK | Rejected: conflicts with the immediate, no-reload switching requirement. |
| `@jsverse/transloco` | Yes | OK (JSON over XHR, `connect-src 'self'`) | Viable. Latest `8.4.0`, peer `@angular/core: ">=16.0.0"`, `rxjs: ">=6.0.0"` [A2]. That is **declared** compatibility only; it has not been runtime-tested with Angular 21.2.26 in this repo. |
| `@ngx-translate/core` | Yes | OK | Viable. Latest `18.1.0`, peer `@angular/core: ">=18"`, `@angular/common: ">=18"`, `rxjs: ">=7"` [A3]. Declared compatibility only, not runtime-tested here. |
| **Custom signal service + typed TS catalogs** | Yes | OK: no eval, no network, no `innerHTML` | **Chosen.** |

**Why custom.** About 60 messages, 2 languages, zoneless signal-first app. A ~100-line service gives compile-time key parity, no dependency or loader, synchronous switching, and descriptors that fit signals. Cost: we own interpolation/fallback and have no translator file format; Transloco (§11) is a compatible later migration.

## 5. Architecture (all files under `frontend/src/app/`)

```
i18n/languages.ts        SUPPORTED_LANGUAGES registry, Language type, DEFAULT_LANGUAGE='en'
i18n/catalog.en.ts       export const en = {...} as const  (source of truth for keys)
i18n/catalog.da.ts       export const da: Catalog = {...}
i18n/i18n.ts             MessageKey, Catalog, Message, LANGUAGE_STORAGE, BROWSER_LANGUAGES,
                         resolveLanguage(), formatMessage(), LanguageService
i18n/translate.pipe.ts   TranslatePipe  ('t', standalone, pure: false)
```

### 5.1 Language registry

```ts
export const SUPPORTED_LANGUAGES = [
  { code: 'en', endonym: 'English', dateLocale: 'en-GB' },
  { code: 'da', endonym: 'Dansk',   dateLocale: 'da-DK' },
] as const;
export type Language = (typeof SUPPORTED_LANGUAGES)[number]['code'];
export const DEFAULT_LANGUAGE: Language = 'en';
```

Endonyms are never translated. Each option shows its own name in its own language and has an option `lang` attribute.

### 5.2 Catalogs and types

```ts
export type MessageKey = keyof typeof en;
export type Catalog = Readonly<Record<MessageKey, string>>;
export interface Message { key: MessageKey; params?: Readonly<Record<string, string>>; }
const CATALOGS: Readonly<Record<Language, Partial<Catalog>>> = { en, da };
```

`da` is declared `: Catalog`, so a missing or extra key fails `ng build` and type-checking. `CATALOGS` is typed `Partial` on purpose, so the runtime fallback (§5.4) stays exercised and testable.

### 5.3 Resolution: `resolveLanguage(stored, browserLanguages)` (pure function)

1. If `stored` is exactly `'en'` or `'da'`, use it. Otherwise ignore it and do not delete it.
2. Go through `browserLanguages` in order. For each tag, lowercase it and take the primary subtag before the first `-` or `_` (`da-DK`→`da`, `en-US`→`en`, `EN`→`en`). The first one that is supported wins.
3. Otherwise use `'en'`.

`BROWSER_LANGUAGES` is an `InjectionToken<readonly string[]>`. Its default factory returns `navigator.languages`, and if that is empty, `[navigator.language]`, and if that is unavailable, `[]`. MDN: `navigator.languages` is "ordered by preference with the most preferred language first", and `navigator.language` is its first element [A4].

### 5.4 `LanguageService` (`providedIn: 'root'`)

```ts
readonly language: Signal<Language>;        // active language
readonly dateLocale: Signal<string>;        // 'en-GB' | 'da-DK'
readonly persistFailed: Signal<boolean>;    // last explicit choice could not be saved
setLanguage(lang: Language): void;          // explicit user choice
dismissPersistNotice(): void;
translate(key: MessageKey, params?: Record<string, string>): string;
message(m: Message | null | undefined): string;   // '' for null
```

**Storage.** `LANGUAGE_STORAGE` is an `InjectionToken<Pick<Storage,'getItem'|'setItem'> | null>`. Its factory returns `window.localStorage` and returns `null` if the property access throws (some browsers throw `SecurityError` when storage is blocked).

- Reading wraps `getItem` in `try/catch`. A throw counts as "no stored value".
- `setLanguage` updates the signal **first**, then calls `setItem` in `try/catch`. A throw (for example `QuotaExceededError` [A5]) or a `null` storage sets `persistFailed` to `true`. The switch still applies.
- A later successful write clears `persistFailed`. Startup resolution **never writes**, so a browser-derived default never becomes sticky.

**Persistence across auth.** Origin-scoped `localStorage` survives `/bff/login` and `/bff/logout` redirects. `SessionService.signOut` must not clear it. It is per-browser only.

**Side effects.** An `effect` (with `DOCUMENT` injected) sets `document.documentElement.lang = language()`. `index.html` keeps `lang="en"` as the pre-bootstrap default. The service is constructed by `App` before the first render, so the first render uses the resolved language.

**`translate`.**

- Template lookup: `CATALOGS[lang][key]`. If that is `undefined` or `''`, use `en[key]`. A fallback emits one `console.warn` per key and language, only when `isDevMode()`.
- `formatMessage(template, params)` replaces `/\{([A-Za-z][A-Za-z0-9]*)\}/g` with `params[name]`.
  - A missing parameter is replaced with `''` and gets a dev warning. A raw `{name}` is never shown.
  - Parameter values are inserted once, so a value that contains `{x}` is not re-interpolated.
- Output is plain text, bound only through Angular text or attribute interpolation (auto-escaped). **No `innerHTML`.**

### 5.5 `TranslatePipe`

`{{ 'household.save' | t }}` and `{{ 'home.welcome' | t: { name: user.displayName } }}`. It is `pure: false` and calls `translate`, which reads the `language()` signal. In zoneless Angular, the template's reactive context tracks that read, so changing the language re-renders bound text and attributes. Attribute use: `[attr.aria-label]="'shell.primaryNav' | t"`.

### 5.6 Message descriptors in components

| Current string signal | New type |
| --- | --- |
| `SessionService.logoutError: string\|null` | `Message\|null` (`{key:'shell.signOutFailed'}`) |
| `FoundationPage.authError` (computed string) | `computed<MessageKey\|null>` |
| `problem`, `fieldError`: `string\|null` | `Message\|null` |
| `copyMessage: string` | `Message\|null` |
| `traceId`, `conflict` | unchanged types; lifecycle in §7.1 |

Templates render these with `{{ i18n.message(problem()) }}` or `{{ key | t }}`. Values (`traceId`, `reference()`, `displayName`, `displayLabel`, household `name`) stay raw and are passed only as parameters or placed next to translated labels.

### 5.7 Dates

Register the locale data once in `app.config.ts` (module scope): `registerLocaleData(localeEnGb, 'en-GB')` and `registerLocaleData(localeDa, 'da-DK')`, importing from `@angular/common/locales/en-GB` and `@angular/common/locales/da` (both present in `@angular/common@21.2.26` [A6]). The `registerLocaleData(data, localeId?, extraData?)` signature is from [A7]. The DatePipe docs: "Only the en-US locale data comes with Angular" [A8].

The template becomes:
`{{ 'household.joined' | t: { date: (member.grantedAt | date:'mediumDate':undefined:i18n.dateLocale()) } }}`.

The timezone argument stays `undefined` (user's local timezone, unchanged) [A8]. `LOCALE_ID` stays unchanged. Locale is always passed explicitly, so no global locale switch is needed. Expected output for `2026-03-05T10:00:00Z` (from CLDR conventions, not yet runtime-verified): en `5 Mar 2026`, da `5. mar. 2026`. Tests assert locale-specific fragments (`Mar`, `mar.`) rather than full strings, to stay robust to CLDR updates.

### 5.8 Language selector (shell)

In `app.html`, inside `<div class="preferences">` (in `.header-actions`, before sign-out), rendered in **every** session state:

```html
<label for="language-select" class="pref-label">{{ 'language.label' | t }}</label>
<select id="language-select" [value]="i18n.language()" (change)="onLanguage($event)">
  @for (l of languages; track l.code) {<option [value]="l.code" [attr.lang]="l.code">{{ l.endonym }}</option>}
</select>
```

- **Native `<select>`:** keyboard-operable, exposes its value to assistive technology, named by the `<label>`. `onLanguage` validates the value against the registry before calling `setLanguage`.
- Switching does not navigate, call `session.load()` or move focus. `FoundationPage.name` (an `ngModel` field) is untouched, and the `NavigationEnd` focus handler does not fire.
- **Mobile (`max-width: 899px`):** `.pref-label` is visually hidden (`sr-only`, name kept). The select stays in the header.
- **Header DOM:** `.preferences` and the sign-out button sit together in a `<div class="header-actions">` after `.spacer` (`display:flex; align-items:center; gap:1rem`). DOM/tab order is brand → Menu → language → sign-out.
- **Narrow (`max-width: 479px`, covers 360 px and 320 px):** header gets `height:auto; min-height:4rem; flex-wrap:wrap; padding:.5rem 1rem; gap:.5rem .6rem`. `.header-actions` gets `flex-basis:100%; justify-content:flex-end; flex-wrap:wrap`. Row 1 holds brand + Menu, and row 2 holds the language select + sign-out. This leaves room for the longest label (`Logger ud...`). Nothing is clipped or overlaps at 360 or 320 px (WCAG 1.4.10). The `.layout.member` min-height calc (≥900px only) is unaffected.
- **Theme slot:** a future theme control goes inside `.preferences` with its own label. Not specified here.
- **Persist notice:** when `persistFailed()` is true, `main` shows a non-blocking `<div class="notice" role="status">` with `language.saveFailed` and a `language.dismiss` button. Focus does not move.

## 6. Message catalog contract

Parameters are written `{name}`. Both catalogs must contain exactly these keys. **Untranslated allowlist** (identical in both): `shell.menu`. The product name `Subscribed` appears inside messages and is not translated.

| Key | en | da |
| --- | --- | --- |
| shell.skipLink | Skip to content | Gå til indhold |
| shell.menu | Menu | Menu |
| shell.primaryNav | Primary | Primær |
| shell.nav.home | Home | Forside |
| shell.nav.household | Household | Husstand |
| shell.signOut | Sign out | Log ud |
| shell.signingOut | Signing out... | Logger ud... |
| shell.signOutFailed | Sign out failed. Your session may still be active. Please retry. | Udlogningen mislykkedes. Din session kan stadig være aktiv. Prøv igen. |
| shell.sessionExpired | Your session expired. | Din session er udløbet. |
| shell.signInAgain | Sign in again | Log ind igen |
| language.label | Language | Sprog |
| language.saveFailed | Your language choice couldn't be saved in this browser. It applies until you reload the page. | Dit sprogvalg kunne ikke gemmes i denne browser. Det gælder, indtil du genindlæser siden. |
| language.dismiss | Dismiss | Luk |
| common.signIn | Sign in | Log ind |
| common.retry | Retry | Prøv igen |
| common.referenceLabel | Reference: | Fejlreference: |
| signedOut.heading | You are signed out | Du er logget ud |
| signedOut.idpActive | Your organisation sign-in may still be active. You have been signed out of Subscribed. | Dit login hos din organisation kan stadig være aktivt. Du er logget ud af Subscribed. |
| loading.heading | Getting your household ready | Gør din husstand klar |
| loading.status | Checking your session... | Tjekker din session... |
| unavailable.heading | We couldn't reach your household | Vi kunne ikke få forbindelse til din husstand |
| unavailable.message | Your session could not be checked. This does not mean you are signed out. | Din session kunne ikke kontrolleres. Det betyder ikke, at du er logget ud. |
| anonymous.eyebrow | YOUR HOUSEHOLD, IN ONE PLACE | HELE DIN HUSSTAND ÉT STED |
| anonymous.heading | Welcome to Subscribed | Velkommen til Subscribed |
| anonymous.intro | Keep a clear view of your household's subscription expenses. | Bevar overblikket over din husstands udgifter til abonnementer. |
| anonymous.signIn | Sign in with your organisation | Log ind med din organisation |
| anonymous.idpNote | Sign in is managed by your existing identity provider. | Login håndteres af din eksisterende identitetsudbyder. |
| auth.cancelled | Sign in was cancelled. You can try again. | Login blev annulleret. Du kan prøve igen. |
| auth.failed | Sign in could not be completed. Please try again. | Login kunne ikke gennemføres. Prøv igen. |
| access.heading | Household access needed | Du mangler adgang til husstanden |
| access.notGranted | You are signed in, but haven't been granted access to this household. | Du er logget ind, men har ikke fået adgang til denne husstand. |
| access.askAdmin | Ask the household administrator to grant access using your reference. | Bed husstandens administrator om at give dig adgang med din reference. |
| access.referenceLabel | Your account reference | Din kontoreference |
| access.copy | Copy reference | Kopiér reference |
| access.copied | Reference copied. | Referencen er kopieret. |
| access.copyUnavailable | Automatic copy is unavailable. The reference is selected; copy it manually. | Automatisk kopiering er ikke tilgængelig. Referencen er markeret; kopiér den manuelt. |
| access.checkAgain | Check access again | Tjek adgang igen |
| household.heading | Household settings | Indstillinger for husstand |
| household.loading | Loading household... | Indlæser husstand... |
| household.loadFailed | The household could not be loaded. Please retry. | Husstanden kunne ikke indlæses. Prøv igen. |
| household.reload | Reload household | Genindlæs husstand |
| household.nameHeading | Household name | Husstandens navn |
| household.nameLabel | Name | Navn |
| household.nameHint | 1-80 characters. Visible to everyone in your household. | 1-80 tegn. Synligt for alle i din husstand. |
| household.nameInvalid | Enter 1-80 characters, without control characters. | Indtast 1-80 tegn uden kontroltegn. |
| household.conflict | Someone else changed this household. Your input has been kept. | En anden har ændret husstanden. Dit input er bevaret. |
| household.reloadLatest | Reload latest | Hent seneste |
| household.save | Save name | Gem navn |
| household.saving | Saving... | Gemmer... |
| household.saved | Household name saved. | Husstandens navn er gemt. |
| household.saveFailed | The change could not be saved. Please retry. | Ændringen kunne ikke gemmes. Prøv igen. |
| household.membersHeading | Household members | Medlemmer af husstanden |
| household.joined | Joined {date} | Medlem siden {date} |
| household.membersManaged | Membership is managed by the household administrator. | Medlemskab administreres af husstandens administrator. |
| notFound.heading | Page not found | Siden blev ikke fundet |
| notFound.home | Return home | Gå til forsiden |
| home.welcome | Welcome, {name} | Velkommen, {name} |
| home.ready | Your household is ready. | Din husstand er klar. |
| home.emptyHeading | A fresh start for your household | En frisk start for din husstand |
| home.emptyBody | Subscription tracking is coming in the next milestone. | Overblik over abonnementer kommer i næste milepæl. |
| home.viewHousehold | View household settings | Se indstillinger for husstand |

The header brand link text `Subscribed` stays a literal product name and is not catalogued.

## 7. API contracts and error mapping

**No API, payload, route or backend change.** The frontend consumes fields that already exist. `Problem` in `session.ts` gains `type?: string` (read-only consumption). Mapping uses the HTTP status, the problem `type` and **field keys** only. It never uses `title`, `detail` or `errors[*]` message text.

| Situation | Signal | Message key | Reference shown |
| --- | --- | --- | --- |
| `GET /bff/session` error / malformed | session `unavailable` | unavailable.* | `traceId` if present |
| `authError=cancelled` query | authError | auth.cancelled | – |
| `authError=<other non-empty>` | authError | auth.failed | – |
| `POST /bff/logout` error | logoutError | shell.signOutFailed | – |
| any protected 401 (except session) | `expired` | shell.sessionExpired | – |
| `GET /api/household` error | problem | household.loadFailed | `traceId` |
| client-side invalid name | fieldError | household.nameInvalid | – |
| `PUT …/name` 412 | conflict | household.conflict | `traceId` |
| `PUT …/name` 400, `type === 'urn:subscribed:problem:validation-failed'` **and** `errors` has own key `name` | fieldError | household.nameInvalid | `traceId` |
| `PUT …/name` any other 400 (e.g. `errors.ifMatch`), 403, 413, 415, 428, 5xx, network/status 0, unparseable body | problem | household.saveFailed | `traceId` if present |
| clipboard write ok / rejected | copyMessage | access.copied / access.copyUnavailable | – |

### 7.1 Diagnostic reference display

Every row above with a `traceId` shows it **inside the block that shows that row's message**, whether that block is `problem`, `fieldError` or `conflict`:

```html
@if (traceId()) { <p class="reference">{{ 'common.referenceLabel' | t }} <code>{{ traceId() }}</code></p> }
```

- **`problem` block** (`div.error[role=alert]`): before the Reload button, as today.
- **`fieldError` block.** `<p id="name-error">` becomes `<div id="name-error" class="error" role="alert">` holding `<p>{{ i18n.message(fieldError()) }}</p>` and the reference. The input keeps `aria-describedby="name-hint name-error"`.
- **`conflict` notice** (`div.notice[role=alert]`): after the message, before "Reload latest".
- **Session `unavailable`.** The existing reference paragraph uses the same label key.

**Lifecycle.** Only one server error state is visible at a time, so a stale ID can't be shown next to a newer error:

- `load()` and `save()` first reset `problem`, `fieldError`, `conflict` and `traceId`. The client-side invalid-name path therefore shows no reference.
- A server error then sets exactly one state and `traceId = problem?.traceId`, the raw string, unchanged.
- Switching language changes only the label (`Reference:` / `Fejlreference:`). The `<code>` text stays the same.

**Authentication boundary.** Unchanged. Language selection works without a session and sends no requests.

**Data migration.** None (browser-local key only).

## 8. Ownership

| Area | Owner | Files |
| --- | --- | --- |
| Frontend | Codex (frontend role) | new `frontend/src/app/i18n/{languages,catalog.en,catalog.da,i18n,translate.pipe}.ts` and specs `i18n/{i18n,catalogs,translate.pipe}.spec.ts`; new `frontend/src/app/app.spec.ts`; modified `app.ts`, `app.html`, `app.scss`, `app.config.ts`, `foundation-page.ts`, `foundation-page.html`, `foundation-page.spec.ts`, `session.ts`, `session.spec.ts`, `frontend/src/styles.scss` (sr-only utility only, if not present), `frontend/README.md` |
| Backend | none | No backend files change. The Claude backend role has no task. |
| Shared | none | The API contract is unchanged. `docs/agentic/localization-issue-2-updated-base/*` is owned by design. |

`frontend/src/index.html` is unchanged (`lang="en"` is the pre-bootstrap default). No new npm dependencies.

## 9. Test strategy (Vitest via `ng test`, jsdom)

Tests inject `BROWSER_LANGUAGES`/`LANGUAGE_STORAGE` fakes; existing specs pin `['en']` and in-memory storage so English assertions stay valid. The "maps server validation errors" test now expects `household.nameInvalid` and asserts backend prose is **absent**.

1. **Resolution (`i18n.spec.ts`).** Stored `da` beats browser `en-US`. Invalid stored values (`'fr'`, `'DA '`, `'"da"'`, `''`) are ignored. `['fr-FR','da-DK','en']`→`da`, `['en-US','da']`→`en`, `['de']`→`en`, `[]`→`en`. Case and `_` are handled. A throwing `getItem` falls back to browser resolution. Startup never calls `setItem`.
2. **Persistence and failures.** `setLanguage('da')` writes `subscribed.language=da`. With a throwing `setItem` or `null` storage, the language still switches, `persistFailed` becomes true, and a dismissible `role=status` notice renders. A later successful write clears the flag.
3. **Interpolation and fallback.** `{name}` is substituted. Values containing `{x}`, `<b>` or `æøå` render literally. A missing parameter never shows a raw `{name}`. A key removed from a `Partial` `da` test catalog falls back to English (never the key, never blank).
4. **Catalog validation (`catalogs.spec.ts`)**
   - `Object.keys(en)` and `Object.keys(da)` are equal (also enforced by tsc).
   - All values are non-empty and trimmed.
   - Placeholder sets match per key.
   - `da[k] !== en[k]` unless `k` is allowlisted.
5. **Runtime switching (`app.spec.ts`)**
   - The selector has the accessible name "Language"/"Sprog" and the value matches the active language.
   - It renders in the loading, anonymous and member states.
   - Changing it to `da` updates the visible text, `aria-label`s and `document.documentElement.lang`, with no HTTP request, no router navigation (`router.url` unchanged), and the session state unchanged.
   - An already-visible `logoutError` re-translates.
6. **Foundation page (`foundation-page.spec.ts`)**
   - Typed unsaved household name survives a switch.
   - Visible `problem`/`fieldError`/`copyMessage` re-translate.
   - Member date renders `Mar` in en and `mar.` in da and updates on switch.
   - Error mapping table §7, and backend prose is never rendered.
   - **Reference visibility (§7.1).** Three cases: 400 `validation-failed` + `errors.name`, 412, and 500. Each carries `traceId: '00-abc-01'`. Each case asserts that its own block (`#name-error`, `.notice` or `.error`) contains `code` with `textContent === '00-abc-01'`. Then switch to `da` and assert that the label is now `Fejlreference:` and the code text is identical.
   - A client-side invalid name shows no reference. A new `save()` removes the previous reference.
7. **Coverage sweep.** Render every route/session state (loading, unavailable, anonymous + auth error, not-member, member home, household with errors, not-found, signed-out `idp=0`) in `da`; text plus `aria-label`s contain no English catalog value (except allowlist and product name), and `æ`, `ø`, `å` render.

**Checks:** `npm --prefix frontend run test:ci`, `… run lint`, `… run build`. **Manual:** 320/360/900 px widths (member + Menu + sign-out pending, both languages), keyboard-only switching, screen-reader output for the selector.

## 10. Risks and mitigations

- **Impure pipe cost:** rare zoneless CD, O(1) lookup.
- **Strings slip back in:** coverage sweep (§9.7) and README workflow.
- **Live regions re-announce on switch:** acceptable; focus not moved.
- **Header overflow:** two-row wrap at ≤479px; manual 320/360 px check.
- **Storage throws:** `try/catch`, token may be `null`, notice shown.
- **Theme control (#1):** shares `.preferences` on row 2.
- **Danish wording:** §6 needs human approval; keys are stable.
- **CLDR drift:** assert date fragments.

## 11. Adding a language (README content)

1. Add `{ code, endonym, dateLocale }` to `SUPPORTED_LANGUAGES`.
2. Create `catalog.<code>.ts` typed `: Catalog`, and register it in `CATALOGS`.
3. Import and register its Angular locale data in `app.config.ts`.
4. Run the tests: catalog parity, placeholders and the coverage sweep.

Plurals (not needed now): add `Intl.PluralRules` in `formatMessage` or migrate to Transloco.

**README must document:** supported languages (English, Dansk); resolution order (§5.3); English runtime fallback; storage key `subscribed.language` (browser-local, explicit choice only, kept on sign-out); date locales; and the workflow above.

## 12. Acceptance criteria → verification

| Criterion | Verified by |
| --- | --- |
| All states en/da incl. a11y | §6, §9.5–9.7 |
| Resolution/fallback | §9.1 |
| Selector keyboard/mobile | §5, §9.5, manual |
| Switch keeps route/auth/input | §9.5–9.6 |
| `lang`, æøå | §9.5, §9.7 |
| Persistence/storage failure | §9.1–9.2 |
| Localized errors, IDs, API unchanged | §7, §9.6 |
| Catalog parity | tsc, §9.4, §9.7 |
| Tests/lint/build | §9 checks |

## Sources (fetched 2026-11-12)

- [A1] https://angular.dev/guide/i18n/merge: per-locale build/deploy, no runtime switching described.
- [A2] https://registry.npmjs.org/@jsverse/transloco/latest: `version: "8.4.0"`, `peerDependencies: {"rxjs":">=6.0.0","@angular/core":">=16.0.0"}`.
- [A3] https://registry.npmjs.org/@ngx-translate/core/latest: `version: "18.1.0"`, `peerDependencies: {"rxjs":">=7","@angular/core":">=18","@angular/common":">=18"}`.
- [A4] https://developer.mozilla.org/en-US/docs/Web/API/Navigator/languages
- [A5] https://developer.mozilla.org/en-US/docs/Web/API/Storage/setItem: `QuotaExceededError`.
- [A6] https://unpkg.com/@angular/common@21.2.26/locales/da.d.ts and …/locales/en-GB.d.ts: files present at the repo's pinned version.
- [A7] https://angular.dev/api/common/registerLocaleData
- [A8] https://angular.dev/api/common/DatePipe: `date:format:timezone:locale`, default `LOCALE_ID` en-US, extra locale data must be imported.

Repo pins `@angular/core 21.2.26`. All compatibility above is **declared**; nothing was runtime-tested in design.
