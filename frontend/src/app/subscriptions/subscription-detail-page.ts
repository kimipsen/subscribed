import { HttpErrorResponse } from '@angular/common/http';
import { Component, Injector, afterNextRender, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { LanguageService, Message, MessageKey } from '../i18n/i18n';
import { TranslatePipe } from '../i18n/translate.pipe';
import { SessionService } from '../session';
import { PaymentOptionLogo } from './payment-option-logo';
import { optionText } from './payment-option-text';
import { PaymentOptionsService } from './payment-options.service';
import { formatMoney, formatPercentChange, formatSignedMoney, parseAmountInput, toAmountInput, decimalSeparator } from './money';
import { ServiceLogo } from './service-logo';
import { SessionUnavailable } from './session-unavailable';
import { cadenceMessage } from './subscription-overview';
import { CadencePreset } from './subscription-form-page';
import {
  ApiProblem,
  CadenceUnit,
  HistoryEntry,
  PaymentOption,
  PriceEntryBody,
  SubscriptionDetail,
  SubscriptionStatus,
  UpdateSubscriptionBody,
} from './subscription.models';
import { SubscriptionsService } from './subscriptions.service';
import { formatWallTime } from './time-zone';
import { isHttpsUrl } from './unsubscribe-url';
import { FormField } from '../ui/form-field';
import { Notice } from '../ui/notice';
import { Panel } from '../ui/panel';
import { StatusBadge } from '../ui/status-badge';

type EntryForm = { mode: 'add' } | { mode: 'correct'; entry: HistoryEntry };
type Area = 'entry' | 'end' | 'other';

const STATUS_KEYS: Readonly<Record<SubscriptionStatus, MessageKey>> = {
  active: 'subscriptions.status.active',
  'not-started': 'subscriptions.status.not-started',
  ended: 'subscriptions.status.ended',
  paused: 'subscriptions.status.paused',
};

const ENTRY_ERRORS: Readonly<Record<string, MessageKey>> = {
  effectiveFrom: 'detail.error.effectiveFrom',
  amount: 'subscriptions.error.amount',
  cadenceUnit: 'subscriptions.error.cadenceUnit',
  cadenceCount: 'subscriptions.error.cadenceCount',
  paymentOptionId: 'subscriptions.error.paymentOptionId',
};

const isDate = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value));

@Component({
  selector: 'app-subscription-detail-page',
  imports: [FormsModule, RouterLink, TranslatePipe, SessionUnavailable, ServiceLogo, PaymentOptionLogo, FormField, Notice, Panel, StatusBadge],
  templateUrl: './subscription-detail-page.html',
})
export class SubscriptionDetailPage {
  readonly i18n = inject(LanguageService);
  readonly session = inject(SessionService);
  private readonly service = inject(SubscriptionsService);
  private readonly paymentOptionsService = inject(PaymentOptionsService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  readonly isHttpsUrl = isHttpsUrl;
  readonly id = this.route.snapshot.paramMap.get('id') ?? '';
  readonly detail = signal<SubscriptionDetail | null>(null);
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  readonly notFound = signal(false);
  readonly saving = signal(false);
  readonly conflict = signal(false);
  readonly problem = signal<Message | null>(null);
  readonly notice = signal<Message | null>(null);
  readonly traceId = signal<string | undefined>(undefined);

  /** Every option of the household, removed ones included: a history row resolves its option by id, whatever its state. A failed load leaves it empty. */
  readonly paymentOptions = signal<PaymentOption[]>([]);
  readonly optionText = optionText;
  /** The household's IANA zone, named next to the cancel-by deadline. null until loaded and after a failed load. */
  readonly householdZone = signal<string | null>(null);

  // Price-entry form.
  readonly entryForm = signal<EntryForm | null>(null);
  readonly entryErrors = signal<Readonly<Record<string, Message>>>({});
  entryDate = '';
  entryAmountText = '';
  entryPreset: CadencePreset = 'month';
  entryCustomCount = '2';
  entryCustomUnit: CadenceUnit = 'month';
  /** The option picked on the price-entry form: '' means none. */
  entryOption = '';
  private entryAmountCanonical: string | null = null;

  // End-date form, and the two inline confirmations.
  readonly endOpen = signal(false);
  readonly endError = signal<Message | null>(null);
  endDateValue = '';
  readonly confirmEntry = signal<HistoryEntry | null>(null);
  readonly confirmRemove = signal(false);

  private started = false;
  private optionsLoaded = false;

  readonly separator = computed(() => decimalSeparator(this.i18n.dateLocale()));
  readonly amountExample = computed(() => toAmountInput(this.minorUnit() === 0 ? '129' : '129.00', this.i18n.dateLocale()));
  readonly initialCorrection = computed(() => {
    const form = this.entryForm();
    return form?.mode === 'correct' && form.entry.initial;
  });

  /**
   * What the option select offers: the non-removed options, plus the entry's own option when it has since been removed, shown
   * disabled as "(removed)". Saving keeps it, so correcting a price never silently clears the link.
   */
  readonly entryOptionChoices = computed(() => {
    const form = this.entryForm();
    const keep = form?.mode === 'correct' ? form.entry.paymentOptionId : null;
    return this.paymentOptions().filter((o) => !o.removed || o.id === keep);
  });

  constructor() {
    effect(() => {
      if (this.session.current().state === 'member' && !this.started) {
        this.started = true;
        untracked(() => this.load());
      }
    });
    effect(() => {
      const locale = this.i18n.dateLocale();
      untracked(() => {
        if (this.entryAmountCanonical !== null) this.entryAmountText = toAmountInput(this.entryAmountCanonical, locale);
      });
    });
  }

  minorUnit(): number {
    return this.detail()?.minorUnit ?? 2;
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.notFound.set(false);
    this.clearProblems();
    if (!this.optionsLoaded) this.loadOptions();
    if (!this.householdZone()) this.service.householdZone().subscribe({ next: (zone) => this.householdZone.set(zone || null), error: () => undefined });
    this.service.detail(this.id).subscribe({
      next: (detail) => {
        this.detail.set(detail);
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

  /**
   * Once per page, and again on a retry only if it failed. Options never block the page: a failed request means the history shows no
   * option names and the select offers just "None".
   */
  private loadOptions(): void {
    this.paymentOptionsService.list().subscribe({
      next: (options) => {
        this.paymentOptions.set(options);
        this.optionsLoaded = true;
      },
      error: () => this.paymentOptions.set([]),
    });
  }

  // ---- display helpers -------------------------------------------------------------------------------

  money(amount: string): string {
    return formatMoney(amount, this.detail()?.currency ?? '', this.minorUnit(), this.i18n.dateLocale());
  }

  signed(amount: string): string {
    return formatSignedMoney(amount, this.detail()?.currency ?? '', this.minorUnit(), this.i18n.dateLocale());
  }

  percent(value: string): string {
    return formatPercentChange(value, this.i18n.dateLocale());
  }

  cadence(unit: CadenceUnit, count: number): string {
    return this.i18n.message(cadenceMessage(unit, count));
  }

  statusKey(status: SubscriptionStatus): MessageKey {
    return STATUS_KEYS[status];
  }

  date(value: string): string {
    return new Intl.DateTimeFormat(this.i18n.dateLocale(), { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(`${value}T00:00:00Z`));
  }

  /** The cancel-by deadline as it reads on the household's clock (the server sends it as local wall time in that zone). */
  cancelByText(value: string): string {
    return formatWallTime(value, this.i18n.dateLocale());
  }

  /** The option a price entry points at, removed or not; null for none, or when the list could not be loaded. */
  optionOf(id: string | null): PaymentOption | null {
    return id ? (this.paymentOptions().find((o) => o.id === id) ?? null) : null;
  }

  choiceText(option: PaymentOption): string {
    return option.removed ? this.i18n.message({ key: 'paymentOptions.select.removed', params: { name: optionText(option) } }) : optionText(option);
  }

  entryError(field: string): Message | null {
    return this.entryErrors()[field] ?? null;
  }

  describedBy(field: string, hint?: string): string | null {
    return [this.entryError(field) ? `entry-${field}-error` : '', hint ?? ''].filter(Boolean).join(' ') || null;
  }

  // ---- price entries ---------------------------------------------------------------------------------

  openEntry(form: EntryForm): void {
    this.clearProblems();
    this.confirmEntry.set(null);
    const detail = this.detail();
    if (!detail) return;
    this.entryForm.set(form);
    if (form.mode === 'add') {
      this.entryDate = '';
      this.entryAmountText = '';
      this.entryAmountCanonical = null;
      this.entryPreset = 'month';
      // A new price usually stays on the same option, so it starts from the current one unless that has been removed.
      const current = this.optionOf(detail.currentPrice.paymentOptionId);
      this.entryOption = current && !current.removed ? current.id : '';
    } else {
      const entry = form.entry;
      this.entryOption = entry.paymentOptionId ?? '';
      this.entryDate = entry.effectiveFrom;
      this.entryAmountCanonical = entry.amount;
      this.entryAmountText = toAmountInput(entry.amount, this.i18n.dateLocale());
      if (entry.cadenceCount === 1) {
        this.entryPreset = entry.cadenceUnit;
      } else {
        this.entryPreset = 'custom';
        this.entryCustomCount = String(entry.cadenceCount);
        this.entryCustomUnit = entry.cadenceUnit;
      }
    }
    this.focusLater('#entry-form-heading');
  }

  closeEntry(): void {
    const form = this.entryForm();
    this.entryForm.set(null);
    this.entryErrors.set({});
    this.focusLater(form?.mode === 'correct' ? `#correct-${form.entry.entryId}` : '#add-entry');
  }

  onEntryAmountInput(text: string): void {
    this.entryAmountText = text;
    this.entryAmountCanonical = parseAmountInput(text, this.i18n.dateLocale(), this.minorUnit());
    if (this.entryErrors()['amount'] && this.entryAmountCanonical !== null) {
      const next = { ...this.entryErrors() };
      delete next['amount'];
      this.entryErrors.set(next);
    }
  }

  saveEntry(): void {
    const detail = this.detail();
    const form = this.entryForm();
    if (!detail || !form || this.saving()) return;
    this.clearProblems();
    if (!this.validateEntry(detail, form)) {
      this.focusLater('.entry-form [aria-invalid="true"]');
      return;
    }
    const body = this.entryBody(detail, form);
    const request = form.mode === 'add' ?
      this.service.addPrice(detail.id, detail.version, body) :
      this.service.correctPrice(detail.id, detail.version, form.entry.entryId, body);
    this.run(request, 'entry', () => {
      this.entryForm.set(null);
      this.focusLater('#add-entry');
    });
  }

  askRemoveEntry(entry: HistoryEntry): void {
    this.clearProblems();
    this.entryForm.set(null);
    this.confirmEntry.set(entry);
    this.focusLater('#confirm-entry');
  }

  cancelRemoveEntry(): void {
    const entry = this.confirmEntry();
    this.confirmEntry.set(null);
    this.focusLater(entry ? `#remove-${entry.entryId}` : '#add-entry');
  }

  removeEntry(): void {
    const detail = this.detail();
    const entry = this.confirmEntry();
    if (!detail || !entry || this.saving()) return;
    this.clearProblems();
    this.run(this.service.removePrice(detail.id, detail.version, entry.entryId), 'other', () => this.confirmEntry.set(null));
  }

  /** The body comes from the page's own fields and the loaded entry, never from a form's value (a disabled date control is left out of it). The details PUT (see `details`) carries no option. */
  private entryBody(detail: SubscriptionDetail, form: EntryForm): PriceEntryBody {
    const initial = form.mode === 'correct' && form.entry.initial;
    const cadence = this.entryPreset === 'custom' ?
      { cadenceUnit: this.entryCustomUnit, cadenceCount: Number(this.entryCustomCount.trim()) } :
      { cadenceUnit: this.entryPreset, cadenceCount: 1 };
    return {
      effectiveFrom: initial ? detail.startDate : this.entryDate,
      amount: this.entryAmountCanonical as string,
      ...cadence,
      // Always sent: the price endpoints fully replace the option, so a price-only correction must carry the existing one.
      paymentOptionId: this.entryOption || null,
    };
  }

  private validateEntry(detail: SubscriptionDetail, form: EntryForm): boolean {
    const errors: Record<string, Message> = {};
    const initial = form.mode === 'correct' && form.entry.initial;
    if (!initial) {
      const others = detail.priceHistory.filter((e) => form.mode === 'add' || e.entryId !== form.entry.entryId);
      if (!isDate(this.entryDate) || this.entryDate <= detail.startDate ||
          (detail.endDate !== null && this.entryDate > detail.endDate) ||
          others.some((e) => e.effectiveFrom === this.entryDate)) {
        errors['effectiveFrom'] = { key: 'detail.error.effectiveFrom' };
      }
    }
    this.entryAmountCanonical = parseAmountInput(this.entryAmountText, this.i18n.dateLocale(), this.minorUnit());
    if (this.entryAmountCanonical === null) errors['amount'] = { key: 'subscriptions.error.amount', params: { example: this.amountExample() } };
    if (this.entryPreset === 'custom' && !/^(?:[1-9]|[1-9][0-9])$/.test(this.entryCustomCount.trim())) {
      errors['cadenceCount'] = { key: 'subscriptions.error.cadenceCount' };
    }
    this.entryErrors.set(errors);
    return Object.keys(errors).length === 0;
  }

  // ---- pause and resume ------------------------------------------------------------------------------

  /** Pause is offered on active subscriptions only. Resume follows the flag, so it stays reachable when the dates show `ended` or `not-started`. */
  canPause(detail: SubscriptionDetail): boolean {
    return detail.status === 'active' && !detail.paused;
  }

  pause(): void {
    this.togglePause(true);
  }

  resume(): void {
    this.togglePause(false);
  }

  private togglePause(pausing: boolean): void {
    const detail = this.detail();
    if (!detail || this.saving() || !(pausing ? this.canPause(detail) : detail.paused)) return;
    this.clearProblems();
    const request = pausing ? this.service.pause(detail.id, detail.version) : this.service.resume(detail.id, detail.version);
    this.run(request, 'other', () => {
      this.notice.set({ key: pausing ? 'subscriptions.paused' : 'subscriptions.resumed', params: { name: detail.name } });
      this.focusLater('#pause-toggle');
    });
  }

  // ---- end, reinstate, remove ------------------------------------------------------------------------

  openEnd(): void {
    this.clearProblems();
    this.endOpen.set(true);
    this.endDateValue = '';
    this.focusLater('#end-date');
  }

  closeEnd(): void {
    this.endOpen.set(false);
    this.endError.set(null);
    this.focusLater('#end-open');
  }

  saveEnd(): void {
    const detail = this.detail();
    if (!detail || this.saving()) return;
    this.clearProblems();
    const last = detail.priceHistory.reduce((max, e) => (e.effectiveFrom > max ? e.effectiveFrom : max), detail.startDate);
    if (!isDate(this.endDateValue) || this.endDateValue < detail.startDate || this.endDateValue < last) {
      this.endError.set({ key: 'detail.end.error' });
      this.focusLater('#end-date');
      return;
    }
    this.run(this.service.update(detail.id, detail.version, this.details(detail, this.endDateValue)), 'end', () => this.endOpen.set(false));
  }

  reinstate(): void {
    const detail = this.detail();
    if (!detail || this.saving()) return;
    this.clearProblems();
    this.run(this.service.update(detail.id, detail.version, this.details(detail, null)), 'other', () => undefined);
  }

  askRemove(): void {
    this.clearProblems();
    this.confirmRemove.set(true);
    this.focusLater('#confirm-remove');
  }

  cancelRemove(): void {
    this.confirmRemove.set(false);
    this.focusLater('#remove-open');
  }

  remove(): void {
    const detail = this.detail();
    if (!detail || this.saving()) return;
    this.clearProblems();
    this.saving.set(true);
    this.service.remove(detail.id, detail.version).subscribe({
      next: () => {
        this.saving.set(false);
        this.service.notice.set({ key: 'detail.removed', params: { name: detail.name } });
        void this.router.navigate(['/']);
      },
      error: (error: HttpErrorResponse) => {
        this.saving.set(false);
        this.fail(error, 'other');
      },
    });
  }

  /** All nine PUT keys come from the loaded detail, never from what the page shows: an omitted category or notes would be erased (D-10). */
  private details(detail: SubscriptionDetail, endDate: string | null): UpdateSubscriptionBody {
    return {
      name: detail.name,
      category: detail.category,
      notes: detail.notes,
      currency: detail.currency,
      startDate: detail.startDate,
      endDate,
      catalogId: detail.catalogId,
      unsubscribeUrl: detail.unsubscribeUrl,
      cancelBy: detail.cancelBy,
    };
  }

  // ---- shared plumbing -------------------------------------------------------------------------------

  private run(request: Observable<SubscriptionDetail>, area: Area, done: () => void): void {
    this.saving.set(true);
    request.subscribe({
      next: (updated) => {
        this.saving.set(false);
        this.detail.set(updated);
        done();
      },
      error: (error: HttpErrorResponse) => {
        this.saving.set(false);
        this.fail(error, area);
      },
    });
  }

  private fail(error: HttpErrorResponse, area: Area): void {
    const body = error.error as ApiProblem | null;
    this.traceId.set(body?.traceId);
    if (error.status === 412) {
      this.conflict.set(true);
    } else if (error.status === 403) {
      this.problem.set({ key: 'subscriptions.form.forbidden' });
    } else if (error.status === 404) {
      this.notFound.set(true);
    } else if (error.status === 409) {
      // Only pause and resume answer 409 (the state was already as asked). Show the message and reload the subscription.
      this.problem.set({ key: 'subscriptions.pauseInvalid' });
      this.refresh();
    } else if (error.status === 400 && area === 'entry' && body?.errors && this.mapEntryErrors(body.errors)) {
      this.focusLater('.entry-form [aria-invalid="true"]');
    } else if (error.status === 400 && area === 'end' && body?.errors && Object.hasOwn(body.errors, 'endDate')) {
      this.endError.set({ key: 'detail.end.error' });
      this.focusLater('#end-date');
    } else {
      this.problem.set({ key: 'subscriptions.form.saveFailed' });
    }
  }

  /** Replaces the detail without touching the messages: a plain `load()` would clear the problem it follows. */
  private refresh(): void {
    this.service.detail(this.id).subscribe({
      next: (detail) => this.detail.set(detail),
      error: (error: HttpErrorResponse) => {
        if (error.status === 404) this.notFound.set(true);
      },
    });
  }

  private mapEntryErrors(errors: Record<string, string[]>): boolean {
    const mapped: Record<string, Message> = {};
    for (const key of Object.keys(errors)) {
      const messageKey = Object.hasOwn(ENTRY_ERRORS, key) ? ENTRY_ERRORS[key] : undefined;
      if (messageKey) mapped[key] = messageKey === 'subscriptions.error.amount' ? { key: messageKey, params: { example: this.amountExample() } } : { key: messageKey };
    }
    this.entryErrors.set(mapped);
    return Object.keys(mapped).length > 0;
  }

  /** Focus after the next render: the element is not in the DOM until change detection has run. */
  private focusLater(selector: string): void {
    afterNextRender(() => document.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }

  private clearProblems(): void {
    this.problem.set(null);
    this.notice.set(null);
    this.conflict.set(false);
    this.traceId.set(undefined);
    this.entryErrors.set({});
    this.endError.set(null);
  }
}
