import { isHttpsUrl, normalizeUnsubscribeUrl } from './unsubscribe-url';

describe('normalizeUnsubscribeUrl (mirrors the server rule)', () => {
  it.each([
    'https://example.com',
    'https://Example.com/Path?q=1#frag',
    'HTTPS://x.example',
    'Https://x.example/cancel',
    'https://localhost/cancel',
    'https://127.0.0.1/cancel',
    'https://[::1]/cancel',
    'https://xn--bcher-kva.example/cancel',
    'https://example.com/' + 'a'.repeat(2048 - 'https://example.com/'.length),
  ])('accepts %s and keeps it as typed', (value) => {
    expect(normalizeUnsubscribeUrl(value)).toBe(value);
  });

  it.each(['', '   ', '  '])('treats %j as none (null)', (value) => {
    expect(normalizeUnsubscribeUrl(value)).toBeNull();
  });

  it('trims surrounding white space only', () => {
    expect(normalizeUnsubscribeUrl('  https://Example.com/Path  ')).toBe('https://Example.com/Path');
  });

  it.each([
    'http://example.com',
    'javascript:alert(1)',
    '/relative',
    'example.com/cancel',
    'https:/x',
    'https:\\\\x',
    'https://',
    'https:///x',
    'https:example.com',
    'https://example.com/' + 'a'.repeat(2049 - 'https://example.com/'.length),
    'https://example.com/a b',
    'https://example.com/a\tb',
    'https://example.com/a\nb',
    'https://example.com/a b',
    'https://example.com/a b',
    'https://u@a.example',
    'https://user:pass@a.example/x',
    'https://a.example%2F@b',
    'https://a.example\\@b.example',
    'https://example.com/a\\b',
  ])('rejects %j', (value) => {
    expect(normalizeUnsubscribeUrl(value)).toBeUndefined();
  });

  it('allows an @ after the authority (path, query or fragment)', () => {
    expect(normalizeUnsubscribeUrl('https://example.com/a@b?x=@#@')).toBe('https://example.com/a@b?x=@#@');
  });
});

describe('isHttpsUrl', () => {
  it.each(['https://x', 'HTTPS://x', 'Https://x'])('is true for %s', (value) => expect(isHttpsUrl(value)).toBe(true));
  it.each([null, undefined, '', 'http://x', 'javascript:alert(1)', 'https:/x', '//x'])('is false for %j', (value) => expect(isHttpsUrl(value)).toBe(false));
});
