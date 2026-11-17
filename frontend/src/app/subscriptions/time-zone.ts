// The server stores a cancel-by deadline as a UTC instant and sends it back as local wall time in the household zone
// (`yyyy-MM-ddTHH:mm`). The browser never converts between zones: it only formats that wall time.

const WALL_TIME = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})$/;

/** The date part (`yyyy-MM-dd`) of a wall-time string, or null when the text is not a complete `yyyy-MM-ddTHH:mm`. */
export function wallTimeDate(value: string): string | null {
  const match = WALL_TIME.exec(value);
  if (!match || Number.isNaN(Date.parse(`${match[1]}T${match[2]}:00Z`))) return null;
  return match[1];
}

/** Formats a wall-time string as it reads on the household's clock, without applying the browser's own zone. */
export function formatWallTime(value: string, locale: string): string {
  if (wallTimeDate(value) === null) return value;
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' }).format(new Date(`${value}:00Z`));
}
