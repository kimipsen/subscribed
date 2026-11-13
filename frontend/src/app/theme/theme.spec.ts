import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import {
  COLOR_SCHEME_QUERY,
  THEME_STORAGE,
  THEME_STORAGE_KEY,
  ThemeMode,
  ThemeService,
  isThemeMode,
  resolveTheme,
} from './theme';

type Listener = (event: MediaQueryListEvent) => void;

class FakeQuery {
  readonly listeners = new Set<Listener>();
  constructor(public matches: boolean) {}
  addEventListener(type: string, listener: Listener) {
    if (type === 'change') this.listeners.add(listener);
  }
  removeEventListener(type: string, listener: Listener) {
    if (type === 'change') this.listeners.delete(listener);
  }
  fire(matches: boolean) {
    this.matches = matches;
    for (const listener of this.listeners) listener({ matches } as MediaQueryListEvent);
  }
}

class FakeStorage {
  readonly values = new Map<string, string>();
  readonly getItem = vi.fn((key: string) => this.values.get(key) ?? null);
  readonly setItem = vi.fn((key: string, value: string) => void this.values.set(key, value));
}

describe('ThemeService', () => {
  let storage: FakeStorage;
  let query: FakeQuery;

  beforeEach(() => {
    storage = new FakeStorage();
    query = new FakeQuery(false);
  });

  afterEach(() => document.documentElement.removeAttribute('data-theme'));

  function create(
    store: Pick<Storage, 'getItem' | 'setItem'> | null = storage,
    media: FakeQuery | null = query,
  ): ThemeService {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        { provide: THEME_STORAGE, useValue: store },
        { provide: COLOR_SCHEME_QUERY, useValue: media },
      ],
    });
    const service = TestBed.inject(ThemeService);
    TestBed.tick();
    return service;
  }

  const attribute = () => document.documentElement.getAttribute('data-theme');

  it('defaults to system, follows the system scheme and writes nothing', () => {
    query = new FakeQuery(true);
    const service = create();
    expect(service.mode()).toBe('system');
    expect(service.theme()).toBe('dark');
    expect(attribute()).toBe('dark');
    expect(storage.setItem).not.toHaveBeenCalled();
    expect(service.persistFailed()).toBe(false);
  });

  it('falls back to light without a color-scheme query', () => {
    const service = create(storage, null);
    expect(service.systemDark()).toBe(false);
    expect(service.theme()).toBe('light');
    expect(attribute()).toBe('light');
  });

  it('follows live system changes in system mode', () => {
    const service = create();
    query.fire(true);
    TestBed.tick();
    expect(service.theme()).toBe('dark');
    expect(attribute()).toBe('dark');
    query.fire(false);
    TestBed.tick();
    expect(service.theme()).toBe('light');
    expect(attribute()).toBe('light');
  });

  it('lets explicit light and dark override the system until system is chosen again', () => {
    const service = create();
    service.setMode('dark');
    TestBed.tick();
    expect(attribute()).toBe('dark');
    query.fire(false);
    TestBed.tick();
    expect(service.theme()).toBe('dark');

    service.setMode('light');
    query.fire(true);
    TestBed.tick();
    expect(service.theme()).toBe('light');
    expect(attribute()).toBe('light');

    service.setMode('system');
    TestBed.tick();
    expect(service.theme()).toBe('dark');
    query.fire(false);
    TestBed.tick();
    expect(attribute()).toBe('light');
  });

  it.each<ThemeMode>(['system', 'light', 'dark'])('persists %s and restores it in a new instance', (mode) => {
    create().setMode(mode);
    expect(storage.setItem).toHaveBeenCalledWith(THEME_STORAGE_KEY, mode);
    const restored = create();
    expect(restored.mode()).toBe(mode);
  });

  it.each(['Dark', 'auto', '', 'null', '{"a":1}'])('ignores the invalid stored value %j', (value) => {
    storage.values.set(THEME_STORAGE_KEY, value);
    query = new FakeQuery(true);
    const service = create();
    expect(service.mode()).toBe('system');
    expect(service.theme()).toBe('dark');
    expect(storage.setItem).not.toHaveBeenCalled();
    expect(storage.values.get(THEME_STORAGE_KEY)).toBe(value);
    expect(service.persistFailed()).toBe(false);
  });

  it('reports unavailable storage but still applies choices', () => {
    const service = create(null);
    expect(service.mode()).toBe('system');
    expect(service.persistFailed()).toBe(true);
    service.dismissPersistNotice();
    expect(service.persistFailed()).toBe(false);
    service.setMode('dark');
    TestBed.tick();
    expect(attribute()).toBe('dark');
    expect(service.persistFailed()).toBe(true);
  });

  it('reports a throwing getItem and recovers after a successful write', () => {
    storage.getItem.mockImplementation(() => { throw new DOMException('Denied', 'SecurityError'); });
    const service = create();
    expect(service.mode()).toBe('system');
    expect(service.persistFailed()).toBe(true);
    service.setMode('light');
    expect(service.persistFailed()).toBe(false);
    expect(storage.values.get(THEME_STORAGE_KEY)).toBe('light');
  });

  it('reports a throwing setItem, applies the choice and can be dismissed', () => {
    storage.setItem.mockImplementation(() => { throw new DOMException('Full', 'QuotaExceededError'); });
    const service = create();
    service.setMode('dark');
    TestBed.tick();
    expect(service.mode()).toBe('dark');
    expect(attribute()).toBe('dark');
    expect(service.persistFailed()).toBe(true);
    service.dismissPersistNotice();
    expect(service.persistFailed()).toBe(false);
  });

  it('removes the system listener when its injector is destroyed', () => {
    create();
    expect(query.listeners.size).toBe(1);
    TestBed.resetTestingModule();
    expect(query.listeners.size).toBe(0);
  });
});

describe('theme helpers', () => {
  it('recognises only the exact theme modes', () => {
    for (const value of ['system', 'light', 'dark']) expect(isThemeMode(value)).toBe(true);
    for (const value of ['Dark', 'auto', '', 'null', null, undefined, 1, {}]) expect(isThemeMode(value)).toBe(false);
  });

  it('resolves every mode and system combination', () => {
    expect(resolveTheme('system', false)).toBe('light');
    expect(resolveTheme('system', true)).toBe('dark');
    expect(resolveTheme('light', true)).toBe('light');
    expect(resolveTheme('light', false)).toBe('light');
    expect(resolveTheme('dark', false)).toBe('dark');
    expect(resolveTheme('dark', true)).toBe('dark');
  });
});
