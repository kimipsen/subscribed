/**
 * Below the `nav` breakpoint a page renders every row action twice: once in the table (`display:none` on phones)
 * and once in the card list. `document.querySelector('#id')` returns the first copy, which can be the hidden one,
 * and focusing a `display:none` element silently does nothing. Both copies therefore share
 * `data-focus-key="<kind>-<id>"` and this helper focuses the first copy that is actually visible.
 *
 * The default predicate must work in jsdom (no layout) and in browsers, so it never uses `getClientRects()`
 * (always empty in jsdom) or `offsetParent` (always null in jsdom and for `position:fixed`). An element is hidden only
 * when it, or an ancestor, has the `hidden` attribute, or when `checkVisibility()` exists and returns false. Unknown
 * (no `checkVisibility`) counts as visible.
 */
export const defaultIsVisible = (element: HTMLElement): boolean =>
  !element.closest('[hidden]') && element.checkVisibility?.() !== false;

/**
 * Focuses the first element under `root` whose `data-focus-key` equals `key` and that `isVisible` accepts; false when
 * none is visible. The key is compared as an attribute value instead of being spliced into a selector, so it needs no
 * escaping (and the helper does not depend on `CSS.escape`, which jsdom lacks).
 */
export function focusVisible(root: ParentNode, key: string, isVisible: (element: HTMLElement) => boolean = defaultIsVisible): boolean {
  for (const element of Array.from(root.querySelectorAll<HTMLElement>('[data-focus-key]'))) {
    if (element.getAttribute('data-focus-key') === key && isVisible(element)) {
      element.focus();
      return true;
    }
  }
  return false;
}
