import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { query } from '../testing/query';
import { Panel } from './panel';

@Component({
  imports: [Panel],
  template: `<app-panel [narrow]="narrow()" [labelledBy]="labelledBy()"><h2 id="panel-title">Title</h2><p>Body</p></app-panel>`,
})
class Host {
  readonly narrow = signal(false);
  readonly labelledBy = signal<string | null>(null);
}

describe('Panel', () => {
  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    return fixture;
  }

  it('projects its content into an inner section', () => {
    const fixture = render();
    expect(query(fixture, 'app-panel > section').textContent).toContain('Body');
    expect(query(fixture, 'section #panel-title').textContent).toBe('Title');
  });

  // Host display choice: `block`, so the panel keeps the block layout a plain div had (a host element is inline by default).
  it('sets the host to block and puts the card classes on the inner section', () => {
    const fixture = render();
    expect(query(fixture, 'app-panel').classList.contains('block')).toBe(true);
    const section = query(fixture, 'section');
    expect(section.classList.contains('card')).toBe(true);
    expect(section.classList.contains('bg-base-100')).toBe(true);
    expect(section.hasAttribute('role')).toBe(false);
  });

  it('is wide by default and capped when narrow', () => {
    const fixture = render();
    expect(query(fixture, 'section').classList.contains('max-w-2xl')).toBe(false);
    fixture.componentInstance.narrow.set(true);
    fixture.detectChanges();
    expect(query(fixture, 'section').classList.contains('max-w-2xl')).toBe(true);
  });

  it('sets aria-labelledby on the inner landmark only when asked', () => {
    const fixture = render();
    expect(query(fixture, 'section').hasAttribute('aria-labelledby')).toBe(false);
    expect(query(fixture, 'app-panel').hasAttribute('aria-labelledby')).toBe(false);
    fixture.componentInstance.labelledBy.set('panel-title');
    fixture.detectChanges();
    expect(query(fixture, 'section').getAttribute('aria-labelledby')).toBe('panel-title');
    expect(query(fixture, 'app-panel').hasAttribute('aria-labelledby')).toBe(false);
  });
});
