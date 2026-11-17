import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * A labelled form field: the visible label, the projected control, then an optional hint and error. It does not wrap the
 * native control, so the control stays in the page template with its own `id`, `ngModel`/`formControl` binding,
 * `aria-invalid` and `aria-describedby`; this component only renders what those attributes point at.
 *
 * `controlId` is the control's id (the label's `for`). `hintId` and `errorId` are the ids of the hint and error paragraphs
 * (default `<controlId>-hint` and `<controlId>-error`); a page passes the ids its control's `aria-describedby` names.
 * Hint and error are plain text, empty or null means not rendered. The host is `block` (a stack of label, control and
 * messages), and the spec hook `data-field` sits on the inner wrapper, not on the host.
 */
@Component({
  selector: 'app-form-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block mb-4' },
  template: `
    <div class="flex flex-col gap-1" data-field>
      <label class="font-medium" [for]="controlId()">{{ label() }}</label>
      <ng-content />
      @if (hint()) {
        <p class="m-0 text-sm text-base-content/70 wrap-anywhere" [id]="resolvedHintId()" data-field-hint>{{ hint() }}</p>
      }
      @if (error()) {
        <p class="m-0 text-sm text-error wrap-anywhere" [id]="resolvedErrorId()" data-field-error>{{ error() }}</p>
      }
    </div>
  `,
})
export class FormField {
  readonly label = input.required<string>();
  readonly controlId = input.required<string>();
  readonly hint = input<string | null>(null);
  readonly error = input<string | null>(null);
  readonly hintId = input<string | null>(null);
  readonly errorId = input<string | null>(null);

  protected readonly resolvedHintId = computed(() => this.hintId() ?? `${this.controlId()}-hint`);
  protected readonly resolvedErrorId = computed(() => this.errorId() ?? `${this.controlId()}-error`);
}
