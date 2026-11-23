# MCP server

Subscribed can be driven by an AI assistant that speaks the [Model Context Protocol](https://modelcontextprotocol.io) (MCP),
for example Claude Code. A household member creates a **token** on the household page, gives it to the assistant, and the
assistant can then read and change the household's subscriptions through 20 tools. The assistant acts as that member.

- Endpoint: `https://<host>/mcp`, the Streamable HTTP transport, **stateless** (no session id, no server-to-client stream).
- Authentication: `Authorization: Bearer <token>`, nothing else. No cookie, no query string, **no OAuth**.
- Scope: subscriptions, price history, pause periods, payment options, the catalog and report totals. Households, members
  and tokens cannot be managed through MCP, and **no tool deletes a subscription**.
- Switch: the server setting `Mcp:Enabled` (environment variable `Mcp__Enabled`, default `true`), see [The `Mcp:Enabled` flag](#the-mcpenabled-flag).

## Create a token

1. Sign in and open the household page.
2. In the **MCP tokens** section, enter a name that says where the token will be used (1 to 64 characters, for example
   `Laptop Claude Code`) and choose **Create token**.
3. The token is shown **once**, together with the MCP server address and a ready-made `claude mcp add` command. Copy it
   before leaving the page. If you lose it, revoke it and create a new one.
4. A member can hold at most **20 active tokens**. Each row shows the name, when it was created and when it was last used
   (updated at most every five minutes). **Revoke** removes a token at once.

A token is 43 characters of base64url (256 random bits) and has no prefix.

## Connect a client

### Claude Code

```sh
claude mcp add --transport http subscribed https://<host>/mcp --header "Authorization: Bearer <token>"
```

`--transport http` selects Streamable HTTP and `--header` (short form `-H`) adds the bearer header to every request. Add
`--scope user` to make the server available in every project instead of only the current one. Run `claude mcp list` to see
whether it connects.

### MCP Inspector

```sh
npx @modelcontextprotocol/inspector
```

Choose the transport **Streamable HTTP**, enter `https://<host>/mcp` as the URL and add the header `Authorization` with
the value `Bearer <token>`. Then connect and open **Tools**.

### Raw HTTP

Every request is a JSON-RPC message sent with `POST`. The client must accept `application/json` and `text/event-stream`:

```sh
curl -sS https://<host>/mcp \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Responses are `200` with `Content-Type: text/event-stream`: one `event: message` whose `data:` line is the JSON-RPC
message. A notification (a message without `id`) is answered with `202`. `GET /mcp` and `DELETE /mcp` are `405` with
`Allow: POST` (after the token check, so they are `401` without a valid token). A request body larger than 64 KB is `413`.

## Tools

Ten tools read and ten write. Amounts are **decimal strings** in the subscription's own currency (for example `"129.00"`);
nothing is converted. Dates are `yyyy-MM-dd`; `cancelBy` is a local wall time `yyyy-MM-ddTHH:mm` in the household time zone.
The `period` argument of the read tools is `year-YYYY`, `last-12-months` or `next-12-months`; omit it for the current
calendar year. Every tool returns one JSON text block (camelCase, the same shapes as the web API).

### Read tools

| Tool | Purpose | Arguments |
| --- | --- | --- |
| `list_subscriptions` | Every subscription with current price, status and cost for the period, plus per-currency totals | `period?` |
| `get_subscription` | One subscription with price history, pause history and the period total; its `version` is the `expectedVersion` of every write | `id`, `period?` |
| `get_price_history` | The price entries of one subscription (the `priceHistory` part of `get_subscription`) | `id` |
| `get_pauses` | The pause periods of one subscription (the `pauseHistory` part of `get_subscription`) | `id` |
| `get_totals` | Per-currency total, monthly average and active count for a period | `period?` |
| `list_periods` | The report periods that make sense for this household | none |
| `list_payment_options` | Payment options; a removed option stays listed with `removed=true` | none |
| `get_payment_option_suggestions` | Suggested names for new payment options | none |
| `get_catalog` | The built-in catalog of well-known services a subscription can be linked to | none |
| `get_subscription_options` | The currencies and other choices a subscription can use | none |

### Write tools

| Tool | Purpose | Arguments |
| --- | --- | --- |
| `create_subscription` | Creates a subscription; **not idempotent** | `name`, `currency`, `startDate`, `amount`, `cadenceUnit`, `cadenceCount`; optional `category`, `notes`, `endDate`, `catalogId`, `unsubscribeUrl`, `paymentOptionId`, `cancelBy` |
| `update_subscription` | **Full replace** of the details, see below | `id`, `expectedVersion`, `name`, `currency`, `startDate`; optional `category`, `notes`, `endDate`, `catalogId`, `unsubscribeUrl`, `paymentOption`, `cancelBy` |
| `add_price_entry` | Adds a price change from a date | `id`, `expectedVersion`, `effectiveFrom`, `amount`, `cadenceUnit`, `cadenceCount`; optional `paymentOptionId` |
| `update_price_entry` | Replaces one price entry; an omitted `paymentOptionId` clears it | `id`, `expectedVersion`, `entryId`, `effectiveFrom`, `amount`, `cadenceUnit`, `cadenceCount`; optional `paymentOptionId` |
| `delete_price_entry` | Deletes one price entry (not the initial one) | `id`, `expectedVersion`, `entryId` |
| `pause_subscription` | Pauses from today; fails with `invalid-subscription-state` if already paused | `id`, `expectedVersion` |
| `resume_subscription` | Resumes from today; fails with `invalid-subscription-state` if not paused | `id`, `expectedVersion` |
| `add_pause_period` | Adds a dated pause period; periods must not overlap | `id`, `expectedVersion`, `pauseDate`; optional `resumeDate` (omit for an open pause) |
| `update_pause_period` | Replaces both dates of one pause period | `id`, `expectedVersion`, `periodId`, `pauseDate`; optional `resumeDate` |
| `delete_pause_period` | Deletes one pause period | `id`, `expectedVersion`, `periodId` |

`entryId` comes from `get_price_history` and `periodId` from `get_pauses`. Every write returns the updated subscription,
including its new `version`.

### `update_subscription` replaces everything

`update_subscription` is a **full replace**, not a patch. Call `get_subscription` first and send **every field you want to
keep**. Any optional field you leave out is **cleared**: `category`, `notes`, `endDate`, `catalogId`, `unsubscribeUrl` and
`cancelBy`. A null or missing `endDate` also **reinstates a cancelled subscription**. `name`, `currency` and `startDate`
are required.

`paymentOption` is the one opt-in field: omit it or send `null` to leave the price entries' payment options alone, send
`{ "id": null }` to clear the payment option of every price entry, or send `{ "id": "<payment option id>" }` to set one.
`{}` is rejected. Use the narrower tools (`add_price_entry`, `pause_subscription`, ...) when you only mean one change.

### Concurrency: the `expectedVersion` retry loop

Every write that changes an existing subscription takes `expectedVersion`, the `version` from `get_subscription` read right
before the change. If the subscription changed in between (in the web UI, or by another agent), the call fails with
`concurrency-conflict` and `currentVersion`. The loop for an agent is:

1. `get_subscription` and note `version`.
2. Call the write tool with `expectedVersion` set to that version.
3. On `concurrency-conflict` (or `invalid-subscription-state`), read the subscription again, decide whether your change
   still makes sense on the new state, and repeat from step 2 with the new version. Do not just resend the old arguments
   with `currentVersion` after a full replace: the data may have changed.

`create_subscription` has no version and is **not idempotent**: calling it twice creates two subscriptions. After a failure
or a timeout, call `list_subscriptions` to see whether it was created **before** you retry.

### Errors

A domain error is a tool result with `isError: true` and one JSON text block `{ code, title, errors?, currentVersion?, traceId? }`.
The codes are the web API's problem codes:

| Code | Meaning |
| --- | --- |
| `validation-failed` | An argument is wrong; `errors` maps each field name to its messages. A missing or mistyped argument is reported here too |
| `not-found` | The subscription, price entry or pause period does not exist (a malformed id is `not-found` as well) |
| `concurrency-conflict` | `expectedVersion` is stale; `currentVersion` is the current one |
| `invalid-subscription-state` | The change does not fit the subscription's state (for example pausing a paused subscription); `currentVersion` is included |
| `internal-error` | An unexpected failure, with a fixed title and a `traceId`; the details are in the server log. It does **not** say whether a write was applied, so re-read before retrying |

Authentication problems are not tool results: they are HTTP `401` with a `problem+json` body (see below).

### What "via MCP" means

On the subscription page, a price entry or a pause period shows a **via MCP** badge when the **last change made to that
row** went through MCP. It is the last change, not the creation: if someone later edits the row in the web UI, the badge
disappears. The audit trail records which token made a change by its id, never its secret.

### The `Mcp:Enabled` flag

`Mcp:Enabled` defaults to `true`. With `false` the server does not map `/mcp` (every method on `/mcp` is a `404`) and
`POST /api/household/mcp-tokens` answers `409` with the code `mcp-disabled`. Listing and revoking tokens still work, so a
member can still revoke. The flag stops new exposure; it does **not** undo events that were already written, see
[deploy/README.md](../deploy/README.md#mcp-rollout-is-one-way-once-a-token-exists).

## Security model

- **A bearer credential.** The token is shown once and the server stores only its **SHA-256 hash** (the household event
  stream and the read model hold the hash and a public token id, never the token). A lost token cannot be recovered, only
  replaced.
- **It acts as the member.** A request with a token has the permissions of the member who created it in the web UI, except
  that it **cannot delete subscriptions and cannot manage tokens** (the token endpoints reject an MCP token). A token never
  exceeds its member: anyone who holds it can read and change the household's subscriptions until it is revoked.
- **Revocation is immediate.** Revoking a token, and removing the member from the household, take effect on the **next
  request**: the server checks the token against the current household state on every call.
- **One answer for every failure.** A missing header, a malformed token, an unknown or revoked token and a token of a removed
  member are all the same `401` `problem+json` response with `WWW-Authenticate: Bearer`, so a caller learns nothing about
  which part failed. The token is compared in constant time against every stored token, and a database outage is `503`,
  never a `401`.
- **Tokens never appear in logs.** The server's MCP log lines carry only the tool name, the outcome, an error code, the token
  **id** and a correlation id: no arguments, no results, no `Authorization` header. The MCP library's own exception logging is
  silenced. Your reverse proxy must not log the `Authorization` header either, see [deploy/README.md](../deploy/README.md#reverse-proxy-settings-for-mcp).
- **Bearer only, no cookies.** `/mcp` rejects a session cookie, so a browser that is signed in cannot be used to call it.
  A cross-site request cannot attach the token, so cross-site request forgery and DNS rebinding against `/mcp` are low
  risk. The MCP library **does not validate the `Origin` header** (checked), and there is no CORS policy; this is acceptable
  because the token, not the browser's ambient credentials, is what authorizes a request.
- **No OAuth.** The server does not offer OAuth discovery or dynamic client registration. The well-known OAuth paths answer a
  plain `404` so a client does not mistake the web app for OAuth metadata.
- **Rotate by replacing.** Create a new token, switch your client to it, then revoke the old one.
- **Treat a token like a password.** A token in your shell history (the `claude mcp add` command) or in a client's
  configuration file (for example `.mcp.json` or `~/.claude.json`) can be read by anything that reads that file: do not commit
  it, do not paste it into chat, and prefer a user-scope configuration over a project file in a shared repository.
- **Request limits.** A request body is capped at 64 KB. The server does not rate limit failed authentication; that belongs to
  the reverse proxy.
