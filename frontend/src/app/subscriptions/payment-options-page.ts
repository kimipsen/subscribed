import { HttpErrorResponse } from '@angular/common/http';
import { Component, Injector, afterNextRender, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';
import { LanguageService, Message, MessageKey } from '../i18n/i18n';
import { TranslatePipe } from '../i18n/translate.pipe';
import { SessionService } from '../session';
import { FormField } from '../ui/form-field';
import { Notice } from '../ui/notice';
import { Panel } from '../ui/panel';
import { SkeletonBlock } from '../ui/skeleton-block';
import { PAYMENT_OPTION_BRANDS, brandOf } from './payment-option-brands';
import { PaymentOptionLogo } from './payment-option-logo';
import { OptionField, OptionProblem, checkOptionFields, optionText } from './payment-option-text';
import { PaymentOptionsService } from './payment-options.service';
import { SessionUnavailable } from './session-unavailable';
import { ApiProblem, PaymentOption, PaymentOptionBrand, PaymentOptionSuggestion } from './subscription.models';

type Errors = Readonly<Partial<Record<OptionField, Message>>>;

const PROBLEM_KEYS: Readonly<Record<OptionField, Readonly<Record<OptionProblem, MessageKey>>>> = {
  name: { invalid: 'paymentOptions.error.name', cardNumber: 'paymentOptions.error.cardNumber' },
  label: { invalid: 'paymentOptions.error.label', cardNumber: 'paymentOptions.error.cardNumber' },
  brand: { invalid: 'paymentOptions.error.brand', cardNumber: 'paymentOptions.error.brand' },
};

/** The server's field keys, shown with a localized message (no backend English). The server cannot say why, so these are the general ones. */
const SERVER_KEYS: Readonly<Record<OptionField, MessageKey>> = {
  name: 'paymentOptions.error.name',
  label: 'paymentOptions.error.label',
  brand: 'paymentOptions.error.brand',
};

@Component({
  selector: 'app-payment-options-page',
  imports: [FormsModule, RouterLink, TranslatePipe, SessionUnavailable, PaymentOptionLogo, FormField, Notice, Panel, SkeletonBlock],
  templateUrl: './payment-options-page.html',
})
export class PaymentOptionsPage {
  readonly i18n = inject(LanguageService);
  readonly session = inject(SessionService);
  private readonly service = inject(PaymentOptionsService);
  private readonly injector = inject(Injector);

  /** Removed options are not shown: they stay resolvable on price entries, but are no longer managed here. */
  readonly options = signal<PaymentOption[]>([]);
  readonly suggestions = signal<PaymentOptionSuggestion[]>([]);
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  readonly saving = signal(false);
  readonly conflict = signal(false);
  readonly problem = signal<Message | null>(null);
  readonly notice = signal<Message | null>(null);
  readonly traceId = signal<string | undefined>(undefined);

  // Add form.
  name = '';
  label = '';
  brand: PaymentOptionBrand | null = null;
  readonly errors = signal<Errors>({});

  // Edit form (one option at a time) and the inline removal confirmation.
  readonly editing = signal<PaymentOption | null>(null);
  editName = '';
  editLabel = '';
  editBrand: PaymentOptionBrand | null = null;
  readonly editErrors = signal<Errors>({});
  readonly confirming = signal<PaymentOption | null>(null);

  private started = false;

  readonly text = optionText;
  readonly brands = PAYMENT_OPTION_BRANDS;
  readonly brandOf = brandOf;

  constructor() {
    effect(() => {
      if (this.session.current().state === 'member' && !this.started) {
        this.started = true;
        untracked(() => this.load());
      }
    });
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.clearProblems();
    forkJoin({
      options: this.service.list(),
      // Suggestions are an enhancement, never a blocker: a failed request just means no chips.
      suggestions: this.suggestions().length > 0 ? of(this.suggestions()) : this.service.suggestions().pipe(catchError(() => of([] as PaymentOptionSuggestion[]))),
    }).subscribe({
      next: ({ options, suggestions }) => {
        this.options.set(options.filter((o) => !o.removed));
        this.suggestions.set(suggestions);
        this.loading.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.loading.set(false);
        if (error.status === 403) this.problem.set({ key: 'subscriptions.form.forbidden' });
        else this.loadFailed.set(true);
        this.traceId.set((error.error as ApiProblem | null)?.traceId);
      },
    });
  }

  err(field: OptionField): Message | null {
    return this.errors()[field] ?? null;
  }

  editErr(field: OptionField): Message | null {
    return this.editErrors()[field] ?? null;
  }

  /** A suggestion prefills the name and the brand (its id is the brand id). The member can still change both. */
  pick(suggestion: PaymentOptionSuggestion): void {
    this.name = suggestion.name;
    this.brand = brandOf(suggestion.id);
    this.errors.set({ ...this.errors(), name: undefined });
    this.focusLater('#option-name');
  }

  add(): void {
    if (this.saving()) return;
    this.clearProblems();
    const errors = this.check(this.name, this.label);
    if (errors) {
      this.errors.set(errors);
      this.focusLater('[data-add-form] [aria-invalid="true"]');
      return;
    }
    const name = this.name.trim();
    this.saving.set(true);
    this.service.create({ name, label: this.label.trim() || null, brand: this.brand }).subscribe({
      next: () => {
        this.saving.set(false);
        this.name = '';
        this.label = '';
        this.brand = null;
        this.notice.set({ key: 'paymentOptions.added', params: { name } });
        this.reloadList();
        this.focusLater('#option-name');
      },
      error: (error: HttpErrorResponse) => {
        this.saving.set(false);
        this.fail(error, (errs) => this.errors.set(errs), '[data-add-form] [aria-invalid="true"]');
      },
    });
  }

  startEdit(option: PaymentOption): void {
    this.clearProblems();
    this.confirming.set(null);
    this.editing.set(option);
    this.editName = option.name;
    this.editLabel = option.label ?? '';
    this.editBrand = option.brand;
    this.focusLater('#edit-name');
  }

  cancelEdit(): void {
    const option = this.editing();
    this.editing.set(null);
    this.editErrors.set({});
    this.focusLater(option ? `#edit-${option.id}` : '#option-name');
  }

  saveEdit(): void {
    const option = this.editing();
    if (!option || this.saving()) return;
    this.clearProblems();
    const errors = this.check(this.editName, this.editLabel);
    if (errors) {
      this.editErrors.set(errors);
      this.focusLater('[data-edit-form] [aria-invalid="true"]');
      return;
    }
    const name = this.editName.trim();
    this.saving.set(true);
    this.service.update(option.id, option.version, { name, label: this.editLabel.trim() || null, brand: this.editBrand }).subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(null);
        this.notice.set({ key: 'paymentOptions.saved', params: { name } });
        this.reloadList();
        this.focusLater(`#edit-${option.id}`);
      },
      error: (error: HttpErrorResponse) => {
        this.saving.set(false);
        this.fail(error, (errs) => this.editErrors.set(errs), '[data-edit-form] [aria-invalid="true"]');
      },
    });
  }

  askRemove(option: PaymentOption): void {
    this.clearProblems();
    this.editing.set(null);
    this.confirming.set(option);
    this.focusLater('#confirm-option');
  }

  cancelRemove(): void {
    const option = this.confirming();
    this.confirming.set(null);
    this.focusLater(option ? `#remove-${option.id}` : '#option-name');
  }

  remove(): void {
    const option = this.confirming();
    if (!option || this.saving()) return;
    this.clearProblems();
    this.saving.set(true);
    this.service.remove(option.id, option.version).subscribe({
      next: () => {
        this.saving.set(false);
        this.confirming.set(null);
        this.notice.set({ key: 'paymentOptions.removed', params: { name: option.name } });
        this.reloadList();
        this.focusLater('#option-name');
      },
      error: (error: HttpErrorResponse) => {
        this.saving.set(false);
        this.fail(error, () => undefined, '');
      },
    });
  }

  /** Reloads the options without touching the messages: `load()` would clear the notice that follows a save. */
  private reloadList(): void {
    this.service.list().subscribe({
      next: (options) => this.options.set(options.filter((o) => !o.removed)),
      error: () => this.loadFailed.set(true),
    });
  }

  /** Client-side early feedback only. The server rule is the authority. */
  private check(name: string, label: string): Errors | null {
    const problems = checkOptionFields(name, label);
    const errors: Partial<Record<OptionField, Message>> = {};
    for (const field of ['name', 'label'] as const) {
      const problem = problems[field];
      if (problem) errors[field] = { key: PROBLEM_KEYS[field][problem] };
    }
    return Object.keys(errors).length > 0 ? errors : null;
  }

  private fail(error: HttpErrorResponse, show: (errors: Errors) => void, focus: string): void {
    const body = error.error as ApiProblem | null;
    this.traceId.set(body?.traceId);
    if (error.status === 412) {
      this.conflict.set(true);
      this.confirming.set(null);
    } else if (error.status === 403) {
      this.problem.set({ key: 'subscriptions.form.forbidden' });
    } else if (error.status === 404) {
      // Removed or gone since the list was loaded.
      this.editing.set(null);
      this.confirming.set(null);
      this.problem.set({ key: 'paymentOptions.gone' });
      this.reloadList();
    } else if (error.status === 400 && body?.errors) {
      const mapped: Partial<Record<OptionField, Message>> = {};
      for (const field of ['name', 'label', 'brand'] as const) {
        if (Object.hasOwn(body.errors, field)) mapped[field] = { key: SERVER_KEYS[field] };
      }
      if (Object.keys(mapped).length > 0) {
        show(mapped);
        this.focusLater(focus);
      } else {
        this.problem.set({ key: 'paymentOptions.saveFailed' });
      }
    } else {
      this.problem.set({ key: 'paymentOptions.saveFailed' });
    }
  }

  /** Focus after the next render: the element is not in the DOM until change detection has run. */
  private focusLater(selector: string): void {
    if (!selector) return;
    afterNextRender(() => document.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }

  private clearProblems(): void {
    this.problem.set(null);
    this.notice.set(null);
    this.conflict.set(false);
    this.traceId.set(undefined);
    this.errors.set({});
    this.editErrors.set({});
  }
}
