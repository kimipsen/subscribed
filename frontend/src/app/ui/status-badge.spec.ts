import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { query, queryAll } from '../testing/query';
import { BadgeStatus, StatusBadge } from './status-badge';

@Component({
  imports: [StatusBadge],
  template: `<app-status-badge [status]="status()">{{ text() }}</app-status-badge>`,
})
class Host {
  readonly status = signal<BadgeStatus>('active');
  readonly text = signal('Active');
}

@Component({
  imports: [StatusBadge],
  template: `
    <app-status-badge status="active">A</app-status-badge>
    <app-status-badge status="paused">P</app-status-badge>
    <app-status-badge status="upcoming">U</app-status-badge>
  `,
})
class ListHost {}

const EXPECTED: Record<BadgeStatus, string> = {
  active: 'badge-success',
  paused: 'badge-warning',
  'not-started': 'badge-neutral',
  ended: 'badge-neutral',
  upcoming: 'badge-info',
  current: 'badge-success',
};

describe('StatusBadge', () => {
  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    return fixture;
  }

  it('projects the visible text into the badge', () => {
    const fixture = render();
    expect(query(fixture, '[data-status]').textContent).toBe('Active');
  });

  for (const [status, variant] of Object.entries(EXPECTED) as [BadgeStatus, string][]) {
    it(`maps ${status} to ${variant} and exposes data-status on the inner span`, () => {
      const fixture = render();
      fixture.componentInstance.status.set(status);
      fixture.detectChanges();
      const badge = query(fixture, '[data-status]');
      expect(badge.tagName).toBe('SPAN');
      expect(badge.getAttribute('data-status')).toBe(status);
      expect(badge.classList.contains('badge')).toBe(true);
      expect(badge.classList.contains(variant)).toBe(true);
    });
  }

  it('replaces the variant when the status changes', () => {
    const fixture = render();
    fixture.componentInstance.status.set('paused');
    fixture.detectChanges();
    const badge = query(fixture, '[data-status]');
    expect(badge.classList.contains('badge-warning')).toBe(true);
    expect(badge.classList.contains('badge-success')).toBe(false);
  });

  // Host display choice: `contents`, so the host makes no box. It carries no data-* or role.
  it('makes the host contents and keeps data-* off it', () => {
    const fixture = render();
    const host = query(fixture, 'app-status-badge');
    expect(host.classList.contains('contents')).toBe(true);
    expect(Array.from(host.attributes).some((attribute) => attribute.name.startsWith('data-'))).toBe(false);
    expect(host.hasAttribute('role')).toBe(false);
  });

  it('renders several badges independently', () => {
    const fixture = TestBed.createComponent(ListHost);
    fixture.detectChanges();
    const badges = queryAll(fixture, '[data-status]', { min: 3 });
    expect(badges.map((badge) => badge.getAttribute('data-status'))).toEqual(['active', 'paused', 'upcoming']);
  });
});
