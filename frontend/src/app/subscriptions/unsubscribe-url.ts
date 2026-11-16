/** The most characters an unsubscribe link may have, after trimming: the server's `SubscriptionText.MaxUnsubscribeUrl`. */
export const MAX_UNSUBSCRIBE_URL = 2048;

const SCHEME = 'https://';

/** C0 and C1 control characters and DEL, like the server's `char.IsControl`. */
const hasControl = (value: string): boolean =>
  Array.from(value).some((character) => {
    const code = character.codePointAt(0) ?? 0;
    return code <= 0x1f || (code >= 0x7f && code <= 0x9f);
  });

/** True when the value starts with `https://`, compared case-insensitively: the server stores the value as typed, so `HTTPS://x` can arrive. */
export function isHttpsUrl(value: string | null | undefined): value is string {
  return typeof value === 'string' && value.slice(0, SCHEME.length).toLowerCase() === SCHEME;
}

/**
 * Mirrors the server's one rule (`SubscriptionText.TryNormalizeUnsubscribeUrl`): trim; empty means none (null); at most 2048
 * characters; no white space or control characters inside; an https:// prefix (any case); no backslash anywhere and no `@` in the
 * authority; an absolute URL with a host and no user-info. Returns `undefined` when invalid, so `null` stays "none".
 */
export function normalizeUnsubscribeUrl(raw: string): string | null | undefined {
  const value = raw.trim();
  if (value.length === 0) return null;
  if (value.length > MAX_UNSUBSCRIBE_URL || /\s/u.test(value) || hasControl(value) || !isHttpsUrl(value) || value.includes('\\')) {
    return undefined;
  }
  const rest = value.slice(SCHEME.length);
  const authority = rest.slice(0, rest.search(/[/?#]|$/));
  // An empty authority (`https:///x`) is rejected here: the WHATWG parser would take `x` as the host, the server rejects it.
  if (authority.length === 0 || authority.includes('@')) return undefined;
  try {
    const url = new URL(value);
    return url.hostname.length > 0 && url.username === '' && url.password === '' ? value : undefined;
  } catch {
    return undefined;
  }
}
