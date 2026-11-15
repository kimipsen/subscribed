import {
  buildCategories,
  categoryKey,
  filterRows,
  groupRows,
  parseSelection,
  selectionExists,
  selectionValue,
} from './categories';
import { SubscriptionSummary } from './subscription.models';
import { overviewWith } from './testing/fixtures';

const rows = (...patches: Partial<SubscriptionSummary>[]): SubscriptionSummary[] => overviewWith(patches).subscriptions;
const inCategory = (...categories: (string | null)[]): SubscriptionSummary[] => rows(...categories.map((category) => ({ category })));

describe('categoryKey', () => {
  it('matches by case and whitespace (AC3)', () => {
    expect(categoryKey('Food')).toBe('food');
    expect(categoryKey(' food ')).toBe('food');
    expect(categoryKey('FOOD')).toBe('food');
    expect(categoryKey('Food  Court')).toBe(categoryKey('food court'));
    expect(categoryKey('Food\t\n Court')).toBe('food court');
  });

  it('keeps words apart', () => {
    expect(categoryKey('fo od')).not.toBe(categoryKey('food'));
  });

  it('is null for missing, empty and whitespace-only categories', () => {
    expect(categoryKey(null)).toBeNull();
    expect(categoryKey(undefined)).toBeNull();
    expect(categoryKey('')).toBeNull();
    expect(categoryKey(' \t  ')).toBeNull();
  });

  it('does not depend on the runtime locale (Turkish dotted I)', () => {
    expect(categoryKey('I')).toBe('i');
    expect(categoryKey('İstanbul')).not.toBe(categoryKey('istanbul'));
    expect(categoryKey('İstanbul')).toBe('İstanbul'.toLowerCase());
  });
});

describe('buildCategories', () => {
  it('is distinct by key and labels with the first-seen spelling, collapsed', () => {
    const set = buildCategories(inCategory(' Food  Court ', 'food court', 'Internet', 'INTERNET', 'FOOD COURT'));
    expect(set.options).toEqual([
      { key: 'food court', label: 'Food Court' },
      { key: 'internet', label: 'Internet' },
    ]);
    expect(set.hasUncategorized).toBe(false);
  });

  it('reports uncategorized rows without making them an option', () => {
    const set = buildCategories(inCategory(null, '', '   ', 'Food'));
    expect(set.options.map((option) => option.label)).toEqual(['Food']);
    expect(set.hasUncategorized).toBe(true);
  });

  it('never rewrites the stored value', () => {
    const input = inCategory(' food ');
    buildCategories(input);
    expect(input[0].category).toBe(' food ');
  });

  it('keeps categories literally named All and Uncategorized as ordinary options', () => {
    const set = buildCategories(inCategory('All', 'Uncategorized'));
    expect(set.options.map((option) => option.label)).toEqual(['All', 'Uncategorized']);
    expect(set.hasUncategorized).toBe(false);
  });
});

describe('filterRows', () => {
  const data = inCategory('Food', ' food ', 'Internet', null, '  ');

  it('returns everything for all', () => {
    expect(filterRows(data, { kind: 'all' })).toHaveLength(5);
  });

  it('filters by category key', () => {
    expect(filterRows(data, { kind: 'category', key: 'food' }).map((row) => row.id)).toEqual(['row-1', 'row-2']);
  });

  it('filters uncategorized', () => {
    expect(filterRows(data, { kind: 'uncategorized' }).map((row) => row.id)).toEqual(['row-4', 'row-5']);
  });

  it('returns an empty list for a key that matches nothing (R3.4)', () => {
    expect(filterRows(data, { kind: 'category', key: 'nope' })).toEqual([]);
  });

  it('keeps a category named "uncategorized" apart from the built-in choice', () => {
    const tricky = inCategory('Uncategorized', null);
    expect(filterRows(tricky, { kind: 'uncategorized' }).map((row) => row.id)).toEqual(['row-2']);
    expect(filterRows(tricky, { kind: 'category', key: 'uncategorized' }).map((row) => row.id)).toEqual(['row-1']);
  });
});

describe('selection encoding', () => {
  it('round-trips and never collides with categories named all or uncategorized', () => {
    expect(selectionValue({ kind: 'all' })).toBe('all');
    expect(selectionValue({ kind: 'uncategorized' })).toBe('uncategorized');
    expect(selectionValue({ kind: 'category', key: 'all' })).toBe('cat:all');
    expect(selectionValue({ kind: 'category', key: 'uncategorized' })).toBe('cat:uncategorized');
    for (const selection of [
      { kind: 'all' as const },
      { kind: 'uncategorized' as const },
      { kind: 'category' as const, key: 'all' },
      { kind: 'category' as const, key: 'uncategorized' },
      { kind: 'category' as const, key: 'food court' },
    ]) {
      expect(parseSelection(selectionValue(selection))).toEqual(selection);
    }
  });

  it('falls back to all for unknown values', () => {
    expect(parseSelection('whatever')).toEqual({ kind: 'all' });
  });

  it('knows whether a selection still exists', () => {
    const set = buildCategories(inCategory('Food', null));
    expect(selectionExists({ kind: 'all' }, set)).toBe(true);
    expect(selectionExists({ kind: 'uncategorized' }, set)).toBe(true);
    expect(selectionExists({ kind: 'category', key: 'food' }, set)).toBe(true);
    expect(selectionExists({ kind: 'category', key: 'gone' }, set)).toBe(false);
    expect(selectionExists({ kind: 'uncategorized' }, buildCategories(inCategory('Food')))).toBe(false);
  });
});

describe('groupRows', () => {
  it('orders alphabetically with Uncategorized last, in English and Danish (AC5)', () => {
    const data = inCategory('Zebra', null, 'Åben', 'Æble', 'apple', 'Øl', '');
    expect(groupRows(data, 'en-GB').map((group) => group.label)).toEqual(['Åben', 'Æble', 'apple', 'Øl', 'Zebra', null]);
    // Full ICU is assumed: Danish sorts Æ, Ø, Å after Z.
    expect(groupRows(data, 'da-DK').map((group) => group.label)).toEqual(['apple', 'Zebra', 'Æble', 'Øl', 'Åben', null]);
  });

  it('merges case and whitespace variants into one group with the first-seen label and counts every status', () => {
    const data = rows(
      { category: 'Food', status: 'active' },
      { category: ' food ', status: 'ended' },
      { category: 'FOOD', status: 'not-started' },
    );
    const [group] = groupRows(data, 'en-GB');
    expect(group).toMatchObject({ key: 'food', label: 'Food', count: 3 });
    expect(group.rows.map((row) => row.id)).toEqual(['row-1', 'row-2', 'row-3']);
  });

  it('totals the active rows per currency and never mixes currencies (AC4)', () => {
    const data = rows(
      { category: 'Media', currency: 'DKK', minorUnit: 2, normalizedMonthly: '79.00', normalizedYearly: '948.00' },
      { category: 'media', currency: 'DKK', minorUnit: 2, normalizedMonthly: '10.50', normalizedYearly: '126.00' },
      { category: 'Media', currency: 'JPY', minorUnit: 0, normalizedMonthly: '1490', normalizedYearly: '17880' },
      { category: 'Media', currency: 'DKK', minorUnit: 2, status: 'ended', normalizedMonthly: '5.00', normalizedYearly: '60.00' },
      { category: 'Media', currency: 'KWD', minorUnit: 3, status: 'not-started', normalizedMonthly: '1.000', normalizedYearly: '12.000' },
    );
    const [group] = groupRows(data, 'en-GB');
    expect(group.count).toBe(5);
    expect(group.totals).toEqual([
      { currency: 'DKK', minorUnit: 2, monthly: '89.50', yearly: '1074.00', activeCount: 2 },
      { currency: 'JPY', minorUnit: 0, monthly: '1490', yearly: '17880', activeCount: 1 },
    ]);
  });

  it('shows no totals for a group without active rows', () => {
    const [group] = groupRows(rows({ category: 'Old', status: 'ended' }, { category: 'Soon', status: 'not-started' }), 'en-GB');
    expect(group.totals).toEqual([]);
  });

  it('sums the displayed per-row strings exactly: three 0.00 rows total 0.00', () => {
    // Documented divergence from the server's round-once total (SubscriptionCalculator.Totals): the server sums the unrounded
    // values and rounds once, so it could show 0.01 for the same rows. A later switch to server totals must be deliberate.
    const zero = { category: 'Tiny', normalizedMonthly: '0.00', normalizedYearly: '0.00' };
    const [group] = groupRows(rows(zero, zero, zero), 'en-GB');
    expect(group.totals).toEqual([{ currency: 'DKK', minorUnit: 2, monthly: '0.00', yearly: '0.00', activeCount: 3 }]);
  });

  it('sums monthly and yearly exactly beyond the float range', () => {
    const big = { category: 'Big', normalizedMonthly: '9007199254740993.00', normalizedYearly: '0.10' };
    const small = { category: 'Big', normalizedMonthly: '0.01', normalizedYearly: '0.20' };
    const [group] = groupRows(rows(big, small), 'en-GB');
    expect(group.totals[0]).toMatchObject({ monthly: '9007199254740993.01', yearly: '0.30' });
  });

  it('drops a currency with malformed amounts but keeps the rows and other currencies', () => {
    const data = rows(
      { category: 'Mixed', currency: 'DKK', normalizedMonthly: '1,50', normalizedYearly: '18.00' },
      { category: 'Mixed', currency: 'JPY', minorUnit: 0, normalizedMonthly: '100', normalizedYearly: '1200' },
    );
    const [group] = groupRows(data, 'en-GB');
    expect(group.rows).toHaveLength(2);
    expect(group.totals.map((total) => total.currency)).toEqual(['JPY']);
  });

  it('takes the minor unit from the group\'s own rows', () => {
    const [group] = groupRows(rows({ category: 'Yen', currency: 'JPY', minorUnit: 0, status: 'active', normalizedMonthly: '100', normalizedYearly: '1200' }), 'en-GB');
    expect(group.totals[0].minorUnit).toBe(0);
  });

  it('returns no groups for no rows', () => {
    expect(groupRows([], 'en-GB')).toEqual([]);
  });
});
