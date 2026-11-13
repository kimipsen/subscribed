export const SUPPORTED_LANGUAGES = [
  { code: 'en', endonym: 'English', dateLocale: 'en-GB' },
  { code: 'da', endonym: 'Dansk', dateLocale: 'da-DK' },
] as const;

export type Language = (typeof SUPPORTED_LANGUAGES)[number]['code'];
export const DEFAULT_LANGUAGE: Language = 'en';

export function isLanguage(value: unknown): value is Language {
  return SUPPORTED_LANGUAGES.some((language) => language.code === value);
}
