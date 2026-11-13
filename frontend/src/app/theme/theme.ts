import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, InjectionToken, computed, effect, inject, signal } from '@angular/core';

// Keep isThemeMode/resolveTheme in sync with public/theme-init.js (tested by scripts/theme.test.mjs).
export const THEME_MODES = ['system', 'light', 'dark'] as const;
export type ThemeMode = (typeof THEME_MODES)[number];
export type Theme = 'light' | 'dark';
export const THEME_STORAGE_KEY = 'subscribed.theme';
export const DEFAULT_THEME_MODE: ThemeMode = 'system';

export function isThemeMode(value: unknown): value is ThemeMode {
  return typeof value === 'string' && (THEME_MODES as readonly string[]).includes(value);
}

export function resolveTheme(mode: ThemeMode, systemDark: boolean): Theme {
  if (mode === 'system') return systemDark ? 'dark' : 'light';
  return mode;
}

export const THEME_STORAGE = new InjectionToken<Pick<Storage, 'getItem' | 'setItem'> | null>(
  'theme preference storage',
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

export const COLOR_SCHEME_QUERY = new InjectionToken<MediaQueryList | null>('dark color scheme query', {
  providedIn: 'root',
  factory: () => {
    try {
      return inject(DOCUMENT).defaultView?.matchMedia?.('(prefers-color-scheme: dark)') ?? null;
    } catch {
      return null;
    }
  },
});

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly storage = inject(THEME_STORAGE);
  private readonly document = inject(DOCUMENT);
  private readonly selected = signal<ThemeMode>(DEFAULT_THEME_MODE);
  private readonly systemPrefersDark = signal(false);
  private readonly persistenceError = signal(false);
  readonly mode = this.selected.asReadonly();
  readonly systemDark = this.systemPrefersDark.asReadonly();
  readonly theme = computed(() => resolveTheme(this.mode(), this.systemDark()));
  readonly persistFailed = this.persistenceError.asReadonly();

  constructor() {
    try {
      const stored = this.storage?.getItem(THEME_STORAGE_KEY) ?? null;
      if (isThemeMode(stored)) this.selected.set(stored);
      this.persistenceError.set(this.storage === null);
    } catch {
      this.persistenceError.set(true);
    }

    const query = inject(COLOR_SCHEME_QUERY);
    if (query) {
      this.systemPrefersDark.set(query.matches);
      const listener = (event: MediaQueryListEvent) => this.systemPrefersDark.set(event.matches);
      query.addEventListener('change', listener);
      inject(DestroyRef).onDestroy(() => query.removeEventListener('change', listener));
    }

    effect(() => this.document.documentElement.setAttribute('data-theme', this.theme()));
  }

  setMode(mode: ThemeMode): void {
    this.selected.set(mode);
    if (!this.storage) {
      this.persistenceError.set(true);
      return;
    }
    try {
      this.storage.setItem(THEME_STORAGE_KEY, mode);
      this.persistenceError.set(false);
    } catch {
      this.persistenceError.set(true);
    }
  }

  dismissPersistNotice(): void {
    this.persistenceError.set(false);
  }
}
