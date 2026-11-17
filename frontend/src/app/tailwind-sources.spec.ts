// Holds only the sentinel utility literal 'w-[137px]' for scripts/theme-dist.test.mjs, which fails if Tailwind
// ever scans spec files (it would then emit .w-[137px] into the built stylesheet).
describe('tailwind sources', () => {
  it('keeps spec literals out of the stylesheet (checked by theme-dist.test.mjs)', () => {
    const sentinel = 'w-[137px]';
    expect(sentinel).toBe('w-[137px]');
  });
});
