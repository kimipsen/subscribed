import { PaymentOption } from './subscription.models';

export const MAX_OPTION_NAME = 40;
export const MAX_OPTION_LABEL = 40;
const CARD_NUMBER_DIGITS = 13;

/**
 * Early feedback only: the server rule and the server tests are the authority, and a drift between the two is intentional. The same
 * scan as the server: a running digit count over UTF-16 code units, any Unicode decimal digit (Nd) counts, a letter resets the run,
 * and every other character is a separator that does not reset it. True when a run reaches 13 digits.
 */
export function looksLikeCardNumber(text: string): boolean {
  let run = 0;
  const trimmed = text.trim();
  for (let index = 0; index < trimmed.length; index++) {
    const unit = trimmed.charAt(index);
    if (/\p{Nd}/u.test(unit)) {
      run++;
      if (run >= CARD_NUMBER_DIGITS) return true;
    } else if (/\p{L}/u.test(unit)) {
      run = 0;
    }
  }
  return false;
}

const hasControl = (value: string): boolean => Array.from(value).some((c) => {
  const code = c.codePointAt(0) ?? 0;
  return code < 32 || (code >= 127 && code <= 159);
});

export type OptionField = 'name' | 'label' | 'brand';
export type OptionProblem = 'invalid' | 'cardNumber';

/** Client-side rules for a name (1-40 characters) and an optional label (at most 40), neither with control characters nor a card number. */
export function checkOptionFields(name: string, label: string): Partial<Record<OptionField, OptionProblem>> {
  const problems: Partial<Record<OptionField, OptionProblem>> = {};
  const n = name.trim();
  const l = label.trim();
  if (n.length < 1 || n.length > MAX_OPTION_NAME || hasControl(n)) problems.name = 'invalid';
  else if (looksLikeCardNumber(n)) problems.name = 'cardNumber';
  if (l.length > MAX_OPTION_LABEL || hasControl(l)) problems.label = 'invalid';
  else if (looksLikeCardNumber(l)) problems.label = 'cardNumber';
  return problems;
}

/** "Visa" or "Visa · Joint account": what a picker and a history row show. */
export function optionText(option: Pick<PaymentOption, 'name' | 'label'>): string {
  return option.label ? `${option.name} · ${option.label}` : option.name;
}
