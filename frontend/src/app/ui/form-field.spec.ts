import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { query } from '../testing/query';
import { FormField } from './form-field';

@Component({
  imports: [FormField],
  template: `
    <app-form-field label="Amount" controlId="amount" [hint]="hint()" [error]="error()" [hintId]="hintId()" [errorId]="errorId()">
      <input id="amount" class="projected" [attr.aria-invalid]="error() ? 'true' : null" [attr.aria-describedby]="describedBy()" />
    </app-form-field>
  `,
})
class Host {
  readonly hint = signal<string | null>('Use a dot');
  readonly error = signal<string | null>(null);
  readonly hintId = signal<string | null>(null);
  readonly errorId = signal<string | null>(null);
  describedBy() {
    return [this.hint() ? this.hintId() ?? 'amount-hint' : null, this.error() ? this.errorId() ?? 'amount-error' : null].filter(Boolean).join(' ') || null;
  }
}

describe('FormField', () => {
  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the visible label for the control id', () => {
    const fixture = render();
    const label = query<HTMLLabelElement>(fixture, 'label');
    expect(label.textContent).toBe('Amount');
    expect(label.htmlFor).toBe('amount');
    expect(query<HTMLInputElement>(fixture, 'input').labels?.[0]).toBe(label);
  });

  it('leaves the projected control untouched: same element, id and wiring, not wrapped', () => {
    const fixture = render();
    const input = query(fixture, 'input.projected');
    expect(input.id).toBe('amount');
    expect(input.parentElement?.getAttribute('data-field')).not.toBeNull();
    expect(input.classList.length).toBe(1);
    expect(input.getAttribute('aria-describedby')).toBe('amount-hint');
    expect(input.hasAttribute('aria-invalid')).toBe(false);
  });

  it('renders the hint with the id the control points at', () => {
    const fixture = render();
    const hint = query(fixture, '[data-field-hint]');
    expect(hint.id).toBe('amount-hint');
    expect(hint.textContent).toBe('Use a dot');
    expect(query(fixture, 'input').getAttribute('aria-describedby')).toBe(hint.id);
  });

  it('renders the error with the id the control points at and wires aria-invalid', () => {
    const fixture = render();
    fixture.componentInstance.error.set('Enter a number');
    fixture.detectChanges();
    const error = query(fixture, '[data-field-error]');
    expect(error.id).toBe('amount-error');
    expect(error.textContent).toBe('Enter a number');
    const input = query(fixture, 'input');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe('amount-hint amount-error');
  });

  it('uses explicit hint and error ids when given', () => {
    const fixture = render();
    fixture.componentInstance.hintId.set('entry-amount-hint');
    fixture.componentInstance.errorId.set('entry-amount-err');
    fixture.componentInstance.error.set('Bad');
    fixture.detectChanges();
    expect(query(fixture, '[data-field-hint]').id).toBe('entry-amount-hint');
    expect(query(fixture, '[data-field-error]').id).toBe('entry-amount-err');
  });

  it('omits the hint and error elements when they are empty or null', () => {
    const fixture = render();
    fixture.componentInstance.hint.set(null);
    fixture.componentInstance.error.set('');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-field-hint]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-field-error]')).toBeNull();
    expect(query(fixture, 'input').hasAttribute('aria-describedby')).toBe(false);
  });

  // Host display choice: `block`, a vertical stack of label, control and messages. `data-field` is on the inner wrapper.
  it('sets the host to block and keeps the spec hook on the inner wrapper', () => {
    const fixture = render();
    const host = query(fixture, 'app-form-field');
    expect(host.classList.contains('block')).toBe(true);
    expect(host.hasAttribute('data-field')).toBe(false);
    expect(query(host, '[data-field]').tagName).toBe('DIV');
  });

  it('styles the error as error text and the hint as muted text with the tested alpha', () => {
    const fixture = render();
    fixture.componentInstance.error.set('Bad');
    fixture.detectChanges();
    expect(query(fixture, '[data-field-error]').classList.contains('text-error')).toBe(true);
    expect(query(fixture, '[data-field-hint]').classList.contains('text-base-content/70')).toBe(true);
  });
});
