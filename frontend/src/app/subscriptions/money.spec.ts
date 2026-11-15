import {
  decimalSeparator,
  formatMoney,
  formatPercentChange,
  formatSignedMoney,
  parseAmountInput,
  sumDecimalStrings,
  toAmountInput,
} from './money';

// ICU has changed these separators between releases, so compare after normalising them to a plain space.
const plain = (value: string) => value.replace(/[\u00a0\u202f]/g, ' ');

describe('money formatting', () => {
  it('formats exact decimal strings for the active language (EX-018)', () => {
    expect(plain(formatMoney('1548.00', 'DKK', 2, 'en-GB'))).toBe('DKK 1,548.00');
    expect(plain(formatMoney('1548.00', 'DKK', 2, 'da-DK'))).toBe('1.548,00 DKK');
  });

  it('formats the currency by its own minor unit', () => {
    expect(plain(formatMoney('1490', 'JPY', 0, 'en-GB'))).toBe('JPY 1,490');
    expect(plain(formatMoney('14.808', 'KWD', 3, 'en-GB'))).toBe('KWD 14.808');
    expect(plain(formatMoney('1.234', 'KWD', 3, 'da-DK'))).toBe('1,234 KWD');
  });

  it('never goes through a binary float', () => {
    expect(plain(formatMoney('999999999.99', 'DKK', 2, 'en-GB'))).toBe('DKK 999,999,999.99');
    expect(plain(formatMoney('0.10', 'DKK', 2, 'en-GB'))).toBe('DKK 0.10');
    expect(plain(formatMoney('9007199254740993.00', 'DKK', 2, 'en-GB'))).toBe('DKK 9,007,199,254,740,993.00');
  });

  it('signs every non-zero change and leaves zero unsigned', () => {
    expect(plain(formatSignedMoney('10.00', 'DKK', 2, 'en-GB'))).toBe('+DKK 10.00');
    expect(plain(formatSignedMoney('-15.75', 'DKK', 2, 'en-GB'))).toBe('-DKK 15.75');
    expect(plain(formatSignedMoney('0.00', 'DKK', 2, 'en-GB'))).toBe('DKK 0.00');
    expect(plain(formatPercentChange('16.9', 'en-GB'))).toBe('+16.9 %');
    expect(plain(formatPercentChange('-15.9', 'da-DK'))).toBe('-15,9 %');
    expect(plain(formatPercentChange('0.0', 'en-GB'))).toBe('0.0 %');
  });
});

describe('parseAmountInput', () => {
  it('accepts the active decimal separator and returns the canonical string', () => {
    expect(parseAmountInput('129,50', 'da-DK', 2)).toBe('129.50');
    expect(parseAmountInput('129.50', 'en-GB', 2)).toBe('129.50');
    expect(parseAmountInput(' 129 ', 'en-GB', 2)).toBe('129');
    expect(parseAmountInput('0', 'en-GB', 2)).toBe('0');
    expect(parseAmountInput('1,234', 'da-DK', 3)).toBe('1.234');
    expect(parseAmountInput('999999999,99', 'da-DK', 2)).toBe('999999999.99');
  });

  it('takes JPY as whole numbers only', () => {
    expect(parseAmountInput('1490', 'en-GB', 0)).toBe('1490');
    expect(parseAmountInput('1490.0', 'en-GB', 0)).toBeNull();
    expect(parseAmountInput('1490,5', 'da-DK', 0)).toBeNull();
  });

  it('rejects too many decimals, grouping separators, the other language\'s separator and 10-digit integers', () => {
    expect(parseAmountInput('1.234', 'en-GB', 2)).toBeNull();
    expect(parseAmountInput('1,2345', 'da-DK', 3)).toBeNull();
    expect(parseAmountInput('1,490.00', 'en-GB', 2)).toBeNull();
    expect(parseAmountInput('1.490,00', 'da-DK', 2)).toBeNull();
    expect(parseAmountInput('1.490', 'da-DK', 2)).toBeNull();
    expect(parseAmountInput('129,00', 'en-GB', 2)).toBeNull();
    expect(parseAmountInput('129.00', 'da-DK', 2)).toBeNull();
    expect(parseAmountInput('1000000000', 'en-GB', 2)).toBeNull();
    expect(parseAmountInput('', 'en-GB', 2)).toBeNull();
    expect(parseAmountInput('-5', 'en-GB', 2)).toBeNull();
    expect(parseAmountInput('1e3', 'en-GB', 2)).toBeNull();
    expect(parseAmountInput('12٣', 'en-GB', 2)).toBeNull();
    expect(parseAmountInput('1.', 'en-GB', 2)).toBeNull();
    expect(parseAmountInput('.5', 'en-GB', 2)).toBeNull();
  });
});

describe('toAmountInput', () => {
  it('swaps only the decimal separator and never groups', () => {
    expect(toAmountInput('1490.00', 'da-DK')).toBe('1490,00');
    expect(toAmountInput('1490.00', 'en-GB')).toBe('1490.00');
    expect(toAmountInput('129', 'da-DK')).toBe('129');
    expect(decimalSeparator('da-DK')).toBe(',');
    expect(decimalSeparator('en-GB')).toBe('.');
  });

  it('is the inverse of parseAmountInput for valid input', () => {
    for (const locale of ['da-DK', 'en-GB']) {
      for (const canonical of ['1490.00', '999999999.99', '129.5']) {
        expect(parseAmountInput(toAmountInput(canonical, locale), locale, 2)).toBe(canonical);
      }
    }
    expect(parseAmountInput(toAmountInput('1490', 'da-DK'), 'da-DK', 0)).toBe('1490');
  });
});

describe('sumDecimalStrings', () => {
  it('sums exactly at minor units 0, 2 and 3', () => {
    expect(sumDecimalStrings(['1490', '10'], 0)).toBe('1500');
    expect(sumDecimalStrings(['10.50', '4.75'], 2)).toBe('15.25');
    expect(sumDecimalStrings(['14.808', '0.192'], 3)).toBe('15.000');
  });

  it('is exact where a binary float is not', () => {
    expect(sumDecimalStrings(['0.10', '0.20'], 2)).toBe('0.30');
    expect(sumDecimalStrings(['9007199254740993.00', '0.01'], 2)).toBe('9007199254740993.01');
    expect(sumDecimalStrings(['9007199254740993.00', '9007199254740993.00'], 2)).toBe('18014398509481986.00');
  });

  it('pads input with fewer fraction digits than the minor unit', () => {
    expect(sumDecimalStrings(['1'], 2)).toBe('1.00');
    expect(sumDecimalStrings(['1.5', '2'], 2)).toBe('3.50');
  });

  it('sums an empty list to zero', () => {
    expect(sumDecimalStrings([], 2)).toBe('0.00');
    expect(sumDecimalStrings([], 0)).toBe('0');
  });

  it('keeps leading zeros in the fraction and a zero integer part', () => {
    expect(sumDecimalStrings(['0.05', '0.04'], 2)).toBe('0.09');
    expect(sumDecimalStrings(['0.00', '0.00', '0.00'], 2)).toBe('0.00');
  });

  it('returns null instead of throwing for malformed input', () => {
    for (const bad of ['', '1,50', 'abc', '+1.00', '-1.00', '1.', '.5', ' 1.00', '1e3']) {
      expect(sumDecimalStrings(['1.00', bad], 2)).toBeNull();
    }
    expect(sumDecimalStrings(['1.234'], 2)).toBeNull();
    expect(sumDecimalStrings(['1.5'], 0)).toBeNull();
  });

  it('formats through formatMoney like any other amount', () => {
    const total = sumDecimalStrings(['1000.10', '548.20'], 2) as string;
    expect(plain(formatMoney(total, 'DKK', 2, 'en-GB'))).toBe('DKK 1,548.30');
  });
});
