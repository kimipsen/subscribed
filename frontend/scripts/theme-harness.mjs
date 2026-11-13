// Shared fake-global harness for public/theme-init.js. node:vm is used only to
// provide fake globals, not as a security boundary.
import vm from 'node:vm';

export const THROWS = Symbol('throws');

/**
 * Runs the init script once.
 * @param {string} source script text
 * @param {{ stored?: string | null | typeof THROWS, systemDark?: boolean | typeof THROWS | undefined, noMatchMedia?: boolean }} options
 */
export function runInit(source, { stored = null, systemDark = false, noMatchMedia = false } = {}) {
  const calls = [];
  const localStorage = {
    getItem(key) {
      if (stored === THROWS) throw new Error('storage denied');
      return key === 'subscribed.theme' ? stored : null;
    },
    setItem() {
      throw new Error('theme-init.js must never write storage');
    },
  };
  const window = { localStorage };
  if (!noMatchMedia) {
    window.matchMedia = (query) => {
      if (systemDark === THROWS) throw new Error('matchMedia failed');
      return { matches: query === '(prefers-color-scheme: dark)' && systemDark === true };
    };
  }
  const document = {
    documentElement: {
      setAttribute(name, value) { calls.push([name, value]); },
      style: new Proxy({}, { set() { throw new Error('theme-init.js must not write inline style'); } }),
    },
  };
  vm.runInNewContext(source, { window, document });
  return calls;
}
