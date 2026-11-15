// Pure argument construction for the production build wrapper. No side effects on import.
import { readFileSync } from 'node:fs';

const ALLOWED = /^[0-9A-Za-z._+-]+$/;

/** Trim, strip one leading `v`, and return '' for unset or empty input. */
export function normalizeVersion(raw) {
  if (typeof raw !== 'string') return '';
  const trimmed = raw.trim();
  return trimmed.startsWith('v') ? trimmed.slice(1) : trimmed;
}

/** The package.json version, used as the fallback when APP_VERSION is unset. */
export function readPackageVersion() {
  const url = new URL('../package.json', import.meta.url);
  return JSON.parse(readFileSync(url, 'utf8')).version;
}

/** Arguments for `ng`. A `--define` is always present so the placeholder never reaches dist. */
export function buildArgs(env, pkgVersion) {
  const value = normalizeVersion(env.APP_VERSION) || pkgVersion;
  if (!ALLOWED.test(value)) {
    throw new Error(`Invalid APP_VERSION ${JSON.stringify(value)}: allowed characters are [0-9A-Za-z._+-]`);
  }
  return ['build', '--configuration', 'production', '--define', `__APP_VERSION__=${JSON.stringify(value)}`];
}
