import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { da } from './catalog.da';
import { en } from './catalog.en';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService, MessageKey, formatMessage, resolveLanguage } from './i18n';

describe('Language preferences', () => {
  const cases = [
    { stored: 'da', browser: ['en-US'], expected: 'da' },
    { stored: 'en', browser: ['da-DK'], expected: 'en' },
    { stored: 'fr', browser: ['fr-FR', 'da-DK', 'en'], expected: 'da' },
    { stored: 'DA ', browser: ['en-US', 'da'], expected: 'en' },
    { stored: '"da"', browser: ['DA_dk'], expected: 'da' },
    { stored: '', browser: ['EN'], expected: 'en' },
    { stored: null, browser: ['de'], expected: 'en' },
    { stored: null, browser: [], expected: 'en' },
  ];

  for (const { stored, browser, expected } of cases) {
    it(`resolves ${stored} with ${browser.join(',')} to ${expected}`, () => {
      expect(resolveLanguage(stored, browser)).toBe(expected);
    });
  }

  function service(storage: Pick<Storage, 'getItem' | 'setItem'> | null) {
    TestBed.configureTestingModule({
      providers: [
        { provide: BROWSER_LANGUAGES, useValue: ['da-DK'] },
        { provide: LANGUAGE_STORAGE, useValue: storage },
      ],
    });
    return TestBed.inject(LanguageService);
  }

  it('persists only explicit choices and restores them on a new visit', () => {
    let stored: string | null = null;
    const storage = {
      getItem: vi.fn(() => stored),
      setItem: vi.fn((key: string, value: string) => { stored = value; }),
    };
    const i18n = service(storage);
    expect(i18n.language()).toBe('da');
    expect(storage.setItem).not.toHaveBeenCalled();
    i18n.setLanguage('en');
    expect(storage.setItem).toHaveBeenCalledWith('subscribed.language', 'en');
    TestBed.resetTestingModule();
    expect(service(storage).language()).toBe('en');
  });

  it('uses browser preferences and reports a failed storage read', () => {
    const i18n = service({
      getItem: () => { throw new DOMException('Blocked', 'SecurityError'); },
      setItem: vi.fn(),
    });
    expect(i18n.language()).toBe('da');
    expect(i18n.persistFailed()).toBe(true);
  });

  it('works without storage and allows dismissing the notice', () => {
    const i18n = service(null);
    i18n.setLanguage('en');
    expect(i18n.language()).toBe('en');
    expect(i18n.persistFailed()).toBe(true);
    i18n.dismissPersistNotice();
    expect(i18n.persistFailed()).toBe(false);
    i18n.setLanguage('da');
    expect(i18n.persistFailed()).toBe(true);
  });

  it('retains failed writes in memory and reports successful retries', () => {
    const setItem = vi.fn().mockImplementationOnce(() => { throw new DOMException('Full', 'QuotaExceededError'); });
    const i18n = service({ getItem: () => null, setItem });
    i18n.setLanguage('en');
    expect(i18n.language()).toBe('en');
    expect(i18n.persistFailed()).toBe(true);
    i18n.setLanguage('da');
    expect(i18n.persistFailed()).toBe(false);
  });

  it('falls back to English for missing/empty entries and warns once', () => {
    const i18n = service(null);
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const original = da['home.ready'];
    try {
      Reflect.deleteProperty(da, 'home.ready');
      expect(i18n.translate('home.ready')).toBe(en['home.ready']);
      Reflect.set(da, 'home.ready', '');
      expect(i18n.translate('home.ready')).toBe(en['home.ready']);
      expect(warn).toHaveBeenCalledTimes(1);
    } finally {
      Reflect.set(da, 'home.ready', original);
      warn.mockRestore();
    }
  });

  it('interpolates text once and reports missing parameters', () => {
    expect(formatMessage('Welcome, {name}', { name: '<b>Åse {date}</b>' }))
      .toBe('Welcome, <b>Åse {date}</b>');
    const missing = vi.fn();
    expect(formatMessage('{name} {date}', { name: 'Ørn' }, missing)).toBe('Ørn ');
    expect(missing).toHaveBeenCalledWith('date');
  });
});

describe('Translation catalogs', () => {
  it('keeps keys and parameters aligned, with complete non-empty translations', () => {
    expect(Object.keys(da).sort()).toEqual(Object.keys(en).sort());
    const placeholders = (value: string) => [...value.matchAll(/\{([A-Za-z][A-Za-z0-9]*)\}/g)]
      .map((match) => match[1]).sort();
    for (const key of Object.keys(en) as MessageKey[]) {
      expect(da[key].trim(), key).toBe(da[key]);
      expect(da[key].length, key).toBeGreaterThan(0);
      expect(en[key].trim(), key).toBe(en[key]);
      expect(placeholders(da[key]), key).toEqual(placeholders(en[key]));
      if (key !== 'shell.menu') expect(da[key], key).not.toBe(en[key]);
    }
  });
});
