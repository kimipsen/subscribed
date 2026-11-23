import { HttpErrorResponse } from '@angular/common/http';
import { NgTemplateOutlet } from '@angular/common';
import { Component, Injector, afterNextRender, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { LanguageService, Message, MessageKey } from '../i18n/i18n';
import { TranslatePipe } from '../i18n/translate.pipe';
import { SessionService } from '../session';
import { focusVisible } from '../ui/focus-visible-target';
import { PaymentOptionLogo } from './payment-option-logo';
import { optionText } from './payment-option-text';
import { PaymentOptionsService } from './payment-options.service';
import { PeriodSelector } from './period-selector';
import { PERIOD_TODAY, PeriodService, parsePeriodKey, periodTotalMessage } from './period';
import { formatMoney, formatPercentChange, formatSignedMoney, parseAmountInput, toAmountInput, decimalSeparator } from './money';
import { ServiceLogo } from './service-logo';
import { SessionUnavailable } from './session-unavailable';
import { cadenceMessage } from './subscription-overview';
import { CadencePreset } from './subscription-form-page';
import {
  ApiProblem,
  CadenceUnit,
  HistoryEntry,
  PauseBody,
  PausePeriod,
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
import { SkeletonBlock } from '../ui/skeleton-block';

type EntryForm = { mode: 'add' } | { mode: 'correct'; entry: HistoryEntry };
type PauseForm = { mode: 'add' } | { mode: 'edit'; period: PausePeriod };
type Area = 'entry' | 'pause' | 'end' | 'other';

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

/**
 * The server's field errors are English sentences; the page shows its own localized text for the ones it knows, picked by the sentence
 * (the API sends no codes). A sentence it does not know maps to nothing, and the page falls back to the generic failure.
 */
const PAUSE_SERVER_ERRORS: Readonly<Record<string, Readonly<Record<string, MessageKey>>>> = {
  pauseDate: {
    'The pause date cannot be in the future.': 'pauses.error.pauseFuture',
    'The pause overlaps another pause.': 'pauses.error.overlap',
    'A date in yyyy-MM-dd format is required.': 'pauses.error.malformed',
    'The date must be in yyyy-MM-dd format.': 'pauses.error.malformed',
  },
  resumeDate: {
    'The resume date cannot be in the future.': 'pauses.error.resumeFuture',
    'The resume date must be after the pause date.': 'pauses.error.resumeNotAfter',
    'Only the latest pause can be open.': 'pauses.error.onlyLatestOpen',
    'The date must be in yyyy-MM-dd format.': 'pauses.error.malformed',
  },
};

const isDate = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value));

@Component({
  selector: 'app-subscription-detail-page',
  imports: [FormsModule, NgTemplateOutlet, RouterLink, TranslatePipe, SessionUnavailable, ServiceLogo, PaymentOptionLogo, FormField, Notice, Panel, PeriodSelector, SkeletonBlock, StatusBadge],
  templateUrl: './subscription-detail-page.html',
})
export class SubscriptionDetailPage {
  readonly i18n = inject(LanguageService);
  readonly session = inject(SessionService);
  private readonly service = inject(SubscriptionsService);
  private readonly paymentOptionsService = inject(PaymentOptionsService);
  private readonly period = inject(PeriodService);
  private readonly today = inject(PERIOD_TODAY);
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

  // Pause-history form and its inline confirmation.
  readonly pauseForm = signal<PauseForm | null>(null);
  readonly pauseErrors = signal<Readonly<Record<string, Message>>>({});
  readonly confirmPause = signal<PausePeriod | null>(null);
  pauseDateValue = '';
  resumeDateValue = '';

  // End-date form, and the two inline confirmations.
  readonly endOpen = signal(false);
  readonly endError = signal<Message | null>(null);
  endDateValue = '';
  readonly confirmEntry = signal<HistoryEntry | null>(null);
  readonly confirmRemove = signal(false);

  private started = false;
  private optionsLoaded = false;
  /** The latest detail request wins: a response of an earlier one (an out-of-order answer) must never overwrite the current selection's data. */
  private latest = 0;
  private requestedKey = '';

  /** The heading of the period-total value: for the period the loaded detail is for, so it never disagrees with the figure. */
  readonly totalHeading = computed(() => periodTotalMessage(parsePeriodKey(this.detail()?.period.key) ?? this.period.effective()));

  /** The pauses in chronological order, whatever order the server sent them in. */
  readonly pauses = computed(() => [...(this.detail()?.pauseHistory ?? [])].sort((a, b) => a.pauseDate.localeCompare(b.pauseDate)));

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
    // A new selection (the selector sits on this page) reloads the detail in place, without touching the messages.
    effect(() => {
      const key = this.period.effectiveKey();
      untracked(() => {
        if (this.started && key !== this.requestedKey) this.refresh();
      });
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
    const request = ++this.latest;
    this.requestedKey = this.period.effectiveKey();
    this.service.detail(this.id, this.period.effective()).subscribe({
      next: (detail) => {
        if (request !== this.latest) return;
        this.detail.set(detail);
        this.loading.set(false);
      },
      error: (error: HttpErrorResponse) => {
        if (request !== this.latest) return;
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

  /** The latest date either pause input accepts: today, the API's own UTC date. */
  maxDate(): string {
    return this.today().toISOString().slice(0, 10);
  }

  pauseError(field: string): Message | null {
    return this.pauseErrors()[field] ?? null;
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
    if (form?.mode === 'correct') this.focusLaterKey(`correct-${form.entry.entryId}`);
    else this.focusLater('#add-entry');
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
      this.focusLater('[data-entry-form] [aria-invalid="true"]');
      return;
    }
    const body = this.entryBody(detail, form);
    const request = form.mode === 'add' ?
      this.service.addPrice(detail.id, detail.version, body, this.period.effective()) :
      this.service.correctPrice(detail.id, detail.version, form.entry.entryId, body, this.period.effective());
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
    if (entry) this.focusLaterKey(`remove-${entry.entryId}`);
    else this.focusLater('#add-entry');
  }

  removeEntry(): void {
    const detail = this.detail();
    const entry = this.confirmEntry();
    if (!detail || !entry || this.saving()) return;
    this.clearProblems();
    this.run(this.service.removePrice(detail.id, detail.version, entry.entryId, this.period.effective()), 'other', () => this.confirmEntry.set(null));
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

  // ---- pause history ---------------------------------------------------------------------------------

  openPause(form: PauseForm): void {
    this.clearProblems();
    this.confirmPause.set(null);
    if (!this.detail()) return;
    this.pauseForm.set(form);
    this.pauseDateValue = form.mode === 'edit' ? form.period.pauseDate : '';
    this.resumeDateValue = form.mode === 'edit' ? (form.period.resumeDate ?? '') : '';
    this.focusLater('#pause-form-heading');
  }

  closePause(): void {
    const form = this.pauseForm();
    this.pauseForm.set(null);
    this.pauseErrors.set({});
    if (form?.mode === 'edit') this.focusLaterKey(`edit-pause-${form.period.id}`);
    else this.focusLater('#add-pause');
  }

  savePause(): void {
    const detail = this.detail();
    const form = this.pauseForm();
    if (!detail || !form || this.saving()) return;
    this.clearProblems();
    if (!this.validatePause()) {
      this.focusLater('[data-pause-form] [aria-invalid="true"]');
      return;
    }
    // From the page's own fields: an empty resume date is an explicit null (an open pause).
    const body: PauseBody = { pauseDate: this.pauseDateValue, resumeDate: this.resumeDateValue || null };
    const period = this.period.effective();
    const request = form.mode === 'add' ?
      this.service.addPause(detail.id, detail.version, body, period) :
      this.service.changePause(detail.id, detail.version, form.period.id, body, period);
    this.run(request, 'pause', () => {
      this.pauseForm.set(null);
      this.notice.set({ key: form.mode === 'add' ? 'pauses.added' : 'pauses.changed' });
      this.focusLater('#add-pause');
    });
  }

  askRemovePause(period: PausePeriod): void {
    this.clearProblems();
    this.pauseForm.set(null);
    this.confirmPause.set(period);
    this.focusLater('#confirm-pause');
  }

  cancelRemovePause(): void {
    const period = this.confirmPause();
    this.confirmPause.set(null);
    if (period) this.focusLaterKey(`delete-pause-${period.id}`);
    else this.focusLater('#add-pause');
  }

  removePause(): void {
    const detail = this.detail();
    const period = this.confirmPause();
    if (!detail || !period || this.saving()) return;
    this.clearProblems();
    this.run(this.service.removePause(detail.id, detail.version, period.id, this.period.effective()), 'other', () => {
      this.confirmPause.set(null);
      this.notice.set({ key: 'pauses.deleted' });
      this.focusLater('#add-pause');
    });
  }

  /** What the server would refuse on shape or on the date rules that need no other pause; overlap and "only the latest can be open" stay with the server. */
  private validatePause(): boolean {
    const errors: Record<string, Message> = {};
    const max = this.maxDate();
    const pause = this.pauseDateValue;
    const resume = this.resumeDateValue;
    if (!isDate(pause)) errors['pauseDate'] = { key: 'pauses.error.malformed' };
    else if (pause > max) errors['pauseDate'] = { key: 'pauses.error.pauseFuture' };
    if (resume) {
      if (!isDate(resume)) errors['resumeDate'] = { key: 'pauses.error.malformed' };
      else if (resume > max) errors['resumeDate'] = { key: 'pauses.error.resumeFuture' };
      else if (isDate(pause) && resume <= pause) errors['resumeDate'] = { key: 'pauses.error.resumeNotAfter' };
    }
    this.pauseErrors.set(errors);
    return Object.keys(errors).length === 0;
  }

  private mapPauseErrors(errors: Record<string, string[]>): boolean {
    const mapped: Record<string, Message> = {};
    for (const field of Object.keys(PAUSE_SERVER_ERRORS)) {
      const known = PAUSE_SERVER_ERRORS[field];
      const text = Object.hasOwn(errors, field) ? errors[field].find((sentence) => Object.hasOwn(known, sentence)) : undefined;
      if (text) mapped[field] = { key: known[text] };
    }
    this.pauseErrors.set(mapped);
    return Object.keys(mapped).length > 0;
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
    const period = this.period.effective();
    const request = pausing ? this.service.pause(detail.id, detail.version, period) : this.service.resume(detail.id, detail.version, period);
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
    this.run(this.service.update(detail.id, detail.version, this.details(detail, this.endDateValue), this.period.effective()), 'end', () => this.endOpen.set(false));
  }

  reinstate(): void {
    const detail = this.detail();
    if (!detail || this.saving()) return;
    this.clearProblems();
    this.run(this.service.update(detail.id, detail.version, this.details(detail, null), this.period.effective()), 'other', () => undefined);
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
        this.latest++;
        this.detail.set(updated);
        done();
        // The selection changed while the request was in flight: the response is for the old period, so show the current one.
        if (updated.period.key !== this.period.effectiveKey()) this.refresh();
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
      this.focusLater('[data-entry-form] [aria-invalid="true"]');
    } else if (error.status === 400 && area === 'pause' && body?.errors && this.mapPauseErrors(body.errors)) {
      this.focusLater('[data-pause-form] [aria-invalid="true"]');
    } else if (error.status === 400 && area === 'end' && body?.errors && Object.hasOwn(body.errors, 'endDate')) {
      this.endError.set({ key: 'detail.end.error' });
      this.focusLater('#end-date');
    } else {
      this.problem.set({ key: 'subscriptions.form.saveFailed' });
    }
  }

  /** Replaces the detail without touching the messages: a plain `load()` would clear the problem it follows. */
  private refresh(): void {
    const request = ++this.latest;
    this.requestedKey = this.period.effectiveKey();
    this.service.detail(this.id, this.period.effective()).subscribe({
      next: (detail) => {
        if (request === this.latest) this.detail.set(detail);
      },
      error: (error: HttpErrorResponse) => {
        if (request === this.latest && error.status === 404) this.notFound.set(true);
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

  /**
   * Focus after the next render: the element is not in the DOM until change detection has run. Only for elements that exist
   * once (headings, forms, page-level buttons); row actions that are also rendered as card buttons use `focusLaterKey`.
   */
  private focusLater(selector: string): void {
    afterNextRender(() => document.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }

  /** Focuses the visible copy of a row action that exists as both a table button and a card button. */
  private focusLaterKey(key: string): void {
    afterNextRender(() => focusVisible(document, key), { injector: this.injector });
  }

  private clearProblems(): void {
    this.problem.set(null);
    this.notice.set(null);
    this.conflict.set(false);
    this.traceId.set(undefined);
    this.entryErrors.set({});
    this.pauseErrors.set({});
    this.endError.set(null);
  }
}
