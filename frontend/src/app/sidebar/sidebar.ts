import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, inject, signal } from '@angular/core';

export const SIDEBAR_STORAGE_KEY = 'subscribed.sidebar';

export const SIDEBAR_STORAGE = new InjectionToken<Pick<Storage, 'getItem' | 'setItem'> | null>(
  'sidebar state storage',
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

/**
 * Whether the sidebar is collapsed to its icons-only rail.
 *
 * Unlike ThemeService and I18n this shows no notice when storage is unavailable: the toggle is a
 * cosmetic preference, so a failed read or write is swallowed and the state still flips for the
 * current session. Storage is read once on construction and written only after a user toggle.
 */
@Injectable({ providedIn: 'root' })
export class SidebarService {
  private readonly storage = inject(SIDEBAR_STORAGE);
  private readonly state = signal(this.restore());
  readonly collapsed = this.state.asReadonly();

  toggle(): void {
    const next = !this.state();
    this.state.set(next);
    try {
      this.storage?.setItem(SIDEBAR_STORAGE_KEY, next ? 'collapsed' : 'expanded');
    } catch {
      // Storage may be full or blocked; the in-memory state is still correct.
    }
  }

  private restore(): boolean {
    try {
      return this.storage?.getItem(SIDEBAR_STORAGE_KEY) === 'collapsed';
    } catch {
      return false;
    }
  }
}
