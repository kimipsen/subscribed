import { en } from '../../i18n/catalog.en';

const SELECTOR = 'h1,h2,h3,p,button,a,label,th,td,caption,legend,option,span,output,li,div';
const ATTRIBUTES = ['aria-label', 'title'];

function pattern(value: string): RegExp | null {
  // A value made only of placeholders, whitespace and punctuation cannot be told apart from data.
  if (!/[A-Za-z]/.test(value.replace(/\{[A-Za-z][A-Za-z0-9]*\}/g, ''))) return null;
  const escaped = value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/\\\{[A-Za-z][A-Za-z0-9]*\\\}/g, '.+');
  return new RegExp(`^${escaped}$`);
}

/**
 * English application copy still visible in a rendered tree: every text node of the usual text elements plus aria-label and title.
 * Values with placeholders become anchored patterns. Returns the offending English values.
 */
export function englishLeftovers(root: Element): string[] {
  const texts: string[] = [];
  for (const element of Array.from(root.querySelectorAll(SELECTOR))) {
    for (const node of Array.from(element.childNodes)) {
      if (node.nodeType === Node.TEXT_NODE) {
        const text = node.textContent?.replace(/\s+/g, ' ').trim();
        if (text) texts.push(text);
      }
    }
    for (const attribute of ATTRIBUTES) {
      const value = element.getAttribute(attribute)?.trim();
      if (value) texts.push(value);
    }
    // A leaf element's whole text, e.g. a heading that contains an inline element.
    if (element.children.length === 0 && element.textContent?.trim()) texts.push(element.textContent.replace(/\s+/g, ' ').trim());
  }
  const found = new Set<string>();
  for (const value of Object.values(en)) {
    if (value === 'Menu') continue;
    const matcher = value.includes('{') ? pattern(value) : new RegExp(`^${value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`);
    if (matcher && texts.some((text) => matcher.test(text))) found.add(value);
  }
  return [...found];
}
