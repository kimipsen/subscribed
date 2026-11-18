import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { query } from '../testing/query';
import { SkeletonBlock } from './skeleton-block';

@Component({ imports: [SkeletonBlock], template: `<app-skeleton-block />` })
class Host {}

describe('SkeletonBlock', () => {
  it('renders a daisyUI skeleton that is hidden from assistive technology', () => {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const host = query(fixture, 'app-skeleton-block');
    expect(host.getAttribute('aria-hidden')).toBe('true');
    expect(query(host, 'div').classList.contains('skeleton')).toBe(true);
  });

  it('is a block-level, full-width host with no text or focusable content', () => {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const host = query(fixture, 'app-skeleton-block');
    expect(host.classList.contains('block')).toBe(true);
    expect(host.classList.contains('w-full')).toBe(true);
    expect(host.textContent?.trim()).toBe('');
    expect(host.querySelectorAll('a, button, input, select, textarea, [tabindex]').length).toBe(0);
  });
});
