import { formatWallTime, wallTimeDate } from './time-zone';

describe('wallTimeDate', () => {
  it('returns the date part of a complete wall time', () => {
    expect(wallTimeDate('2026-12-01T09:30')).toBe('2026-12-01');
  });

  it.each(['', '2026-12-01', '2026-12-01T09', '2026-12-01T09:30:00', '2026-12-01T09:30Z', '2026-13-01T09:30', 'x'])('rejects %j', (value) => {
    expect(wallTimeDate(value)).toBeNull();
  });
});

describe('formatWallTime', () => {
  it('formats the wall time as written, not shifted into the browser zone', () => {
    expect(formatWallTime('2026-12-01T09:30', 'en-GB').replace(/[\u00a0\u202f]/g, ' ')).toMatch(/1 Dec 2026.*09:30/);
    expect(formatWallTime('2026-07-01T00:05', 'en-GB')).toMatch(/00:05/);
  });

  it('returns text that is not a wall time unchanged', () => {
    expect(formatWallTime('soon', 'en')).toBe('soon');
  });
});
