import { checkOptionFields, looksLikeCardNumber, optionText } from './payment-option-text';

describe('looksLikeCardNumber (advisory: the server rule is the authority)', () => {
  it('passes 12 digits and ordinary names and labels', () => {
    for (const text of ['411111111111', 'Visa ·· 4242', 'Last 4 digits', 'Joint account', '', '4111 1111 ab 1111 1111']) {
      expect(looksLikeCardNumber(text), text).toBe(false);
    }
  });

  it('rejects a run of 13 or more digits, whatever separates them', () => {
    for (const text of ['4111111111111', '4111 1111 1111 1111', '4111-1111-1111-1111', '4111.1111.1111.1111', '4111/1111/1111/1111', '4111_1111_1111_1111']) {
      expect(looksLikeCardNumber(text), text).toBe(true);
    }
  });

  it('counts any Unicode decimal digit, so fullwidth and Arabic-Indic digits are rejected too', () => {
    expect(looksLikeCardNumber('４１１１４１１１４１１１４１１１')).toBe(true);
    expect(looksLikeCardNumber('٤١١١٤١١١٤١١١٤١١١')).toBe(true);
    expect(looksLikeCardNumber('4111 ４１１１ 1111 ٤١١١')).toBe(true);
  });

  it('lets a letter reset the run, like the server', () => {
    expect(looksLikeCardNumber('123456789012a123456789012')).toBe(false);
  });
});

describe('checkOptionFields', () => {
  it('accepts a name with an optional label', () => {
    expect(checkOptionFields('Visa', '')).toEqual({});
    expect(checkOptionFields('  Visa  ', ' Joint ')).toEqual({});
  });

  it('flags an empty or over-long name, an over-long label and control characters as invalid', () => {
    expect(checkOptionFields('   ', '').name).toBe('invalid');
    expect(checkOptionFields('a'.repeat(41), '').name).toBe('invalid');
    expect(checkOptionFields('a'.repeat(40), '').name).toBeUndefined();
    expect(checkOptionFields('Visa', 'a'.repeat(41)).label).toBe('invalid');
    expect(checkOptionFields('Vi\tsa', '').name).toBe('invalid');
    expect(checkOptionFields('Visa', 'a\nb').label).toBe('invalid');
  });

  it('flags a card number in the name and in the label separately', () => {
    expect(checkOptionFields('4111 1111 1111 1111', '')).toEqual({ name: 'cardNumber' });
    expect(checkOptionFields('Visa', '4111 1111 1111 1111')).toEqual({ label: 'cardNumber' });
    expect(checkOptionFields('4111111111111111', '4111111111111111')).toEqual({ name: 'cardNumber', label: 'cardNumber' });
  });
});

describe('optionText', () => {
  it('joins the name and the label', () => {
    expect(optionText({ name: 'Visa', label: 'Joint account' })).toBe('Visa · Joint account');
    expect(optionText({ name: 'Visa', label: null })).toBe('Visa');
  });
});
