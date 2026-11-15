import { HttpErrorResponse } from '@angular/common/http';
import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
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
import { ApiProblem, Price, SubscriptionOverview, SubscriptionStatus, SubscriptionSummary } from './subscription.models';
import { SubscriptionsService } from './subscriptions.service';

const STATUS_KEYS: Readonly<Record<SubscriptionStatus, MessageKey>> = {
  active: 'subscriptions.status.active',
  'not-started': 'subscriptions.status.not-started',
  ended: 'subscriptions.status.ended',
};

/** Cadence as a catalog message: "Monthly", "Every 3 months", ... */
export function cadenceMessage(unit: Price['cadenceUnit'], count: number): Message {
  if (count === 1) {
    return { key: unit === 'week' ? 'cadence.weekly' : unit === 'month' ? 'cadence.monthly' : 'cadence.yearly' };
  }
  const key: MessageKey = unit === 'week' ? 'cadence.everyWeeks' : unit === 'month' ? 'cadence.everyMonths' : 'cadence.everyYears';
  return { key, params: { count: String(count) } };
}

@Component({
  selector: 'app-subscription-overview',
  imports: [NgTemplateOutlet, RouterLink, TranslatePipe],
  templateUrl: './subscription-overview.html',
})
export class SubscriptionOverviewComponent {
  readonly i18n = inject(LanguageService);
  private readonly service = inject(SubscriptionsService);
  readonly loading = signal(true);
  readonly failed = signal(false);
  readonly reference = signal<string | undefined>(undefined);
  readonly overview = signal<SubscriptionOverview | null>(null);
  readonly notice = signal<Message | null>(null);

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
