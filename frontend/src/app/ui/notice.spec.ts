import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { query } from '../testing/query';
import { Notice } from './notice';

@Component({
  imports: [Notice],
  template: `
    @if (explicit()) {
      <app-notice [role]="role()">Heads up <a href="/x">link</a></app-notice>
    } @else {
      <app-notice>Default</app-notice>
    }
  `,
})
class Host {
  readonly explicit = signal(true);
  readonly role = signal<'alert' | 'status'>('alert');
}

describe('Notice', () => {
  function render(explicit = true) {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.explicit.set(explicit);
    fixture.detectChanges();
    return fixture;
  }

  it('projects its content, including inline markup, inside the alert', () => {
    const fixture = render();
    const alert = query(fixture, '[role="alert"]');
    expect(alert.textContent).toContain('Heads up');
    expect(query(alert, 'a').getAttribute('href')).toBe('/x');
  });

  it('defaults to role="alert"', () => {
    const fixture = render(false);
    expect(query(fixture, '[role="alert"]').textContent).toContain('Default');
  });

  it('switches the role on the inner element to status', () => {
    const fixture = render();
    fixture.componentInstance.role.set('status');
    fixture.detectChanges();
    expect(query(fixture, '[role="status"]').textContent).toContain('Heads up');
    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeNull();
  });

  // Host display choice: `block`. The host never carries a role; the inner element does.
  it('sets the host to block, never gives it a role, and carries the daisyUI alert classes inside', () => {
    const fixture = render();
    const host = query(fixture, 'app-notice');
    expect(host.classList.contains('block')).toBe(true);
    expect(host.hasAttribute('role')).toBe(false);
    const alert = query(host, '[role]');
    expect(alert.classList.contains('alert')).toBe(true);
    expect(alert.classList.contains('alert-warning')).toBe(true);
  });
});
