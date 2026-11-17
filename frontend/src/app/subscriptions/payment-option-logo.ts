import { Component, computed, input, linkedSignal } from '@angular/core';
import { GENERIC_CARD_LOGO, logoFor } from './payment-option-brands';
import { PaymentOptionBrand } from './subscription.models';

/**
 * A payment option's brand logo, or the generic card icon when the option has no brand, the brand has no licensed logo, or the file
 * fails to load. Decorative (`alt=""`): the option's name is always rendered as adjacent text.
 */
@Component({
  selector: 'app-payment-option-logo',
  host: { class: 'inline-flex flex-none align-middle' },
  template: `<img class="object-contain rounded-sm" [src]="src()" alt="" loading="lazy" [attr.width]="size()" [attr.height]="size()" (error)="failed.set(true)" />`,
})
export class PaymentOptionLogo {
  readonly brand = input<PaymentOptionBrand | null>(null);
  readonly size = input(24);

  /** Set when the brand's logo fails to load; reset whenever the brand changes. */
  readonly failed = linkedSignal<PaymentOptionBrand | null, boolean>({ source: this.brand, computation: () => false });

  readonly src = computed(() => `/logos/${(!this.failed() && logoFor(this.brand())) || GENERIC_CARD_LOGO}`);
}
