import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { PAYMENT_OPTION_BRANDS, PaymentOptionBrandEntry, brandOf, logoFor } from './payment-option-brands';
import { PaymentOptionLogo } from './payment-option-logo';
import { PaymentOptionBrand } from './subscription.models';
import { suggestionListFixture } from './testing/fixtures';

@Component({
  imports: [PaymentOptionLogo],
  template: `<app-payment-option-logo [brand]="brand()" />`,
})
class Host {
  readonly brand = signal<PaymentOptionBrand | null>('visa');
}

describe('PaymentOptionLogo', () => {
  const entries = PAYMENT_OPTION_BRANDS as PaymentOptionBrandEntry[];
  const visa = () => entries.find((b) => b.id === 'visa')!;
  let original: string | null;

  beforeEach(() => (original = visa().logo));
  afterEach(() => (visa().logo = original));

  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    return { fixture, host: fixture.componentInstance, img: () => root.querySelector('img') };
  }

  it('renders the brand\'s licensed file, decorative', () => {
    visa().logo = 'visa.svg';
    const { img } = render();
    expect(img()?.getAttribute('src')).toBe('/logos/visa.svg');
    expect(img()?.getAttribute('alt')).toBe('');
  });

  it('renders the generic card icon for a brand without a licensed logo, decorative', () => {
    const { img } = render();
    expect(img()?.getAttribute('src')).toBe('/logos/generic-card.svg');
    expect(img()?.getAttribute('alt')).toBe('');
  });

  it('renders the generic card icon when there is no brand', () => {
    const { fixture, host, img } = render();
    host.brand.set(null);
    fixture.detectChanges();
    expect(img()?.getAttribute('src')).toBe('/logos/generic-card.svg');
    expect(img()?.getAttribute('alt')).toBe('');
  });

  it('falls back to the generic icon when the logo fails to load, and retries when the brand changes', () => {
    visa().logo = 'visa.svg';
    const { fixture, host, img } = render();
    img()?.dispatchEvent(new Event('error'));
    fixture.detectChanges();
    expect(img()?.getAttribute('src')).toBe('/logos/generic-card.svg');
    expect(img()?.getAttribute('alt')).toBe('');
    host.brand.set('mastercard');
    fixture.detectChanges();
    host.brand.set('visa');
    fixture.detectChanges();
    expect(img()?.getAttribute('src')).toBe('/logos/visa.svg');
  });
});

describe('PAYMENT_OPTION_BRANDS', () => {
  it('has the same ids, in the same order, as the suggestions the API returns', () => {
    expect(PAYMENT_OPTION_BRANDS.map((b) => b.id)).toEqual(suggestionListFixture().suggestions.map((s) => s.id));
    expect(PAYMENT_OPTION_BRANDS.map((b) => b.name)).toEqual(suggestionListFixture().suggestions.map((s) => s.name));
  });

  it('resolves only known ids, never an unknown one', () => {
    expect(brandOf('visa')).toBe('visa');
    expect(brandOf('Visa')).toBeNull();
    expect(brandOf('')).toBeNull();
    expect(brandOf(null)).toBeNull();
    expect(logoFor(null)).toBeNull();
  });
});
