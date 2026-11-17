import { HttpErrorResponse } from '@angular/common/http';
import { Component, Injector, afterNextRender, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';
import { LanguageService, Message, MessageKey } from '../i18n/i18n';
import { TranslatePipe } from '../i18n/translate.pipe';
import { SessionService } from '../session';
import { FormField } from '../ui/form-field';
import { Notice } from '../ui/notice';
import { Panel } from '../ui/panel';
import { buildCategories } from './categories';
import { optionText } from './payment-option-text';
import { PaymentOptionsService } from './payment-options.service';
import { parseAmountInput, toAmountInput, decimalSeparator, formatMoney } from './money';
import { ServiceLogo } from './service-logo';
import { wallTimeDate } from './time-zone';
import { SessionUnavailable } from './session-unavailable';
import {
  AddSubscriptionBody,
  ApiProblem,
  CadenceUnit,
  Catalog,
  CatalogPlan,
  CatalogService,
  PaymentOption,
  SubscriptionDetail,
  SubscriptionOptions,
  SubscriptionOverview,
  UpdateSubscriptionBody,
} from './subscription.models';
import { SubscriptionsService } from './subscriptions.service';
import { normalizeUnsubscribeUrl } from './unsubscribe-url';

export type CadencePreset = CadenceUnit | 'custom';

const FIELD_MESSAGES: Readonly<Record<string, MessageKey>> = {
  name: 'subscriptions.error.name',
  category: 'subscriptions.error.category',
  notes: 'subscriptions.error.notes',
  currency: 'subscriptions.error.currency',
  startDate: 'subscriptions.error.startDate',
  endDate: 'subscriptions.error.endDate',
  amount: 'subscriptions.error.amount',
  cadenceUnit: 'subscriptions.error.cadenceUnit',
  cadenceCount: 'subscriptions.error.cadenceCount',
  catalogId: 'subscriptions.error.catalogId',
  paymentOptionId: 'subscriptions.error.paymentOptionId',
  paymentOption: 'subscriptions.error.paymentOptionEnded',
  unsubscribeUrl: 'subscriptions.error.unsubscribeUrl',
  cancelBy: 'subscriptions.error.cancelBy',
};

const MAX_PICKER_RESULTS = 8;

/** Lowercase with diacritics removed, so "cafe" finds "Café". */
export function foldForSearch(value: string): string {
  return value.normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase();
}

/**
 * The plan a pick prefills. Order is significant: the first plan in the household's default currency, else the first plan of the
 * catalog's default market, else the first plan; null when the service has no plans.
 */
export function defaultPlan(service: CatalogService, defaultCurrency: string | null, defaultCountry: string): CatalogPlan | null {
  return (
    (defaultCurrency ? service.plans.find((p) => p.currency === defaultCurrency) : undefined) ??
    service.plans.find((p) => p.country === defaultCountry) ??
    service.plans[0] ??
    null
  );
}

export function hasControlCharacters(value: string, allowLineFeed = false): boolean {
  return Array.from(value).some((character) => {
    const code = character.codePointAt(0) ?? 0;
    return (code < 32 || code === 127) && !(allowLineFeed && code === 10);
  });
}

/** CRLF and CR become LF, then trim: the same normalization the server applies to notes. */
export function normalizeNotes(value: string): string {
  return value.replace(/\r\n?/g, '\n').trim();
}

@Component({
  selector: 'app-subscription-form-page',
  imports: [FormsModule, RouterLink, TranslatePipe, SessionUnavailable, ServiceLogo, FormField, Notice, Panel],
  templateUrl: './subscription-form-page.html',
})
export class SubscriptionFormPage {
  readonly i18n = inject(LanguageService);
  readonly session = inject(SessionService);
  private readonly service = inject(SubscriptionsService);
  private readonly paymentOptionsService = inject(PaymentOptionsService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  /** The route parameter is named `id`; its presence is what makes this the edit form. */
  readonly id = this.route.snapshot.paramMap.get('id');
  readonly editing = this.id !== null;

  readonly options = signal<SubscriptionOptions | null>(null);
  readonly detail = signal<SubscriptionDetail | null>(null);
  /** Only for category suggestions. Set after a successful load and never on failure, so a retry refetches it. */
  readonly overview = signal<SubscriptionOverview | null>(null);
  /** null until loaded, and after a failed load: the picker is then hidden and free-form entry is the only path (R3.2). */
  readonly catalog = signal<Catalog | null>(null);
  readonly catalogFailed = signal(false);
  /** The household's payment options: for the initial price when adding, for the current price when editing. A failed load leaves just "None". */
  readonly paymentOptions = signal<PaymentOption[]>([]);
  /** The household's IANA zone, named in the cancel-by hint. null until loaded and after a failed load (the hint then names no zone). */
  readonly householdZone = signal<string | null>(null);
  /** True after a failed options load. The edit form then hides the select and never sends `paymentOption`, so a current option is never cleared by accident. */
  readonly paymentOptionsFailed = signal(false);
  /** The option the loaded subscription's current price had when the form was filled; the baseline the edit form diffs against. */
  readonly initialPaymentOptionId = signal('');
  readonly selectableOptions = computed(() => this.paymentOptions().filter((o) => !o.removed));
  /** Edit mode: whether the select is offered. Never for an ended subscription, nor when the options could not be loaded. */
  readonly showPaymentOption = computed(() => {
    const detail = this.detail();
    return this.editing && detail !== null && detail.status !== 'ended' && !this.paymentOptionsFailed();
  });
  /**
   * What the edit select offers: the non-removed options, plus the current price's own option when it has since been removed, shown
   * disabled as "(removed)". Saving keeps it unless another is picked.
   */
  readonly paymentChoices = computed(() => {
    const keep = this.initialPaymentOptionId();
    return this.paymentOptions().filter((o) => !o.removed || o.id === keep);
  });
  readonly optionText = optionText;
  readonly search = signal('');
  readonly selectedService = signal<CatalogService | null>(null);
  readonly selectedPlan = signal<CatalogPlan | null>(null);
  /** Whether the price fields currently hold values taken from a catalog plan. */
  private pricePrefilled = false;
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  readonly notFound = signal(false);
  readonly saving = signal(false);
  readonly conflict = signal(false);
  readonly problem = signal<Message | null>(null);
  readonly traceId = signal<string | undefined>(undefined);
  readonly errors = signal<Readonly<Record<string, Message>>>({});

  name = '';
  category = '';
  notes = '';
  unsubscribeUrl = '';
  currency = '';
  startDate = '';
  endDate = '';
  /** Local wall time in the household zone (`yyyy-MM-ddTHH:mm`, the `datetime-local` value shape), or ''. */
  cancelBy = '';
  amountText = '';
  cadencePreset: CadencePreset = 'month';
  customCount = '2';
  customUnit: CadenceUnit = 'month';
  /** The linked catalog service: null for a free-form add; when editing, the stored link (kept unless removed). */
  catalogId: string | null = null;
  /**
   * The payment option select: '' means none. Adding sends it with the initial price. Editing sends it only when it differs from
   * `initialPaymentOptionId`, so an untouched form never changes a price entry.
   */
  paymentOptionId = '';

  /** The canonical string behind `amountText` while it is valid; null when the member's text is not a valid amount. */
  private amountCanonical: string | null = null;
  private started = false;
  private paymentOptionsLoaded = false;

  readonly minorUnit = computed(() => this.options()?.currencies.find((c) => c.code === this.currency)?.minorUnit ?? 2);
  readonly separator = computed(() => decimalSeparator(this.i18n.dateLocale()));
  readonly amountExample = computed(() => toAmountInput(this.minorUnit() === 0 ? '129' : '129.00', this.i18n.dateLocale()));
  /** Distinct existing categories (matched by case and whitespace) for the datalist; free text stays allowed. */
  readonly categorySuggestions = computed(() => buildCategories(this.overview()?.subscriptions ?? []).options.map((option) => option.label));
  readonly currencyLabels = computed(() => {
    const names = new Intl.DisplayNames([this.i18n.dateLocale()], { type: 'currency' });
    return (this.options()?.currencies ?? []).map((c) => ({ code: c.code, label: `${c.code} — ${names.of(c.code) ?? c.code}` }));
  });

  /** The services matching the search text, sorted by name for the active language, at most eight. */
  readonly results = computed(() => {
    const needle = foldForSearch(this.search().trim());
    const locale = this.i18n.dateLocale();
    return (this.catalog()?.services ?? [])
      .filter((service) => foldForSearch(service.name).includes(needle))
      .sort((a, b) => a.name.localeCompare(b.name, locale))
      .slice(0, MAX_PICKER_RESULTS);
  });

  readonly resultCount = computed(() => String(this.results().length));

  /** The plans of the picked service in its default market, for the plan select (shown only when there is a choice). */
  readonly planChoices = computed(() => {
    const service = this.selectedService();
    const plan = this.selectedPlan();
    if (!service || !plan) return [];
    return service.plans.filter((p) => p.country === plan.country);
  });

  /** An announced price change is shown only from its effective date: a past date is stale data and stays hidden. */
  readonly upcomingChange = computed(() => {
    const change = this.selectedPlan()?.upcomingChange;
    return change && change.effectiveFrom >= new Date().toISOString().slice(0, 10) ? change : null;
  });

  /** The service the stored link points at (edit mode); null when the entry has since left the catalog. */
  readonly linkedService = computed(() => {
    const id = this.catalogId;
    return id ? (this.catalog()?.services.find((s) => s.id === id) ?? null) : null;
  });

  constructor() {
    effect(() => {
      if (this.session.current().state === 'member' && !this.started) {
        this.started = true;
        untracked(() => this.load());
      }
    });
    // A valid amount is re-rendered from its canonical string in the new language. An invalid one stays as typed.
    effect(() => {
      const locale = this.i18n.dateLocale();
      untracked(() => {
        if (this.amountCanonical !== null) this.amountText = toAmountInput(this.amountCanonical, locale);
      });
    });
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.notFound.set(false);
    this.clearProblems();
    forkJoin({
      options: this.options() ? of(this.options() as SubscriptionOptions) : this.service.options(),
      catalog: this.catalog() ? of(this.catalog()) : this.service.catalog().pipe(catchError(() => of(null))),
      detail: this.id ? this.service.detail(this.id) : of(null),
      // Suggestions are an enhancement, never a blocker: a failed overview just means no suggestions.
      overview: this.overview() ? of(this.overview()) : this.service.overview().pipe(catchError(() => of(null))),
      // Never a blocker: a failed load leaves the add form with just "None" and hides the edit form's select. Reused after a success.
      paymentOptions: this.paymentOptionsLoaded ? of(null) : this.paymentOptionsService.list().pipe(catchError(() => of(null))),
      // The zone only labels the cancel-by field: the server does the conversion, so a failed load is not a blocker.
      zone: this.householdZone() ? of(this.householdZone()) : this.service.householdZone().pipe(catchError(() => of(null))),
    }).subscribe({
      next: ({ options, catalog, detail, overview, paymentOptions, zone }) => {
        if (zone) this.householdZone.set(zone);
        this.options.set(options);
        if (overview) this.overview.set(overview);
        if (paymentOptions) {
          this.paymentOptions.set(paymentOptions);
          this.paymentOptionsLoaded = true;
        }

        this.paymentOptionsFailed.set(!this.paymentOptionsLoaded);
        this.catalog.set(catalog);
        this.catalogFailed.set(catalog === null);
        this.detail.set(detail);
        if (detail) this.fill(detail);
        else if (!this.currency) this.currency = options.defaultCurrency ?? '';
        this.loading.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.loading.set(false);
        if (error.status === 404) this.notFound.set(true);
        else if (error.status === 403) this.problem.set({ key: 'subscriptions.form.forbidden' });
        else this.loadFailed.set(true);
        this.traceId.set((error.error as ApiProblem | null)?.traceId);
      },
    });
  }

  reloadLatest(): void {
    this.load();
  }

  onSearchInput(text: string): void {
    this.search.set(text);
  }

  /** Picks a service: prefills name, category and the default plan's price fields once. Afterwards every field is a normal control. */
  pickService(service: CatalogService): void {
    this.selectedService.set(service);
    this.catalogId = service.id;
    this.name = service.name;
    this.category = service.category;
    const plan = defaultPlan(service, this.options()?.defaultCurrency ?? null, this.catalog()?.defaultCountry ?? 'DK');
    this.selectedPlan.set(plan);
    if (plan) this.applyPlan(plan);
    else this.clearPrefilledPrice();
    this.setError('name', null);
    this.setError('category', null);
  }

  /** The plan select: another plan of the same service replaces the previous plan's prefill. */
  choosePlan(index: string): void {
    const plan = this.planChoices()[Number(index)];
    if (!plan) return;
    this.selectedPlan.set(plan);
    this.applyPlan(plan);
  }

  /** Drops the link and the "prefilled" hints, but keeps every value the member has typed. */
  unlink(): void {
    this.catalogId = null;
    this.selectedService.set(null);
    this.selectedPlan.set(null);
    this.pricePrefilled = false;
    this.setError('catalogId', null);
  }

  planLabel(plan: CatalogPlan): string {
    return `${plan.name} — ${formatMoney(plan.amount, plan.currency, plan.minorUnit, this.i18n.dateLocale())}`;
  }

  formatDay(value: string): string {
    return new Intl.DateTimeFormat(this.i18n.dateLocale(), { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(`${value}T00:00:00Z`));
  }

  money(amount: string, plan: CatalogPlan): string {
    return formatMoney(amount, plan.currency, plan.minorUnit, this.i18n.dateLocale());
  }

  private applyPlan(plan: CatalogPlan): void {
    this.currency = plan.currency;
    this.amountCanonical = plan.amount;
    this.amountText = toAmountInput(plan.amount, this.i18n.dateLocale());
    if (plan.cadenceCount === 1) {
      this.cadencePreset = plan.cadenceUnit;
    } else {
      this.cadencePreset = 'custom';
      this.customCount = String(plan.cadenceCount);
      this.customUnit = plan.cadenceUnit;
    }
    this.pricePrefilled = true;
    this.setError('amount', null);
    this.setError('currency', null);
  }

  /** A service without plans prefills only name and category: a price left over from an earlier pick must not stay. */
  private clearPrefilledPrice(): void {
    if (!this.pricePrefilled) return;
    this.amountText = '';
    this.amountCanonical = null;
    this.pricePrefilled = false;
  }

  onAmountInput(text: string): void {
    this.amountText = text;
    this.amountCanonical = parseAmountInput(text, this.i18n.dateLocale(), this.minorUnit());
    if (this.errors()['amount'] && this.amountCanonical !== null) this.setError('amount', null);
  }

  err(field: string): Message | null {
    return this.errors()[field] ?? null;
  }

  describedBy(field: string, hint?: string): string | null {
    return [this.err(field) ? `${field}-error` : '', hint ?? ''].filter(Boolean).join(' ') || null;
  }

  save(): void {
    if (this.saving()) return;
    this.clearProblems();
    if (!this.validate()) {
      this.focusLater('[aria-invalid="true"]');
      return;
    }
    this.saving.set(true);
    const request = this.editing ? this.service.update(this.id as string, this.detail()?.version ?? 0, this.updateBody()) : this.service.add(this.addBody());
    request.subscribe({
      next: (saved) => {
        this.saving.set(false);
        void this.router.navigate(['/subscriptions', saved.id]);
      },
      error: (error: HttpErrorResponse) => {
        this.saving.set(false);
        this.handleSaveError(error);
      },
    });
  }

  private handleSaveError(error: HttpErrorResponse): void {
    const body = error.error as ApiProblem | null;
    this.traceId.set(body?.traceId);
    if (error.status === 412) {
      this.conflict.set(true);
    } else if (error.status === 403) {
      this.problem.set({ key: 'subscriptions.form.forbidden' });
    } else if (error.status === 404) {
      this.notFound.set(true);
    } else if (error.status === 400 && body?.errors && this.mapServerErrors(body.errors)) {
      this.focusLater('[aria-invalid="true"]');
    } else {
      this.problem.set({ key: 'subscriptions.form.saveFailed' });
    }
  }

  /** Maps known server error keys to localized messages next to their fields. Unknown keys are not shown (no backend English). */
  private mapServerErrors(errors: Record<string, string[]>): boolean {
    const mapped: Record<string, Message> = {};
    for (const key of Object.keys(errors)) {
      const messageKey = Object.hasOwn(FIELD_MESSAGES, key) ? FIELD_MESSAGES[key] : undefined;
      if (messageKey) mapped[key] = this.fieldMessage(messageKey);
    }
    this.errors.set(mapped);
    return Object.keys(mapped).length > 0;
  }

  private fieldMessage(key: MessageKey): Message {
    return key === 'subscriptions.error.amount' ? { key, params: { example: this.amountExample() } } : { key };
  }

  private setError(field: string, message: Message | null): void {
    const next = { ...this.errors() };
    if (message) next[field] = message;
    else delete next[field];
    this.errors.set(next);
  }

  /** Focus after the next render: the invalid field's attributes are not in the DOM until change detection has run. */
  private focusLater(selector: string): void {
    afterNextRender(() => document.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }

  private clearProblems(): void {
    this.problem.set(null);
    this.conflict.set(false);
    this.traceId.set(undefined);
    this.errors.set({});
  }

  private fill(detail: SubscriptionDetail): void {
    this.name = detail.name;
    this.category = detail.category ?? '';
    this.notes = detail.notes ?? '';
    this.unsubscribeUrl = detail.unsubscribeUrl ?? '';
    this.currency = detail.currency;
    this.startDate = detail.startDate;
    this.endDate = detail.endDate ?? '';
    this.cancelBy = detail.cancelBy ?? '';
    this.catalogId = detail.catalogId;
    this.paymentOptionId = detail.currentPrice.paymentOptionId ?? '';
    this.initialPaymentOptionId.set(this.paymentOptionId);
  }

  /** The client-side rules mirror the server's field rules and, when editing, the invariants the page can see. */
  private validate(): boolean {
    const errors: Record<string, Message> = {};
    const fail = (field: string, key: MessageKey) => (errors[field] = this.fieldMessage(key));
    const name = this.name.trim();
    if (name.length < 1 || name.length > 80 || hasControlCharacters(name)) fail('name', 'subscriptions.error.name');
    const category = this.category.trim();
    if (category.length > 40 || hasControlCharacters(category)) fail('category', 'subscriptions.error.category');
    const notes = normalizeNotes(this.notes);
    if (notes.length > 1000 || hasControlCharacters(notes, true)) fail('notes', 'subscriptions.error.notes');
    if (normalizeUnsubscribeUrl(this.unsubscribeUrl) === undefined) fail('unsubscribeUrl', 'subscriptions.error.unsubscribeUrl');
    if (!this.options()?.currencies.some((c) => c.code === this.currency)) fail('currency', 'subscriptions.error.currency');
    const validDate = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value));
    if (!validDate(this.startDate)) fail('startDate', 'subscriptions.error.startDate');
    if (this.endDate && (!validDate(this.endDate) || (validDate(this.startDate) && this.endDate < this.startDate))) {
      fail('endDate', 'subscriptions.error.endDate');
    }
    if (this.cancelBy) {
      // Early feedback only; the server is the authority (it compares the local date in the household zone with the start date).
      const day = wallTimeDate(this.cancelBy);
      if (day === null || (validDate(this.startDate) && day < this.startDate)) fail('cancelBy', 'subscriptions.error.cancelBy');
    }
    const entries = this.detail()?.priceHistory ?? [];
    if (this.editing && validDate(this.startDate) && !errors['startDate'] &&
        entries.some((e) => !e.initial && e.effectiveFrom <= this.startDate)) {
      fail('startDate', 'subscriptions.error.startAfterEntries');
    }
    if (this.editing && this.endDate && !errors['endDate'] && entries.some((e) => e.effectiveFrom > this.endDate)) {
      fail('endDate', 'subscriptions.error.endBeforeEntries');
    }
    if (!this.editing) {
      this.amountCanonical = parseAmountInput(this.amountText, this.i18n.dateLocale(), this.minorUnit());
      if (this.amountCanonical === null) fail('amount', 'subscriptions.error.amount');
      if (this.cadencePreset === 'custom' && !/^(?:[1-9]|[1-9][0-9])$/.test(this.customCount.trim())) {
        fail('cadenceCount', 'subscriptions.error.cadenceCount');
      }
    }
    this.errors.set(errors);
    return Object.keys(errors).length === 0;
  }

  private cadence(): { cadenceUnit: CadenceUnit; cadenceCount: number } {
    return this.cadencePreset === 'custom' ?
      { cadenceUnit: this.customUnit, cadenceCount: Number(this.customCount.trim()) } :
      { cadenceUnit: this.cadencePreset, cadenceCount: 1 };
  }

  private details(): UpdateSubscriptionBody {
    return {
      name: this.name.trim(),
      category: this.category.trim() || null,
      notes: normalizeNotes(this.notes) || null,
      currency: this.currency,
      startDate: this.startDate,
      endDate: this.endDate || null,
      catalogId: this.catalogId,
      unsubscribeUrl: normalizeUnsubscribeUrl(this.unsubscribeUrl) ?? null,
      cancelBy: this.cancelBy || null,
    };
  }

  /** Edit sends all nine keys, with explicit nulls (D-10), plus `paymentOption` only when the member changed it. */
  private updateBody(): UpdateSubscriptionBody {
    const changed = this.showPaymentOption() && this.paymentOptionId !== this.initialPaymentOptionId();
    return changed ? { ...this.details(), paymentOption: { id: this.paymentOptionId || null } } : this.details();
  }

  /** The text of an edit-select choice: a removed option is shown disabled as "(removed)". */
  choiceText(option: PaymentOption): string {
    return option.removed ? this.i18n.message({ key: 'paymentOptions.select.removed', params: { name: optionText(option) } }) : optionText(option);
  }

  /** Add sends the canonical decimal string, never the field text (D-7). */
  private addBody(): AddSubscriptionBody {
    return { ...this.details(), amount: this.amountCanonical as string, ...this.cadence(), paymentOptionId: this.paymentOptionId || null };
  }
}
