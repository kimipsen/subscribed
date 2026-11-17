import { Provider } from '@angular/core';
import { HttpTestingController } from '@angular/common/http/testing';
import { PERIOD_STORAGE, PERIOD_STORAGE_KEY, PERIOD_TODAY } from '../period';
import { PeriodOptions, Wire } from '../subscription.models';
import periodsJson from './periods.json';

/** The pinned "now" of every spec that shows period figures: the golden fixtures are for the calendar year 2026. */
export const TEST_TODAY = new Date('2026-11-17T12:00:00Z');

export interface FakeStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  readonly values: Map<string, string>;
}

/** An in-memory storage, so the real localStorage of the test browser never leaks one spec's selection into the next. */
export function memoryStorage(initial: Record<string, string> = {}): FakeStorage {
  const values = new Map(Object.entries(initial));
  return {
    values,
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => void values.set(key, value),
  };
}

/** A storage whose every access throws (blocked or disabled storage). */
export const throwingStorage = {
  getItem: (): string | null => {
    throw new DOMException('Blocked', 'SecurityError');
  },
  setItem: (): void => {
    throw new DOMException('Full', 'QuotaExceededError');
  },
};

export { PERIOD_STORAGE_KEY };

/** Providers that pin today and fake the period storage (a fresh in-memory one unless given). */
export function periodTestProviders(storage: Pick<Storage, 'getItem' | 'setItem'> | null = memoryStorage()): Provider[] {
  return [
    { provide: PERIOD_TODAY, useValue: () => TEST_TODAY },
    { provide: PERIOD_STORAGE, useValue: storage },
  ];
}

export const periodOptionsFixture = (): PeriodOptions => structuredClone(periodsJson satisfies Wire<PeriodOptions>) as PeriodOptions;

/** Answers the selector's single GET periods request. */
export function flushPeriods(http: HttpTestingController, options: PeriodOptions = periodOptionsFixture()): void {
  http.expectOne('/api/subscriptions/periods').flush(options);
}
