import { HttpErrorResponse } from '@angular/common/http';
import { Component, Injector, afterNextRender, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { LanguageService, Message, MessageKey } from '../i18n/i18n';
import { TranslatePipe } from '../i18n/translate.pipe';
import { SessionService } from '../session';
import { parseAmountInput, toAmountInput, decimalSeparator } from './money';
import { SessionUnavailable } from './session-unavailable';
import {
  AddSubscriptionBody,
  ApiProblem,
  CadenceUnit,
  SubscriptionDetail,
  SubscriptionOptions,
  UpdateSubscriptionBody,
} from './subscription.models';
import { SubscriptionsService } from './subscriptions.service';

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
};

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
  imports: [FormsModule, RouterLink, TranslatePipe, SessionUnavailable],
  templateUrl: './subscription-form-page.html',
})
export class SubscriptionFormPage {
  readonly i18n = inject(LanguageService);
  readonly session = inject(SessionService);
  private readonly service = inject(SubscriptionsService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  /** The route parameter is named `id`; its presence is what makes this the edit form. */
  readonly id = this.route.snapshot.paramMap.get('id');
  readonly editing = this.id !== null;

  readonly options = signal<SubscriptionOptions | null>(null);
  readonly detail = signal<SubscriptionDetail | null>(null);
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
  currency = '';
  startDate = '';
  endDate = '';
  amountText = '';
  cadencePreset: CadencePreset = 'month';
  customCount = '2';
  customUnit: CadenceUnit = 'month';

  /** The canonical string behind `amountText` while it is valid; null when the member's text is not a valid amount. */
  private amountCanonical: string | null = null;
  private started = false;

  readonly minorUnit = computed(() => this.options()?.currencies.find((c) => c.code === this.currency)?.minorUnit ?? 2);
  readonly separator = computed(() => decimalSeparator(this.i18n.dateLocale()));
  readonly amountExample = computed(() => toAmountInput(this.minorUnit() === 0 ? '129' : '129.00', this.i18n.dateLocale()));
  readonly currencyLabels = computed(() => {
    const names = new Intl.DisplayNames([this.i18n.dateLocale()], { type: 'currency' });
    return (this.options()?.currencies ?? []).map((c) => ({ code: c.code, label: `${c.code} — ${names.of(c.code) ?? c.code}` }));
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
      detail: this.id ? this.service.detail(this.id) : of(null),
    }).subscribe({
      next: ({ options, detail }) => {
        this.options.set(options);
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
    this.currency = detail.currency;
    this.startDate = detail.startDate;
    this.endDate = detail.endDate ?? '';
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
    if (!this.options()?.currencies.some((c) => c.code === this.currency)) fail('currency', 'subscriptions.error.currency');
    const validDate = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value));
    if (!validDate(this.startDate)) fail('startDate', 'subscriptions.error.startDate');
    if (this.endDate && (!validDate(this.endDate) || (validDate(this.startDate) && this.endDate < this.startDate))) {
      fail('endDate', 'subscriptions.error.endDate');
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
    };
  }

  /** Edit sends all six keys, with explicit nulls (D-10). */
  private updateBody(): UpdateSubscriptionBody {
    return this.details();
  }

  /** Add sends the canonical decimal string, never the field text (D-7). */
  private addBody(): AddSubscriptionBody {
    return { ...this.details(), amount: this.amountCanonical as string, ...this.cadence() };
  }
}
