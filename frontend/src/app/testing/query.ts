// Spec-only query helpers (only *.spec.ts files import this). A missing element throws instead of returning null, so a
// typo in a `data-*`/role hook fails the spec rather than silently skipping its assertions. It lives under a `testing`
// path so Tailwind's `@source not "./**/testing/**"` exclusion covers it, and it holds no class string.
// scripts/theme-dist.test.mjs fails if the sentinel utility named in the Tailwind source-narrowing check, p-[149px],
// reaches the built stylesheet from this file.

/** Anything a spec renders: a component fixture, a debug root element or a plain element. */
export type QueryRoot = { nativeElement: unknown } | Element | ParentNode;

const rootElement = (root: QueryRoot): ParentNode => {
  const candidate = 'nativeElement' in root ? root.nativeElement : root;
  if (candidate && typeof (candidate as ParentNode).querySelector === 'function') return candidate as ParentNode;
  throw new Error('query: the root has no querySelector (pass a fixture, an element or a document)');
};

/** The first element matching `selector`; throws when there is none. */
export function query<T extends Element = HTMLElement>(root: QueryRoot, selector: string): T {
  const found = rootElement(root).querySelector<T>(selector);
  if (!found) throw new Error(`query: no element matches "${selector}"`);
  return found;
}

/** Every element matching `selector`; throws when fewer than `min` match (default 1). */
export function queryAll<T extends Element = HTMLElement>(root: QueryRoot, selector: string, options: { min?: number } = {}): T[] {
  const min = options.min ?? 1;
  const found = Array.from(rootElement(root).querySelectorAll<T>(selector));
  if (found.length < min) throw new Error(`queryAll: expected at least ${min} element(s) matching "${selector}", found ${found.length}`);
  return found;
}
