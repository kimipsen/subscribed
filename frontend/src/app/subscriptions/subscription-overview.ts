import { HttpErrorResponse } from '@angular/common/http';
import { NgTemplateOutlet } from '@angular/common';
import { Component, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LanguageService, Message, MessageKey } from '../i18n/i18n';
import { TranslatePipe } from '../i18n/translate.pipe';
import {
  ALL_SELECTION,
  CategoryGroup,
  CategorySelection,
  buildCategories,
  filterRows,
  groupRows,
  parseSelection,
  selectionExists,
  selectionValue,
} from './categories';
import { formatMoney } from './money';
import { ServiceLogo } from './service-logo';
import { ApiProblem, Price, SubscriptionOverview, SubscriptionStatus, SubscriptionSummary } from './subscription.models';
import { SubscriptionsService } from './subscriptions.service';

const STATUS_KEYS: Readonly<Record<SubscriptionStatus, MessageKey>> = {
  active: 'subscriptions.status.active',
  'not-started': 'subscriptions.status.not-started',
  ended: 'subscriptions.status.ended',
  paused: 'subscriptions.status.paused',
};

/** Cadence as a catalog message: "Monthly", "Every 3 months", ... */
export function cadenceMessage(unit: Price['cadenceUnit'], count: number): Message {
  if (count === 1) {
    return { key: unit === 'week' ? 'cadence.weekly' : unit === 'month' ? 'cadence.monthly' : 'cadence.yearly' };
  }
  const key: MessageKey = unit === 'week' ? 'cadence.everyWeeks' : unit === 'month' ? 'cadence.everyMonths' : 'cadence.everyYears';
  return { key, params: { count: String(count) } };
}

const FAILURE_KEYS: Record<number, MessageKey> = {
  412: 'subscriptions.form.conflict',
  409: 'subscriptions.pauseInvalid',
  403: 'subscriptions.form.forbidden',
  404: 'subscriptions.notFound',
};

@Component({
  selector: 'app-subscription-overview',
  imports: [NgTemplateOutlet, RouterLink, TranslatePipe, ServiceLogo],
  templateUrl: './subscription-overview.html',
})
export class SubscriptionOverviewComponent {
  readonly i18n = inject(LanguageService);
  private readonly service = inject(SubscriptionsService);
  private readonly injector = inject(Injector);
  readonly loading = signal(true);
  readonly failed = signal(false);
  readonly reference = signal<string | undefined>(undefined);
  readonly overview = signal<SubscriptionOverview | null>(null);
  readonly notice = signal<Message | null>(null);

  /** The row whose pause or resume request is in flight: every action button is disabled meanwhile, so a double click sends one request. */
  readonly acting = signal<string | null>(null);
  readonly actionProblem = signal<Message | null>(null);
  readonly actionTrace = signal<string | undefined>(undefined);

  /** The API sorts by name ordinally; the screen re-sorts with the active language's collation, then start date, then id. */
  readonly rows = computed<SubscriptionSummary[]>(() => {
    const collator = new Intl.Collator(this.i18n.dateLocale());
    return [...(this.overview()?.subscriptions ?? [])].sort((a, b) =>
      collator.compare(a.name, b.name) || a.startDate.localeCompare(b.startDate) || a.id.localeCompare(b.id));
  });

  /** View state only (R5.3). The stored selection is never cleared: effectiveFilter hides it while its category is missing. */
  readonly categoryFilter = signal<CategorySelection>(ALL_SELECTION);
  readonly grouped = signal(false);

  readonly categorySet = computed(() => buildCategories(this.rows()));
  /** The select's options in the active language's alphabetical order. */
  readonly categoryOptions = computed(() => {
    const collator = new Intl.Collator(this.i18n.dateLocale());
    return [...this.categorySet().options].sort((a, b) => collator.compare(a.label, b.label) || a.key.localeCompare(b.key));
  });
  /** R3.3: a selection whose category no longer exists reads as "All". */
  readonly effectiveFilter = computed(() => {
    const selection = this.categoryFilter();
    return selectionExists(selection, this.categorySet()) ? selection : ALL_SELECTION;
  });
  readonly filterValue = computed(() => selectionValue(this.effectiveFilter()));
  readonly visibleRows = computed(() => filterRows(this.rows(), this.effectiveFilter()));
  readonly groups = computed(() => (this.grouped() ? groupRows(this.visibleRows(), this.i18n.dateLocale()) : []));

  constructor() {
    this.notice.set(this.service.notice());
    this.service.notice.set(null);
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.failed.set(false);
    this.service.overview().subscribe({
      next: (overview) => {
        this.overview.set(overview);
        this.loading.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.loading.set(false);
        this.failed.set(true);
        this.reference.set((error.error as ApiProblem | null)?.traceId);
      },
    });
  }

  /** Pause is offered on active rows only. Resume follows the flag, so it stays reachable when the dates show `ended` or `not-started`. */
  canPause(row: SubscriptionSummary): boolean {
    return row.status === 'active' && !row.paused;
  }

  toggle(row: SubscriptionSummary): void {
    if (this.acting() !== null || !(row.paused || this.canPause(row))) return;
    const pausing = !row.paused;
    this.acting.set(row.id);
    this.actionProblem.set(null);
    this.actionTrace.set(undefined);
    this.notice.set(null);
    (pausing ? this.service.pause(row.id, row.version) : this.service.resume(row.id, row.version)).subscribe({
      next: () => {
        this.acting.set(null);
        this.notice.set({ key: pausing ? 'subscriptions.paused' : 'subscriptions.resumed', params: { name: row.name } });
        this.refresh(row.id);
      },
      error: (error: HttpErrorResponse) => {
        this.acting.set(null);
        this.fail(error);
        // The row's version may be stale, so the overview is reloaded whatever went wrong.
        this.refresh(row.id);
      },
    });
  }

  /** Reloads the rows in place: no skeleton, so the table and the keyboard focus stay put. */
  private refresh(focusRow: string): void {
    this.service.overview().subscribe({
      next: (overview) => {
        this.overview.set(overview);
        afterNextRender(() => document.querySelector<HTMLElement>(`#action-${focusRow}`)?.focus(), { injector: this.injector });
      },
      error: (error: HttpErrorResponse) => {
        this.failed.set(true);
        this.reference.set((error.error as ApiProblem | null)?.traceId);
      },
    });
  }

  private fail(error: HttpErrorResponse): void {
    this.actionTrace.set((error.error as ApiProblem | null)?.traceId);
    this.actionProblem.set({ key: FAILURE_KEYS[error.status] ?? 'subscriptions.form.saveFailed' });
  }

  selectCategory(value: string): void {
    this.categoryFilter.set(parseSelection(value));
  }

  setGrouped(value: boolean): void {
    this.grouped.set(value);
  }

  groupLabel(group: CategoryGroup): string {
    return group.label ?? this.i18n.message({ key: 'subscriptions.filter.uncategorized' });
  }

  money(amount: string, currency: string, minorUnit: number): string {
    return formatMoney(amount, currency, minorUnit, this.i18n.dateLocale());
  }

  price(row: SubscriptionSummary): string {
    return this.money(row.currentPrice.amount, row.currency, row.minorUnit);
  }

  cadence(price: Price): string {
    return this.i18n.message(cadenceMessage(price.cadenceUnit, price.cadenceCount));
  }

  statusKey(status: SubscriptionStatus): MessageKey {
    return STATUS_KEYS[status];
  }
}
