import { query, queryAll } from './query';

describe('query helpers', () => {
  const root = (html: string) => {
    const element = document.createElement('div');
    element.innerHTML = html;
    return element;
  };

  it('query returns the first match and throws, naming the selector, when none matches', () => {
    const el = root('<p data-a>1</p><p data-a>2</p>');
    expect(query(el, '[data-a]').textContent).toBe('1');
    expect(() => query(el, '[data-missing]')).toThrowError(/data-missing/);
  });

  it('queryAll returns every match and enforces min', () => {
    const el = root('<i></i><i></i>');
    expect(queryAll(el, 'i', { min: 2 })).toHaveLength(2);
    expect(() => queryAll(el, 'i', { min: 3 })).toThrowError(/at least 3.*found 2/);
    expect(() => queryAll(el, 'b')).toThrowError(/at least 1/);
    expect(queryAll(el, 'b', { min: 0 })).toEqual([]);
  });

  it('accepts a fixture-like object with a nativeElement', () => {
    const el = root('<span id="x"></span>');
    expect(query({ nativeElement: el }, '#x').id).toBe('x');
    expect(() => query({ nativeElement: null }, '#x')).toThrowError(/no querySelector/);
  });
});
