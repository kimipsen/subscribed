import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The page panel: a daisyUI card on the base surface. The host is `block` so the panel keeps the block layout a plain `div`
 * had (an Angular host element is `display: inline` otherwise). The inner `section` is the landmark: `labelledBy` sets its
 * `aria-labelledby`, so a page that names its panel by a heading id keeps that wiring.
 *
 * `narrow` caps the width for the single-purpose pre-session panels (sign-in, access, unavailable); the default is wide.
 */
@Component({
  selector: 'app-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block my-4' },
  template: `
    <section class="card card-body w-full border border-base-300 bg-base-100" [class.max-w-2xl]="narrow()" [attr.aria-labelledby]="labelledBy()">
      <ng-content />
    </section>
  `,
})
export class Panel {
  readonly narrow = input(false);
  readonly labelledBy = input<string | null>(null);
}
