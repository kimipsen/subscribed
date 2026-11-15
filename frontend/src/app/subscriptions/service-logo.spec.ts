import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ServiceLogo } from './service-logo';

@Component({
  imports: [ServiceLogo],
  template: `<app-service-logo [name]="name()" [logoUrl]="logoUrl()" />`,
})
class Host {
  readonly name = signal('StreamCo');
  readonly logoUrl = signal<string | null>('/logos/streamco.svg');
}

describe('ServiceLogo', () => {
  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    return { fixture, root, img: () => root.querySelector('img'), letter: () => root.querySelector('span[aria-hidden="true"]') };
  }

  it('renders a decorative image when a URL is set', () => {
    const { img, letter } = render();
    expect(img()?.getAttribute('src')).toBe('/logos/streamco.svg');
    expect(img()?.getAttribute('alt')).toBe('');
    expect(img()?.getAttribute('loading')).toBe('lazy');
    expect(letter()).toBeNull();
  });

  it('falls back to the initial letter when the image fails to load', () => {
    const { fixture, img, letter } = render();
    img()?.dispatchEvent(new Event('error'));
    fixture.detectChanges();
    expect(img()).toBeNull();
    expect(letter()?.textContent?.trim()).toBe('S');
  });

  it('retries when the URL changes after a failure', () => {
    const { fixture, img } = render();
    img()?.dispatchEvent(new Event('error'));
    fixture.detectChanges();
    fixture.componentInstance.logoUrl.set('/logos/other.svg');
    fixture.detectChanges();
    expect(img()?.getAttribute('src')).toBe('/logos/other.svg');
  });

  it('shows the initial letter for a null URL, hidden from assistive technology', () => {
    const { fixture, img, letter } = render();
    fixture.componentInstance.logoUrl.set(null);
    fixture.detectChanges();
    expect(img()).toBeNull();
    expect(letter()?.getAttribute('aria-hidden')).toBe('true');
    expect(letter()?.textContent?.trim()).toBe('S');
  });

  it.each([
    ['', '?'],
    ['   ', '?'],
    ['netflix', 'N'],
    ['  élan', 'É'],
    ['🎵 Music', '🎵'],
    ['Забава', 'З'],
    ['日本', '日'],
  ])('uses %j as the letter %j', (name, expected) => {
    const { fixture, letter } = render();
    fixture.componentInstance.logoUrl.set(null);
    fixture.componentInstance.name.set(name);
    fixture.detectChanges();
    expect(letter()?.textContent?.trim()).toBe(expected);
  });
});
