# Subscribed frontend

Angular standalone application for the foundation milestone: OIDC session
states, shared household settings, membership visibility and optimistic rename.
Subscription management is intentionally not implemented.

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
