import { sumDecimalStrings } from './money';
import { SubscriptionSummary } from './subscription.models';

// Pure helpers behind the client-side category view (filter, group, form suggestions). Categories stay free text on the wire;
// the key below is used for matching only and stored values are never rewritten.

export interface CategoryOption {
  /** The matching key: trimmed, whitespace-collapsed and lower-cased. */
  key: string;
  /** The first-seen spelling, trimmed and whitespace-collapsed, for display. */
  label: string;
}

export interface CategorySet {
  options: CategoryOption[];
  hasUncategorized: boolean;
}

export type CategorySelection = { kind: 'all' } | { kind: 'uncategorized' } | { kind: 'category'; key: string };

export interface GroupTotal {
  currency: string;
  minorUnit: number;
  monthly: string;
  yearly: string;
  activeCount: number;
}

export interface CategoryGroup {
  /** Null for the Uncategorized group. */
  key: string | null;
  /** Null for the Uncategorized group: the screen supplies the localized label. */
  label: string | null;
  rows: SubscriptionSummary[];
  count: number;
  totals: GroupTotal[];
}

export const ALL_SELECTION: CategorySelection = { kind: 'all' };

function collapse(raw: string): string {
  return raw.replace(/\s+/g, ' ').trim();
}

/**
 * The matching key for a category, or null when it is missing, empty or whitespace-only (Uncategorized). It uses toLowerCase,
 * not toLocaleLowerCase, so it never depends on the runtime locale ("İstanbul" stays distinct from "istanbul").
 */
export function categoryKey(raw: string | null | undefined): string | null {
  if (raw === null || raw === undefined) return null;
  const collapsed = collapse(raw);
  return collapsed ? collapsed.toLowerCase() : null;
}

/** Distinct categories in first-seen order of the given rows, plus whether any row is uncategorized. */
export function buildCategories(rows: SubscriptionSummary[]): CategorySet {
  const seen = new Map<string, CategoryOption>();
  let hasUncategorized = false;
  for (const row of rows) {
    const key = categoryKey(row.category);
    if (key === null) {
      hasUncategorized = true;
    } else if (!seen.has(key)) {
      seen.set(key, { key, label: collapse(row.category as string) });
    }
  }
  return { options: [...seen.values()], hasUncategorized };
}

export function filterRows(rows: SubscriptionSummary[], selection: CategorySelection): SubscriptionSummary[] {
  switch (selection.kind) {
    case 'all':
      return rows;
    case 'uncategorized':
      return rows.filter((row) => categoryKey(row.category) === null);
    case 'category':
      return rows.filter((row) => categoryKey(row.category) === selection.key);
  }
}

/** `<select>` option value: "all", "uncategorized" or "cat:<key>", so a category named "all" cannot collide with a built-in. */
export function selectionValue(selection: CategorySelection): string {
  switch (selection.kind) {
    case 'all':
      return 'all';
    case 'uncategorized':
      return 'uncategorized';
    case 'category':
      return `cat:${selection.key}`;
  }
}

export function parseSelection(value: string): CategorySelection {
  if (value === 'uncategorized') return { kind: 'uncategorized' };
  if (value.startsWith('cat:')) return { kind: 'category', key: value.slice('cat:'.length) };
  return ALL_SELECTION;
}

/** Whether a stored selection still names something present in the category set. */
export function selectionExists(selection: CategorySelection, set: CategorySet): boolean {
  switch (selection.kind) {
    case 'all':
      return true;
    case 'uncategorized':
      return set.hasUncategorized;
    case 'category':
      return set.options.some((option) => option.key === selection.key);
  }
}

/**
 * Per-currency totals over the active rows of a group. These are exact sums of the rows' displayed (already rounded) monthly and
 * yearly strings. That is a documented divergence from the server's round-once totals (SubscriptionCalculator.Totals): three rows
 * that each show 0.00 total 0.00 here. A currency whose sums are malformed is dropped. The minor unit comes from the rows of that
 * currency, never from the household totals.
 */
function groupTotals(rows: SubscriptionSummary[]): GroupTotal[] {
  const byCurrency = new Map<string, SubscriptionSummary[]>();
  for (const row of rows) {
    const list = byCurrency.get(row.currency);
    if (list) list.push(row);
    else byCurrency.set(row.currency, [row]);
  }
  const totals: GroupTotal[] = [];
  for (const currency of [...byCurrency.keys()].sort()) {
    const ofCurrency = byCurrency.get(currency) as SubscriptionSummary[];
    const active = ofCurrency.filter((row) => row.status === 'active');
    if (active.length === 0) continue;
    const minorUnit = ofCurrency[0].minorUnit;
    const monthly = sumDecimalStrings(active.map((row) => row.normalizedMonthly), minorUnit);
    const yearly = sumDecimalStrings(active.map((row) => row.normalizedYearly), minorUnit);
    if (monthly === null || yearly === null) continue;
    totals.push({ currency, minorUnit, monthly, yearly, activeCount: active.length });
  }
  return totals;
}

/** Groups by category key, alphabetical by the locale's collation (ties by key), with Uncategorized last. */
export function groupRows(rows: SubscriptionSummary[], locale: string): CategoryGroup[] {
  const categorized = new Map<string, { label: string; rows: SubscriptionSummary[] }>();
  const uncategorized: SubscriptionSummary[] = [];
  for (const row of rows) {
    const key = categoryKey(row.category);
    if (key === null) {
      uncategorized.push(row);
      continue;
    }
    const group = categorized.get(key);
    if (group) group.rows.push(row);
    else categorized.set(key, { label: collapse(row.category as string), rows: [row] });
  }
  const collator = new Intl.Collator(locale);
  const groups: CategoryGroup[] = [...categorized.entries()]
    .sort(([keyA, a], [keyB, b]) => collator.compare(a.label, b.label) || keyA.localeCompare(keyB))
    .map(([key, group]) => ({ key, label: group.label, rows: group.rows, count: group.rows.length, totals: groupTotals(group.rows) }));
  if (uncategorized.length > 0) {
    groups.push({ key: null, label: null, rows: uncategorized, count: uncategorized.length, totals: groupTotals(uncategorized) });
  }
  return groups;
}
