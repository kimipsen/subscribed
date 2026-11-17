import { PaymentOptionBrand } from './subscription.models';

export interface PaymentOptionBrandEntry {
  id: PaymentOptionBrand;
  /** Brand names are not translated. */
  name: string;
  /** A file name under `/logos/` whose licence is confirmed, or null: the generic card icon is shown instead. */
  logo: string | null;
}

/** The same ids, in the same order, as the backend `PaymentOptionBrands.Ids` and the suggestions list. */
export const PAYMENT_OPTION_BRANDS: readonly PaymentOptionBrandEntry[] = [
  { id: 'paypal', name: 'PayPal', logo: null },
  { id: 'visa', name: 'Visa', logo: null },
  { id: 'mastercard', name: 'Mastercard', logo: null },
  { id: 'dankort', name: 'Dankort', logo: null },
  { id: 'mobilepay-vipps', name: 'MobilePay / Vipps', logo: null },
];

export const GENERIC_CARD_LOGO = 'generic-card.svg';

export function brandOf(id: string | null | undefined): PaymentOptionBrand | null {
  return PAYMENT_OPTION_BRANDS.find((b) => b.id === id)?.id ?? null;
}

/** The licensed logo file of a brand, or null when there is none (the generic icon applies). */
export function logoFor(brand: PaymentOptionBrand | null): string | null {
  return PAYMENT_OPTION_BRANDS.find((b) => b.id === brand)?.logo ?? null;
}
