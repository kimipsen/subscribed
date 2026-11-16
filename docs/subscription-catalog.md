# Subscription catalog maintenance

The catalog of known services lives in `backend/src/Subscribed.Subscriptions/Catalog/catalog.json`. It is an embedded
resource: it is validated once at startup (a bad file stops the app from starting), and a change ships as a normal release.
Nothing is fetched automatically, and catalog data never overwrites household data.

## Adding or updating an entry

1. Open the provider's **official** pricing page for the market (for example the Danish page for `DK`).
2. Copy the price exactly as shown. Never use prices from memory, a review site or an app store listing.
3. Edit `catalog.json`:
   - `id`: lowercase kebab-case, at most 64 characters, unique, and **stable**: households store it.
   - `name`, `category`: trimmed text (80 and 40 characters at most).
   - `logo`: `null`, or `/logos/<name>.svg|png|webp` for a file under `frontend/public/logos/` that has a row in
     `frontend/public/logos/LICENSES.md`. Ship no brand logo without a confirmed licence.
   - `lastVerified`: the date you checked the price (`yyyy-MM-dd`, not in the future).
   - `sourceUrl`: the official page you used (absolute https URL). It is not exposed by the API.
   - `unsubscribeUrl` (optional): the provider's official cancel or manage-subscription page (absolute `https://` URL, trimmed,
     at most 2048 characters, no spaces, backslashes or user-info). It is exposed by the API and shown as the "Unsubscribe"
     link for subscriptions linked to this service. Omit the field when you cannot confirm an official page: never guess one.
   - `plans`: one object per plan with `country` (two capitals), `name`, `currency` (ISO 4217), `amount` (a string such
     as `"129.00"`, positive, at most the currency's decimals), `cadenceUnit` (`week`, `month` or `year`) and
     `cadenceCount` (1 to 99). Omit any plan you cannot verify.
   - `upcomingChange` (optional): `{ "amount": "139.00", "effectiveFrom": "2027-02-01" }` for an announced price change.
4. Run `dotnet test backend/Subscribed.slnx`. The loader tests and the shipped-catalog test fail with a message that names
   the service id and the field.

## What counts as verification

A price is verified when you read it on the provider's official pricing page for that market on the `lastVerified` date. If
fewer than three services can be verified, do not invent data: stop and report. That stop-and-report rule applies to
**prices only**.

An `unsubscribeUrl` is verified when you open the official cancel or manage-subscription page on the day and record the
service and the date in the PR description. Zero populated URLs is allowed: omit the field for any service you cannot confirm.

## Rules for pull requests

- A PR that changes a price must update `lastVerified` and cite the source URL in its description.
- Reviewers check each changed `sourceUrl` and `lastVerified` by hand: no test can verify a price. They also check each
  `unsubscribeUrl` by hand: the loader only enforces the https shape, not that the page still exists.
- Cancel pages move. `unsubscribeUrl` is not tied to `lastVerified`, so re-check the URLs at the same review cadence.
- Refresh `lastVerified` on a regular review cadence (for example every quarter), even when nothing changed.

## Things to know

- **File order is significant.** Services and plans keep their order in the API response. The add form picks the default plan
  by that order (first plan in the household currency, else the first `DK` plan, else the first plan).
- **A past `upcomingChange.effectiveFrom` is valid** (stale data is not a load error), but the add form hides it. Remove it once
  the new price is the regular price.
- **Removing an entry is safe.** Subscriptions that link to a removed id keep working and show the initial-letter fallback;
  the API logs a warning that names the subscription id and the catalog id.
- `lastVerified` may be at most one day ahead of UTC today, so a Danish local date cannot crash startup.
