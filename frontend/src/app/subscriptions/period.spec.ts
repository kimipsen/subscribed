import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import {
  PERIOD_STORAGE,
  PERIOD_STORAGE_KEY,
  PERIOD_TODAY,
  PeriodService,
  parsePeriodKey,
  periodKey,
  periodOptionMessage,
  periodTotalMessage,
} from './period';
import { TEST_TODAY, memoryStorage, periodTestProviders, throwingStorage } from './testing/period-providers';

function service(storage: Pick<Storage, 'getItem' | 'setItem'> | null, today: Date = TEST_TODAY): PeriodService {
  TestBed.configureTestingModule({
    providers: [
      { provide: PERIOD_TODAY, useValue: () => today },
      { provide: PERIOD_STORAGE, useValue: storage },
    ],
  });
  return TestBed.inject(PeriodService);
}

describe('period keys', () => {
  it('round-trips every kind through the wire key', () => {
    for (const key of ['year-2026', 'year-1900', 'year-9999', 'last-12-months', 'next-12-months']) {
      const period = parsePeriodKey(key);
      expect(period, key).not.toBeNull();
      expect(periodKey(period!), key).toBe(key);
    }
    expect(parsePeriodKey('year-2026')).toEqual({ kind: 'year', year: 2026 });
  });

  it('rejects anything the API would reject', () => {
    for (const bad of [null, undefined, '', 'year-0', 'year-1899', 'year-26', 'year-02026', 'year-99999', 'year--2026', 'year-2026 ', ' year-2026',
      'Year-2026', 'year-2026x', 'last-12-months ', 'next-6-months', 'garbage', '{"kind":"year"}']) {
      expect(parsePeriodKey(bad), String(bad)).toBeNull();
    }
  });

  it('names the options and the total headings as catalog messages, a year being just its number', () => {
    expect(periodOptionMessage({ kind: 'year', year: 2026 })).toBeNull();
    expect(periodOptionMessage({ kind: 'last-12-months' })).toEqual({ key: 'subscriptions.period.last12' });
    expect(periodOptionMessage({ kind: 'next-12-months' })).toEqual({ key: 'subscriptions.period.next12' });
    expect(periodTotalMessage({ kind: 'year', year: 2026 })).toEqual({ key: 'subscriptions.col.periodTotal.year', params: { year: '2026' } });
    expect(periodTotalMessage({ kind: 'last-12-months' })).toEqual({ key: 'subscriptions.col.periodTotal.last12' });
    expect(periodTotalMessage({ kind: 'next-12-months' })).toEqual({ key: 'subscriptions.col.periodTotal.next12' });
  });
});

describe('PeriodService', () => {
  it('starts with the current calendar year when nothing is stored, from the injected clock', () => {
    expect(service(memoryStorage()).selection()).toEqual({ kind: 'year', year: 2026 });
    TestBed.resetTestingModule();
    expect(service(memoryStorage(), new Date('2031-01-01T00:00:00Z')).selection()).toEqual({ kind: 'year', year: 2031 });
  });

  it('uses the UTC year, like the API\'s own "today"', () => {
    expect(service(memoryStorage(), new Date('2026-12-31T23:30:00Z')).currentYear()).toBe(2026);
    TestBed.resetTestingModule();
    expect(service(memoryStorage(), new Date('2027-01-01T00:00:00Z')).currentYear()).toBe(2027);
  });

  it('restores a stored selection', () => {
    expect(service(memoryStorage({ [PERIOD_STORAGE_KEY]: 'next-12-months' })).selection()).toEqual({ kind: 'next-12-months' });
    TestBed.resetTestingModule();
    expect(service(memoryStorage({ [PERIOD_STORAGE_KEY]: 'year-2024' })).selection()).toEqual({ kind: 'year', year: 2024 });
  });

  it('falls back to the current year for an invalid stored value', () => {
    for (const stored of ['', 'garbage', 'year-99999', 'year-0', '"year-2024"', 'null']) {
      TestBed.resetTestingModule();
      expect(service(memoryStorage({ [PERIOD_STORAGE_KEY]: stored })).selection(), stored).toEqual({ kind: 'year', year: 2026 });
    }
  });

  it('writes the key under subscribed.period only after a choice, and ignores an unknown key', () => {
    const storage = memoryStorage();
    const periods = service(storage);
    expect(storage.values.size).toBe(0);
    periods.select('last-12-months');
    expect(storage.values.get('subscribed.period')).toBe('last-12-months');
    expect(periods.selection()).toEqual({ kind: 'last-12-months' });
    periods.select('year-2025');
    expect(storage.values.get(PERIOD_STORAGE_KEY)).toBe('year-2025');
    periods.select('nonsense');
    expect(periods.selection()).toEqual({ kind: 'year', year: 2025 });
    expect(storage.values.get(PERIOD_STORAGE_KEY)).toBe('year-2025');
  });

  it('keeps working in memory when the storage throws on get and on set', () => {
    const periods = service(throwingStorage);
    expect(periods.selection()).toEqual({ kind: 'year', year: 2026 });
    expect(() => periods.select('next-12-months')).not.toThrow();
    expect(periods.selection()).toEqual({ kind: 'next-12-months' });
  });

  it('works without any storage', () => {
    const periods = service(null);
    periods.select('year-2024');
    expect(periods.selection()).toEqual({ kind: 'year', year: 2024 });
  });

  it('only ever reads the storage once, on construction', () => {
    const storage = { getItem: vi.fn(() => 'year-2025'), setItem: vi.fn() };
    const periods = service(storage);
    periods.select('last-12-months');
    periods.setYears([2025, 2026]);
    expect(storage.getItem).toHaveBeenCalledTimes(1);
  });

  describe('resolve', () => {
    it('returns a selection that is still offered, and always the two rolling windows', () => {
      const periods = service(memoryStorage({ [PERIOD_STORAGE_KEY]: 'year-2025' }));
      expect(periods.resolve([2025, 2026])).toEqual({ kind: 'year', year: 2025 });
      periods.select('last-12-months');
      expect(periods.resolve([])).toEqual({ kind: 'last-12-months' });
      periods.select('next-12-months');
      expect(periods.resolve([2026])).toEqual({ kind: 'next-12-months' });
    });

    it('falls back to the current year for a year that is not offered, without clearing the stored choice', () => {
      const storage = memoryStorage({ [PERIOD_STORAGE_KEY]: 'year-2019' });
      const periods = service(storage);
      expect(periods.resolve([2024, 2025, 2026, 2027])).toEqual({ kind: 'year', year: 2026 });
      expect(periods.selection()).toEqual({ kind: 'year', year: 2019 });
      expect(storage.values.get(PERIOD_STORAGE_KEY)).toBe('year-2019');
      // The year appears again (a subscription was added): the same stored choice works again.
      expect(periods.resolve([2019, 2026])).toEqual({ kind: 'year', year: 2019 });
    });
  });

  describe('effective', () => {
    it('is the selection until the offered years are known, then the resolved one', () => {
      const periods = service(memoryStorage({ [PERIOD_STORAGE_KEY]: 'year-2019' }));
      expect(periodKey(periods.effective())).toBe('year-2019');
      periods.setYears([2025, 2026]);
      expect(periods.effectiveKey()).toBe('year-2026');
      periods.setYears([2019, 2026]);
      expect(periods.effectiveKey()).toBe('year-2019');
    });

    it('follows a new selection, which every page reading it sees', () => {
      const periods = service(memoryStorage());
      const second = TestBed.inject(PeriodService);
      expect(second).toBe(periods);
      periods.select('next-12-months');
      expect(second.effectiveKey()).toBe('next-12-months');
    });
  });
});

describe('periodTestProviders', () => {
  it('pins the clock and isolates the storage per call', () => {
    TestBed.configureTestingModule({ providers: periodTestProviders() });
    const periods = TestBed.inject(PeriodService);
    periods.select('year-2025');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: periodTestProviders() });
    expect(TestBed.inject(PeriodService).selection()).toEqual({ kind: 'year', year: 2026 });
  });
});
