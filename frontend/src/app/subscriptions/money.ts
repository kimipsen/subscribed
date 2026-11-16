/// <reference lib="es2023.intl" />

// Money in the browser is text only: it is parsed from input, formatted for display, and sent as a canonical decimal string
// (a dot, no grouping). It never goes through Number or parseFloat (TS-005). The one arithmetic allowed is exact integer
// BigInt addition of canonical strings in sumDecimalStrings (used for per-category group totals).

/** The active language's decimal separator, without Intl grouping: "." for en-GB and "," for da-DK. */
export function decimalSeparator(locale: string): string {
  return new Intl.NumberFormat(locale).formatToParts(1.1).find((part) => part.type === 'decimal')?.value ?? '.';
}

function formatter(locale: string, currency: string, minorUnit: number, signed: boolean): Intl.NumberFormat {
  return new Intl.NumberFormat(locale, {
    style: 'currency',
    currency,
    currencyDisplay: 'code',
    minimumFractionDigits: minorUnit,
    maximumFractionDigits: minorUnit,
    ...(signed ? { signDisplay: 'exceptZero' as const } : {}),
  });
}

/** Formats an API decimal string exactly (the ES2023 string overload), e.g. "DKK 1,548.00" or "1.548,00 DKK". */
export function formatMoney(amount: string, currency: string, minorUnit: number, locale: string): string {
  return formatter(locale, currency, minorUnit, false).format(amount as `${number}`);
}

/** Like formatMoney, with a sign on every non-zero value: "+DKK 10.00". */
export function formatSignedMoney(amount: string, currency: string, minorUnit: number, locale: string): string {
  return formatter(locale, currency, minorUnit, true).format(amount as `${number}`);
}

/** A one-decimal percentage with a sign on every non-zero value: "+16.9 %". */
export function formatPercentChange(percent: string, locale: string): string {
  const text = new Intl.NumberFormat(locale, {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
    signDisplay: 'exceptZero',
  }).format(percent as `${number}`);
  return `${text}\u00a0%`;
}

/**
 * Parses field text into the canonical decimal string the API takes, or null. Only the active language's decimal separator is
 * accepted (D-7): no grouping, at most nine integer digits, and at most as many fraction digits as the currency's minor unit.
 */
export function parseAmountInput(text: string, locale: string, minorUnit: number): string | null {
  const separator = decimalSeparator(locale);
  const trimmed = text.trim();
  if (!trimmed) return null;
  const parts = trimmed.split(separator);
  if (parts.length > 2) return null;
  const [integer, fraction] = parts;
  if (!/^[0-9]{1,9}$/.test(integer)) return null;
  if (fraction === undefined) return integer;
  if (minorUnit === 0 || !/^[0-9]+$/.test(fraction) || fraction.length > minorUnit || fraction.length > 3) return null;
  return `${integer}.${fraction}`;
}

/** Field text for a canonical decimal string: the dot becomes the language's separator and nothing else changes (never grouped). */
export function toAmountInput(canonical: string, locale: string): string {
  return canonical.replace('.', decimalSeparator(locale));
}

const CANONICAL_DECIMAL = /^[0-9]+(?:\.[0-9]+)?$/;

/** Parses a canonical non-negative decimal string into whole minor units, or null when it is malformed for this minor unit. */
function toMinorUnits(value: string, minorUnit: number): bigint | null {
  if (!CANONICAL_DECIMAL.test(value)) return null;
  const [integer, fraction = ''] = value.split('.');
  if (fraction.length > minorUnit) return null;
  return BigInt(integer + fraction.padEnd(minorUnit, '0'));
}

/**
 * Exactly sums canonical decimal strings in whole minor units (BigInt: no Number, no float) and returns a canonical decimal
 * string, or null when any input is malformed (empty, "1,50", "abc", a leading "+", or more fraction digits than the minor unit).
 * It never throws, so a bad value cannot blank a computed view. An empty list sums to zero.
 *
 * This is the exact sum of the given (already rounded) strings. It can differ from the server's round-once totals.
 */
export function sumDecimalStrings(values: string[], minorUnit: number): string | null {
  let total = 0n;
  for (const value of values) {
    const minor = toMinorUnits(value, minorUnit);
    if (minor === null) return null;
    total += minor;
  }
  const digits = total.toString().padStart(minorUnit + 1, '0');
  if (minorUnit === 0) return digits;
  return `${digits.slice(0, -minorUnit)}.${digits.slice(-minorUnit)}`;
}
