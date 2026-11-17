import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** The states a badge can show: subscription statuses plus the price-change markers `upcoming` and `current`. */
export type BadgeStatus = 'active' | 'paused' | 'not-started' | 'ended' | 'upcoming' | 'current';

/**
 * Complete class literals per status. Never build these by concatenation: Tailwind only emits classes it can read as
 * whole strings, and scripts/styles-contract.test.mjs (check 9) rejects a built class name. Only solid daisyUI colour
 * variants from the contrast-tested set are used.
 */
const BADGE_CLASSES: Record<BadgeStatus, string> = {
  active: 'badge badge-success',
  paused: 'badge badge-warning',
  'not-started': 'badge badge-neutral',
  ended: 'badge badge-neutral',
  upcoming: 'badge badge-info',
  current: 'badge badge-success',
};

/**
 * A status pill. The projected content is the visible text; `status` picks the colour and is exposed as `data-status` on
 * the inner span (the spec hook). The host is `contents`, so it generates no box and the badge sits in the line like any
 * inline element; the host carries no `data-*`.
 */
@Component({
  selector: 'app-status-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  template: `<span [class]="classes()" [attr.data-status]="status()"><ng-content /></span>`,
})
export class StatusBadge {
  readonly status = input.required<BadgeStatus>();

  protected classes(): string {
    return BADGE_CLASSES[this.status()];
  }
}
