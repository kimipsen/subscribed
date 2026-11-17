import { Component, computed, inject } from '@angular/core';
import { LanguageService, Message } from '../i18n/i18n';
import { TranslatePipe } from '../i18n/translate.pipe';
import { Period, PeriodService, periodKey, periodOptionMessage } from './period';
import { SubscriptionsService } from './subscriptions.service';

interface PeriodChoice {
  value: string;
  /** A catalog message for the rolling windows, null for a year (its label is the number). */
  message: Message | null;
  label: string;
}

/**
 * The labelled period select. It reads and writes the shared selection in PeriodService and emits nothing, so every page that
 * totals over a period just reads the same signal. The calendar years come from `GET periods`, loaded once per selector; until
 * (or unless) that answers, the current and the next year are offered.
 */
@Component({
  selector: 'app-period-selector',
  imports: [TranslatePipe],
  templateUrl: './period-selector.html',
})
export class PeriodSelector {
  readonly i18n = inject(LanguageService);
  private readonly period = inject(PeriodService);
  private readonly service = inject(SubscriptionsService);

  /** The select's value: the resolved period, so a stored year that is no longer offered shows as the current year. */
  readonly value = computed(() => this.period.effectiveKey());

  readonly choices = computed<PeriodChoice[]>(() => {
    const current = this.period.currentYear();
    const offered = this.period.years() ?? [current, current + 1];
    // Until the list answers (or when it fails) the effective year may be a stored one that the fallback list lacks: keep it selectable.
    const effective = this.period.effective();
    const years = effective.kind === 'year' && !offered.includes(effective.year) ? [...offered, effective.year].sort((a, b) => a - b) : offered;
    const periods: Period[] = [
      ...years.map((year): Period => ({ kind: 'year', year })),
      { kind: 'last-12-months' },
      { kind: 'next-12-months' },
    ];
    return periods.map((period) => {
      const message = periodOptionMessage(period);
      return { value: periodKey(period), message, label: period.kind === 'year' ? String(period.year) : '' };
    });
  });

  constructor() {
    this.service.periods().subscribe({
      next: (options) => this.period.setYears(options.years),
      // The fallback options stay: a failed list must not break the page that hosts the selector.
      error: () => undefined,
    });
  }

  choose(key: string): void {
    this.period.select(key);
  }
}
