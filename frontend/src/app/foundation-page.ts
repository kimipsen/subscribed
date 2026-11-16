import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Household, Problem, SessionService, validHouseholdName, validMemberLabel } from './session';
import { LanguageService, Message, MessageKey } from './i18n/i18n';
import { TranslatePipe } from './i18n/translate.pipe';
import { SessionUnavailable } from './subscriptions/session-unavailable';
import { SubscriptionOverviewComponent } from './subscriptions/subscription-overview';

@Component({
  selector: 'app-foundation-page',
  imports: [FormsModule, DatePipe, RouterLink, TranslatePipe, SessionUnavailable, SubscriptionOverviewComponent],
  templateUrl: './foundation-page.html',
})
export class FoundationPage {
  readonly i18n = inject(LanguageService);
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
  readonly authError = computed<MessageKey | null>(() => {
    const code = this.route.snapshot.queryParamMap.get('authError');
    if (!code) return null;
    return code === 'cancelled' ? 'auth.cancelled' : 'auth.failed';
  });
  readonly returnUrl = computed(() =>
    this.route.snapshot.queryParamMap.get('returnUrl') ?? (this.page() === 'household' ? '/household' : '/'));
  readonly household = signal<Household | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly problem = signal<Message | null>(null);
  readonly traceId = signal<string | undefined>(undefined);
  readonly fieldError = signal<Message | null>(null);
  readonly conflict = signal(false);
  readonly saved = signal(false);
  readonly copyMessage = signal<Message | null>(null);
  name = '';
  readonly validName = validHouseholdName;

  // The member label editor owns its state; it never reads, shows or clears the rename banners.
  readonly editingMemberId = signal<string | null>(null);
  readonly labelSaving = signal(false);
  readonly labelError = signal<Message | null>(null);
  readonly labelConflict = signal(false);
  readonly labelSaved = signal(false);
  readonly labelTraceId = signal<string | undefined>(undefined);
  labelDraft = '';
  readonly validLabel = validMemberLabel;

  constructor() {
    effect(() => {
      if (this.page() === 'household' && this.session.current().state === 'member') this.load();
    });
  }

  load(): void {
    this.loading.set(true);
    this.clearErrors();
    this.http.get<Household>('/api/household').subscribe({
      next: (household) => {
        this.household.set(household);
        this.name = household.name;
        this.conflict.set(false);
        this.syncLabelEditor(household, true);
        this.loading.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.loading.set(false);
        this.problem.set({ key: 'household.loadFailed' });
        this.traceId.set(this.errorDetail(error)?.traceId);
      },
    });
  }

  save(): void {
    this.clearErrors();
    this.saved.set(false);
    if (!validHouseholdName(this.name)) {
      this.fieldError.set({ key: 'household.nameInvalid' });
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
        const detail = this.errorDetail(error);
        if (error.status === 412) this.conflict.set(true);
        else if (error.status === 400 && detail?.type === 'urn:subscribed:problem:validation-failed' &&
          detail.errors && Object.hasOwn(detail.errors, 'name')) {
          this.fieldError.set({ key: 'household.nameInvalid' });
        } else this.problem.set({ key: 'household.saveFailed' });
        this.traceId.set(detail?.traceId);
      },
    });
  }

  startEdit(member: Household['members'][number]): void {
    this.resetLabelFeedback();
    this.labelSaved.set(false);
    this.editingMemberId.set(member.memberId);
    this.labelDraft = member.displayLabel;
  }

  cancelEdit(): void {
    if (this.labelSaving()) return;
    this.resetLabelFeedback();
    this.editingMemberId.set(null);
    this.labelDraft = '';
  }

  saveLabel(member: Household['members'][number]): void {
    if (this.labelSaving()) return;
    this.resetLabelFeedback();
    this.labelSaved.set(false);
    if (!validMemberLabel(this.labelDraft)) {
      this.labelError.set({ key: 'household.memberLabelInvalid' });
      return;
    }
    const household = this.household();
    if (!household) return;
    this.labelSaving.set(true);
    this.http.put<Household>(`/api/household/members/${encodeURIComponent(member.memberId)}/label`,
      { displayLabel: this.labelDraft.trim() }, { headers: { 'If-Match': `"${household.version}"` } }).subscribe({
      next: (updated) => {
        this.household.set(updated);
        this.labelSaving.set(false);
        this.labelSaved.set(true);
        this.editingMemberId.set(null);
        this.labelDraft = '';
        this.syncLabelEditor(updated, false);
      },
      error: (error: HttpErrorResponse) => {
        this.labelSaving.set(false);
        const detail = this.errorDetail(error);
        if (error.status === 412) this.labelConflict.set(true);
        else if (error.status === 400 && detail?.type === 'urn:subscribed:problem:validation-failed' &&
          detail.errors && Object.hasOwn(detail.errors, 'displayLabel')) {
          this.labelError.set({ key: 'household.memberLabelInvalid' });
        } else this.labelError.set({ key: 'household.saveFailed' });
        this.labelTraceId.set(detail?.traceId);
      },
    });
  }

  labelInvalid(): boolean {
    return this.labelError()?.key === 'household.memberLabelInvalid';
  }

  private resetLabelFeedback(): void {
    this.labelError.set(null);
    this.labelConflict.set(false);
    this.labelTraceId.set(undefined);
  }

  /** Called whenever the household is replaced: drops an edit whose member is gone; a reload resets the draft. */
  private syncLabelEditor(household: Household, reload: boolean): void {
    const editing = this.editingMemberId();
    if (editing === null) return;
    const member = household.members.find((m) => m.memberId === editing);
    if (!member) {
      this.editingMemberId.set(null);
      this.labelDraft = '';
      this.resetLabelFeedback();
    } else if (reload) {
      this.labelDraft = member.displayLabel;
      this.resetLabelFeedback();
    }
  }

  async copy(input: HTMLInputElement): Promise<void> {
    try {
      await navigator.clipboard.writeText(input.value);
      this.copyMessage.set({ key: 'access.copied' });
    } catch {
      input.focus();
      input.select();
      this.copyMessage.set({ key: 'access.copyUnavailable' });
    }
  }

  private clearErrors(): void {
    this.problem.set(null);
    this.fieldError.set(null);
    this.conflict.set(false);
    this.traceId.set(undefined);
  }

  private errorDetail(error: HttpErrorResponse): Problem | undefined {
    const body: unknown = error.error;
    if (!body || typeof body !== 'object') return undefined;
    return {
      type: 'type' in body && typeof body.type === 'string' ? body.type : undefined,
      traceId: 'traceId' in body && typeof body.traceId === 'string' ? body.traceId : undefined,
      errors: this.errorFields(body),
    };
  }

  private errorFields(body: object): Problem['errors'] {
    if (!('errors' in body) || !body.errors || typeof body.errors !== 'object') return undefined;
    const errors: NonNullable<Problem['errors']> = {};
    if (Object.hasOwn(body.errors, 'name')) errors.name = [];
    if (Object.hasOwn(body.errors, 'displayLabel')) errors.displayLabel = [];
    return Object.keys(errors).length > 0 ? errors : undefined;
  }
}
