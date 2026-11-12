import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, computed, effect, inject, isDevMode, signal } from '@angular/core';
import { da } from './catalog.da';
import { en } from './catalog.en';
import { DEFAULT_LANGUAGE, Language, SUPPORTED_LANGUAGES, isLanguage } from './languages';

export type MessageKey = keyof typeof en;
export type Catalog = Readonly<Record<MessageKey, string>>;
export type MessageParams = Readonly<Record<string, string>>;
export interface Message {
  key: MessageKey;
  params?: MessageParams;
}

const STORAGE_KEY = 'subscribed.language';
const CATALOGS: Readonly<Record<Language, Partial<Catalog>>> = { en, da };

export const LANGUAGE_STORAGE = new InjectionToken<Pick<Storage, 'getItem' | 'setItem'> | null>(
  'language preference storage',
  {
    providedIn: 'root',
    factory: () => {
      try {
        return inject(DOCUMENT).defaultView?.localStorage ?? null;
      } catch {
        return null;
      }
    },
  },
);

export const BROWSER_LANGUAGES = new InjectionToken<readonly string[]>('browser languages', {
  providedIn: 'root',
  factory: () => {
    const browser = inject(DOCUMENT).defaultView?.navigator;
    return browser?.languages.length ? browser.languages : browser?.language ? [browser.language] : [];
  },
});

export function resolveLanguage(stored: string | null, browserLanguages: readonly string[]): Language {
  if (isLanguage(stored)) return stored;
  for (const tag of browserLanguages) {
    const language = tag.toLowerCase().split(/[-_]/)[0];
    if (isLanguage(language)) return language;
  }
  return DEFAULT_LANGUAGE;
}

export function formatMessage(
  template: string,
  params: MessageParams = {},
  missing: (name: string) => void = () => undefined,
): string {
  return template.replace(/\{([A-Za-z][A-Za-z0-9]*)\}/g, (_, name: string) => {
    if (Object.hasOwn(params, name)) return params[name];
    missing(name);
    return '';
  });
}

@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly storage = inject(LANGUAGE_STORAGE);
  private readonly document = inject(DOCUMENT);
  private readonly selected = signal<Language>(DEFAULT_LANGUAGE);
  private readonly persistenceError = signal(false);
  private readonly warnings = new Set<string>();
  readonly language = this.selected.asReadonly();
  readonly dateLocale = computed(() =>
    SUPPORTED_LANGUAGES.find((language) => language.code === this.language())?.dateLocale ?? 'en-GB');
  readonly persistFailed = this.persistenceError.asReadonly();

  constructor() {
    let stored: string | null = null;
    try {
      stored = this.storage?.getItem(STORAGE_KEY) ?? null;
      this.persistenceError.set(this.storage === null);
    } catch {
      this.persistenceError.set(true);
    }
    this.selected.set(resolveLanguage(stored, inject(BROWSER_LANGUAGES)));
    effect(() => this.document.documentElement.lang = this.language());
  }

  setLanguage(language: Language): void {
    this.selected.set(language);
    if (!this.storage) {
      this.persistenceError.set(true);
      return;
    }
    try {
      this.storage.setItem(STORAGE_KEY, language);
      this.persistenceError.set(false);
    } catch {
      this.persistenceError.set(true);
    }
  }

  dismissPersistNotice(): void {
    this.persistenceError.set(false);
  }

  translate(key: MessageKey, params?: MessageParams): string {
    const language = this.language();
    const translated = CATALOGS[language][key];
    if (!translated) this.warn(`${language}:${key}`, `Missing ${language} translation: ${key}`);
    return formatMessage(translated || en[key], params, (name) =>
      this.warn(`${language}:${key}:${name}`, `Missing translation parameter: ${key}.${name}`));
  }

  message(message: Message | null | undefined): string {
    return message ? this.translate(message.key, message.params) : '';
  }

  private warn(id: string, text: string): void {
    if (isDevMode() && !this.warnings.has(id)) {
      this.warnings.add(id);
      console.warn(text);
    }
  }
}
