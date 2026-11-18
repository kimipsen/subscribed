import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The loading placeholder: a daisyUI skeleton block, hidden from assistive technology (the page announces loading through its
 * own `role="status"` text). The host is `block` and full width, so it lays out like the `div` it replaces. Used by every
 * page that waits for data, hence a component; there is deliberately no size input (every page uses the same height, and a
 * class name built from an input would fail the complete-literal contract).
 */
@Component({
  selector: 'app-skeleton-block',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block w-full', 'aria-hidden': 'true' },
  template: `<div class="skeleton h-24 w-full"></div>`,
})
export class SkeletonBlock {}
