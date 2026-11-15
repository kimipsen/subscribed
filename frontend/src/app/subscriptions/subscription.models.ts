// Wire types for /api/subscriptions. Amounts are decimal strings: the browser never does money arithmetic.

export type SubscriptionStatus = 'active' | 'not-started' | 'ended';
export type CadenceUnit = 'week' | 'month' | 'year';

export interface Price {
  entryId: string;
  effectiveFrom: string;
  amount: string;
  cadenceUnit: CadenceUnit;
  cadenceCount: number;
}

export interface SubscriptionSummary {
  id: string;
  name: string;
  category: string | null;
  catalogId: string | null;
  logoUrl: string | null;
  status: SubscriptionStatus;
  startDate: string;
  endDate: string | null;
  currency: string;
  minorUnit: number;
  currentPrice: Price;
  normalizedMonthly: string;
  normalizedYearly: string;
  upcomingPriceChange: boolean;
}

export interface CurrencyTotal {
  currency: string;
  minorUnit: number;
  monthly: string;
  yearly: string;
  activeCount: number;
}

export interface SubscriptionOverview {
  asOf: string;
  subscriptions: SubscriptionSummary[];
  totals: CurrencyTotal[];
}

export interface PriceChange {
  amount: string;
  percent: string | null;
}

export interface HistoryEntry {
  entryId: string;
  initial: boolean;
  effectiveFrom: string;
  effectiveTo: string | null;
  amount: string;
  cadenceUnit: CadenceUnit;
  cadenceCount: number;
  normalizedMonthly: string;
  normalizedYearly: string;
  change: PriceChange | null;
  current: boolean;
  upcoming: boolean;
}

export interface SubscriptionDetail {
  id: string;
  version: number;
  asOf: string;
  name: string;
  category: string | null;
  catalogId: string | null;
  logoUrl: string | null;
  notes: string | null;
  status: SubscriptionStatus;
  startDate: string;
  endDate: string | null;
  currency: string;
  minorUnit: number;
  currentPrice: Price;
  normalizedMonthly: string;
  normalizedYearly: string;
  priceHistory: HistoryEntry[];
}

export interface CurrencyOption {
  code: string;
  minorUnit: number;
}

export interface SubscriptionOptions {
  currencies: CurrencyOption[];
  defaultCurrency: string | null;
}

/** Body of POST /api/subscriptions. */
export interface AddSubscriptionBody {
  name: string;
  category: string | null;
  notes: string | null;
  currency: string;
  startDate: string;
  endDate: string | null;
  amount: string;
  cadenceUnit: CadenceUnit;
  cadenceCount: number;
  /** The picked catalog service, or an explicit null for a free-form add. */
  catalogId: string | null;
}

/** Body of PUT /api/subscriptions/{id}: always all seven keys, with explicit nulls (D-10). An omitted or null catalogId unlinks. */
export interface UpdateSubscriptionBody {
  name: string;
  category: string | null;
  notes: string | null;
  currency: string;
  startDate: string;
  endDate: string | null;
  catalogId: string | null;
}

/** Body of POST /prices and PUT /prices/{entryId}: always all four keys. */
export interface PriceEntryBody {
  effectiveFrom: string;
  amount: string;
  cadenceUnit: CadenceUnit;
  cadenceCount: number;
}

export interface CatalogUpcomingChange {
  amount: string;
  effectiveFrom: string;
}

export interface CatalogPlan {
  country: string;
  name: string;
  currency: string;
  minorUnit: number;
  amount: string;
  cadenceUnit: CadenceUnit;
  cadenceCount: number;
  upcomingChange: CatalogUpcomingChange | null;
}

export interface CatalogService {
  id: string;
  name: string;
  category: string;
  logoUrl: string | null;
  lastVerified: string;
  plans: CatalogPlan[];
}

/** GET /api/subscriptions/catalog. Service and plan order is the server's file order. */
export interface Catalog {
  services: CatalogService[];
  defaultCountry: string;
}

export interface ApiProblem {
  type?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  currentVersion?: number;
}

/** Widens every string so a JSON fixture (whose strings are plain `string`s) can be checked against a DTO with literal unions. */
export type Wire<T> = T extends string ? string : T extends (infer U)[] ? Wire<U>[] : T extends object ? { [K in keyof T]: Wire<T[K]> } : T;
