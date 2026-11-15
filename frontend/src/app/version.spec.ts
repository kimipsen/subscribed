import { version as pkgVersion } from '../../package.json';
import { APP_VERSION, resolveVersion } from './version';

describe('resolveVersion', () => {
  it('returns a defined value', () => expect(resolveVersion('1.2.3', 'fb')).toBe('1.2.3'));
  it('trims a padded value', () => expect(resolveVersion('  1.2.3 ', 'fb')).toBe('1.2.3'));
  it('falls back for an empty string', () => expect(resolveVersion('', 'fb')).toBe('fb'));
  it('falls back for a whitespace-only string', () => expect(resolveVersion('   ', 'fb')).toBe('fb'));
  it('falls back for undefined', () => expect(resolveVersion(undefined, 'fb')).toBe('fb'));
  it.each([42, null, {}, true])('falls back for the non-string %j', (value) =>
    expect(resolveVersion(value, 'fb')).toBe('fb'));
});

describe('APP_VERSION', () => {
  it('is the package.json version when nothing is defined', () => {
    expect(APP_VERSION).toBe(pkgVersion);
    expect(APP_VERSION).not.toBe('');
    expect(APP_VERSION).not.toBe('__APP_VERSION__');
  });
});
