import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * A notice banner: a daisyUI warning alert. The inner element carries the live-region role (`alert` for something the user
 * must hear now, `status` for a polite update); the host never carries a role. The host is `block` for the same reason as
 * `Panel`. The inner element is a `div`, not an `<output>`: `role="status"` gives the same live-region semantics.
 */
@Component({
  selector: 'app-notice',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block mb-4' },
  template: `
    <div class="alert alert-warning" [attr.role]="role()">
      <div class="min-w-0 wrap-anywhere"><ng-content /></div>
    </div>
  `,
})
export class Notice {
  readonly role = input<'alert' | 'status'>('alert');
}
