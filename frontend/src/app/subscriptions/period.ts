import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { Message } from '../i18n/i18n';

// The report period the overview and the detail page total over. The choice is shared by both pages and remembered per browser.

export type Period = { kind: 'year'; year: number } | { kind: 'last-12-months' } | { kind: 'next-12-months' };

export const PERIOD_STORAGE_KEY = 'subscribed.period';

/** The calendar years the API accepts (it rejects anything outside 1900..9999). */
const MIN_YEAR = 1900;
const MAX_YEAR = 9999;

/** The wire key: `year-2026`, `last-12-months` or `next-12-months`. The same text is stored and sent as `?period=`. */
export function periodKey(period: Period): string {
  return period.kind === 'year' ? `year-${period.year}` : period.kind;
}

/** The period for a wire key, or null for anything else (an unknown kind, `year-26`, `year-0`, `year-99999`, whitespace). */
export function parsePeriodKey(key: string | null | undefined): Period | null {
  if (key === 'last-12-months' || key === 'next-12-months') return { kind: key };
  const match = /^year-(\d{4})$/.exec(key ?? '');
  if (!match) return null;
  const year = Number(match[1]);
  return year >= MIN_YEAR && year <= MAX_YEAR ? { kind: 'year', year } : null;
}

/** The period's name as an option label: a year is just the number, so only the two rolling windows are catalog messages. */
export function periodOptionMessage(period: Period): Message | null {
  switch (period.kind) {
    case 'year':
      return null;
    case 'last-12-months':
      return { key: 'subscriptions.period.last12' };
    case 'next-12-months':
      return { key: 'subscriptions.period.next12' };
  }
}

/** The heading of the period-total column and of the detail page's matching value: "Total 2026", "Total, last 12 months". */
export function periodTotalMessage(period: Period): Message {
  switch (period.kind) {
    case 'year':
      return { key: 'subscriptions.col.periodTotal.year', params: { year: String(period.year) } };
    case 'last-12-months':
      return { key: 'subscriptions.col.periodTotal.last12' };
    case 'next-12-months':
      return { key: 'subscriptions.col.periodTotal.next12' };
  }
}

export const PERIOD_STORAGE = new InjectionToken<Pick<Storage, 'getItem' | 'setItem'> | null>('report period storage', {
  providedIn: 'root',
  factory: () => {
    try {
      return inject(DOCUMENT).defaultView?.localStorage ?? null;
    } catch {
      return null;
    }
  },
});

/** The clock behind "the current year". Injectable so no spec depends on the real date. The API's own "today" is the UTC date. */
export const PERIOD_TODAY = new InjectionToken<() => Date>('report period today', {
  providedIn: 'root',
  factory: () => () => new Date(),
});

/**
 * The selected report period, shared by every page that totals over a period.
 *
 * Like the sidebar this is a cosmetic preference: every storage access is wrapped, so a blocked or full storage leaves the
 * in-memory selection working. Storage is read once on construction and written only after a user choice. The stored choice is
 * never cleared: a year that is not offered right now is only *resolved* away, so it works again when it reappears.
 */
@Injectable({ providedIn: 'root' })
export class PeriodService {
  private readonly storage = inject(PERIOD_STORAGE);
  private readonly today = inject(PERIOD_TODAY);
  private readonly state = signal<Period>(this.restore());
  private readonly offered = signal<readonly number[] | null>(null);

  /** What the user chose (or the stored choice, or the current year). It may name a year that is not offered. */
  readonly selection = this.state.asReadonly();
  /** The calendar years the selector offers once `GET periods` has answered, else null. */
  readonly years = this.offered.asReadonly();
  /** The period the pages load: the selection once it is known to be offered, else the current year. Both pages read this. */
  readonly effective = computed(() => {
    const years = this.offered();
    return years ? this.resolve(years) : this.state();
  });
  readonly effectiveKey = computed(() => periodKey(this.effective()));

  currentYear(): number {
    return this.today().getUTCFullYear();
  }

  currentPeriod(): Period {
    return { kind: 'year', year: this.currentYear() };
  }

  /** Selects a period by its key. An unknown key is ignored, so a stale option can never poison the selection. */
  select(key: string): void {
    const period = parsePeriodKey(key);
    if (!period) return;
    this.state.set(period);
    try {
      this.storage?.setItem(PERIOD_STORAGE_KEY, periodKey(period));
    } catch {
      // Storage may be full or blocked; the in-memory selection is still correct.
    }
  }

  setYears(years: readonly number[]): void {
    this.offered.set(years);
  }

  /** The selection if it is still offered, else the current year. The stored choice is left alone. */
  resolve(years: readonly number[]): Period {
    const selection = this.state();
    if (selection.kind !== 'year' || years.includes(selection.year)) return selection;
    return this.currentPeriod();
  }

  private restore(): Period {
    try {
      return parsePeriodKey(this.storage?.getItem(PERIOD_STORAGE_KEY)) ?? this.currentPeriod();
    } catch {
      return this.currentPeriod();
    }
  }
}
