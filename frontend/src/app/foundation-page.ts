import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Household, Problem, SessionService, validHouseholdName } from './session';

@Component({
  selector: 'app-foundation-page',
  imports: [FormsModule, DatePipe, RouterLink],
  templateUrl: './foundation-page.html',
})
export class FoundationPage {
  readonly session = inject(SessionService);
  private readonly http = inject(HttpClient);
  readonly route = inject(ActivatedRoute);
  private readonly data = toSignal(this.route.data);
  readonly page = computed(() => this.data()?.['page'] as string | undefined);
  readonly user = computed(() => {
    const state = this.session.current();
    return state.state === 'member' || state.state === 'not-member' ? state.user : null;
  });
  readonly reference = computed(() => {
    const state = this.session.current();
    return state.state === 'not-member' ? state.user.subject ?? '' : '';
  });
  readonly authError = computed(() => {
    const code = this.route.snapshot.queryParamMap.get('authError');
    return code === 'cancelled' ? 'Sign in was cancelled. You can try again.' :
      code ? 'Sign in could not be completed. Please try again.' : '';
  });
  readonly returnUrl = computed(() =>
    this.route.snapshot.queryParamMap.get('returnUrl') ?? (this.page() === 'household' ? '/household' : '/'));
  readonly household = signal<Household | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly problem = signal<string | null>(null);
  readonly traceId = signal<string | undefined>(undefined);
  readonly fieldError = signal<string | null>(null);
  readonly conflict = signal(false);
  readonly saved = signal(false);
  readonly copyMessage = signal('');
  name = '';
  readonly validName = validHouseholdName;

  constructor() {
    effect(() => {
      if (this.page() === 'household' && this.session.current().state === 'member') this.load();
    });
  }

  load(): void {
    this.loading.set(true);
    this.problem.set(null);
    this.http.get<Household>('/api/household').subscribe({
      next: (household) => {
        this.household.set(household);
        this.name = household.name;
        this.conflict.set(false);
        this.loading.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.loading.set(false);
        this.problem.set('The household could not be loaded. Please retry.');
        this.traceId.set((error.error as Problem)?.traceId);
      },
    });
  }

  save(): void {
    this.fieldError.set(null);
    this.saved.set(false);
    if (!validHouseholdName(this.name)) {
      this.fieldError.set('Enter 1-80 characters, without control characters.');
      return;
    }
    const household = this.household();
    if (!household || this.saving()) return;
    this.saving.set(true);
    this.problem.set(null);
    this.http.put<Household>('/api/household/name', { name: this.name.trim() }, {
      headers: { 'If-Match': `"${household.version}"` },
    }).subscribe({
      next: (updated) => {
        this.household.set(updated);
        this.name = updated.name;
        this.saving.set(false);
        this.conflict.set(false);
        this.saved.set(true);
      },
      error: (error: HttpErrorResponse) => {
        this.saving.set(false);
        const detail = error.error as Problem;
        if (error.status === 412) this.conflict.set(true);
        else if (error.status === 400) this.fieldError.set(detail?.errors?.name?.[0] ?? 'Check the household name.');
        else this.problem.set('The change could not be saved. Please retry.');
        this.traceId.set(detail?.traceId);
      },
    });
  }

  async copy(input: HTMLInputElement): Promise<void> {
    try {
      await navigator.clipboard.writeText(input.value);
      this.copyMessage.set('Reference copied.');
    } catch {
      input.focus();
      input.select();
      this.copyMessage.set('Automatic copy is unavailable. The reference is selected; copy it manually.');
    }
  }
}
