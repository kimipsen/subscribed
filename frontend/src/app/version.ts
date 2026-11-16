import { version as pkgVersion } from '../../package.json';

declare const __APP_VERSION__: string | undefined;

/** Pure so the "defined" branch is unit-testable without a global define. */
export function resolveVersion(defined: unknown, fallback: string): string {
  return (typeof defined === 'string' && defined.trim()) || fallback;
}

/** Build version injected with `--define`, else the package.json version. */
export const APP_VERSION: string = resolveVersion(
  typeof __APP_VERSION__ === 'undefined' ? undefined : __APP_VERSION__,
  pkgVersion,
);
