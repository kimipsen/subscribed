import { DOCUMENT, DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, ComponentRef, DestroyRef, ViewContainerRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { DomSanitizer } from '@angular/platform-browser';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Household, Problem, SessionService, validHouseholdName, validMemberLabel } from './session';
import type { McpTokensPanel } from './mcp-tokens-panel';
import { LanguageService, Message, MessageKey } from './i18n/i18n';
import { TranslatePipe } from './i18n/translate.pipe';
import { SessionUnavailable } from './subscriptions/session-unavailable';
import { SubscriptionOverviewComponent } from './subscriptions/subscription-overview';
import { Notice } from './ui/notice';
import { Panel } from './ui/panel';
import { SkeletonBlock } from './ui/skeleton-block';

@Component({
  selector: 'app-foundation-page',
  imports: [FormsModule, DatePipe, RouterLink, TranslatePipe, SessionUnavailable, SubscriptionOverviewComponent, Notice, Panel, SkeletonBlock],
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
  private readonly document = inject(DOCUMENT);
  private readonly sanitizer = inject(DomSanitizer);
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

  // Time zone selector: its own state, like the member label editor.
  readonly timeZones = signal<string[]>([]);
  readonly timeZonesLoading = signal(false);
  readonly timeZonesFailed = signal(false);
  readonly zoneSaving = signal(false);
  readonly zoneSaved = signal(false);
  readonly zoneError = signal<Message | null>(null);
  readonly zoneConflict = signal(false);
  readonly zoneTraceId = signal<string | undefined>(undefined);
  zoneDraft = '';
  /** The server's list, plus the household's current zone when the list does not carry it (so the select never shows a blank). */
  readonly zoneOptions = computed(() => {
    const current = this.household()?.timeZoneId;
    const zones = this.timeZones();
    return current && !zones.includes(current) ? [current, ...zones] : zones;
  });

  // Calendar feed panel. The URL exists only here: it is never stored and is dropped with the component.
  readonly feedBusy = signal(false);
  readonly feedError = signal<Message | null>(null);
  readonly feedConflict = signal(false);
  readonly feedTraceId = signal<string | undefined>(undefined);
  readonly feedRevoked = signal(false);
  readonly confirmingRotate = signal(false);
  readonly feedUrl = signal<string | null>(null);
  readonly feedCopyMessage = signal<Message | null>(null);
  /**
   * The same link with the `webcal` scheme, which opens the calendar app's subscribe dialog. Angular's sanitizer
   * rejects that scheme, so the value is marked as trusted. That is safe only because the URL is parsed first and must
   * be an http(s) URL on this page's own origin: a server path such as `@evil.example/x` cannot move the host, and no
   * other scheme can get through. Anything else gets no subscribe link.
   */
  readonly webcalUrl = computed(() => {
    const url = this.feedUrl();
    if (url === null) return null;
    let parsed: URL;
    try {
      parsed = new URL(url);
    } catch {
      return null;
    }
    const origin = this.document.defaultView?.location.origin;
    if (!/^https?:$/u.test(parsed.protocol) || parsed.origin !== origin) return null;
    return this.sanitizer.bypassSecurityTrustUrl(`webcal://${parsed.host}${parsed.pathname}${parsed.search}`);
  });

  /**
   * Bumped on every household load; the MCP tokens panel reloads its list when it changes. The panel is a separate lazy
   * chunk (a dynamic import mounted into `mcpHost`) so that its code stays out of the initial bundle.
   */
  readonly mcpReload = signal(0);
  private readonly mcpHost = viewChild('mcpHost', { read: ViewContainerRef });
  private mcpPanel: ComponentRef<McpTokensPanel> | null = null;
  private mcpMounting = false;
  private destroyed = false;

  constructor() {
    effect(() => {
      if (this.page() === 'household' && this.session.current().state === 'member') this.load();
    });
    effect(() => {
      const host = this.mcpHost();
      if (host) untracked(() => void this.mountMcpPanel(host));
      else this.mcpPanel = null;
    });
    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
    effect(() => {
      const reload = this.mcpReload();
      this.mcpPanel?.setInput('reload', reload);
    });
  }

  load(): void {
    this.loading.set(true);
    this.clearErrors();
    this.http.get<Household>('/api/household').subscribe({
      next: (household) => {
        this.household.set(household);
        this.name = household.name;
        this.zoneDraft = household.timeZoneId;
        this.conflict.set(false);
        this.resetZoneFeedback();
        this.resetFeedFeedback();
        this.syncLabelEditor(household, true);
        this.loading.set(false);
        if (this.timeZones().length === 0) this.loadTimeZones();
        this.mcpReload.update((n) => n + 1);
      },
      error: (error: HttpErrorResponse) => {
        this.loading.set(false);
        this.problem.set({ key: 'household.loadFailed' });
        this.traceId.set(this.errorDetail(error)?.traceId);
      },
    });
  }

  private async mountMcpPanel(host: ViewContainerRef): Promise<void> {
    if (this.mcpPanel || this.mcpMounting) return;
    this.mcpMounting = true;
    try {
      const { McpTokensPanel: panel } = await import('./mcp-tokens-panel');
      // The page can be gone, the host replaced or already filled by the time the chunk arrives.
      if (this.destroyed || this.mcpHost() !== host || host.length > 0) return;
      const ref = host.createComponent(panel);
      ref.setInput('reload', untracked(this.mcpReload));
      ref.instance.householdChange.subscribe((updated) => this.replaceHousehold(updated));
      this.mcpPanel = ref;
      ref.onDestroy(() => {
        if (this.mcpPanel === ref) this.mcpPanel = null;
      });
    } catch {
      // The chunk could not be loaded (offline, or a deploy replaced it): the rest of the page keeps working and the
      // tokens section stays absent until the page is opened again.
    } finally {
      this.mcpMounting = false;
    }
  }

  /** A panel that changed the household (an MCP token create or revoke) hands back the new one, so If-Match stays current. */
  replaceHousehold(updated: Household): void {
    this.household.set(updated);
  }

  loadTimeZones(): void {
    if (this.timeZonesLoading()) return;
    this.timeZonesLoading.set(true);
    this.timeZonesFailed.set(false);
    this.http.get<{ timeZones: string[] }>('/api/household/time-zones').subscribe({
      next: ({ timeZones }) => {
        this.timeZones.set(Array.isArray(timeZones) ? timeZones : []);
        this.timeZonesFailed.set(!Array.isArray(timeZones));
        this.timeZonesLoading.set(false);
      },
      error: () => {
        this.timeZonesFailed.set(true);
        this.timeZonesLoading.set(false);
      },
    });
  }

  zoneUnchanged(): boolean {
    return this.zoneDraft === this.household()?.timeZoneId;
  }

  saveTimeZone(): void {
    this.resetZoneFeedback();
    this.zoneSaved.set(false);
    const household = this.household();
    if (!household || this.zoneSaving()) return;
    if (!this.zoneOptions().includes(this.zoneDraft)) {
      this.zoneError.set({ key: 'household.timeZoneInvalid' });
      return;
    }
    this.zoneSaving.set(true);
    this.http.put<Household>('/api/household/time-zone', { timeZoneId: this.zoneDraft }, {
      headers: { 'If-Match': `"${household.version}"` },
    }).subscribe({
      next: (updated) => {
        this.household.set(updated);
        this.zoneDraft = updated.timeZoneId;
        this.zoneSaving.set(false);
        this.zoneSaved.set(true);
      },
      error: (error: HttpErrorResponse) => {
        this.zoneSaving.set(false);
        const detail = this.errorDetail(error);
        if (error.status === 412) this.zoneConflict.set(true);
        else if (error.status === 400 && detail?.type === 'urn:subscribed:problem:validation-failed' &&
          detail.errors && Object.hasOwn(detail.errors, 'timeZoneId')) {
          this.zoneError.set({ key: 'household.timeZoneInvalid' });
        } else this.zoneError.set({ key: 'household.saveFailed' });
        this.zoneTraceId.set(detail?.traceId);
      },
    });
  }

  /** Creates the link, or (when one is active) asks first, because rotating breaks every calendar using the old link. */
  createOrRotateFeed(): void {
    if (this.feedBusy()) return;
    if (this.household()?.calendarFeed.enabled && !this.confirmingRotate()) {
      this.resetFeedFeedback();
      this.feedRevoked.set(false);
      this.confirmingRotate.set(true);
      return;
    }
    this.rotateFeed();
  }

  cancelRotate(): void {
    this.confirmingRotate.set(false);
  }

  private rotateFeed(): void {
    const household = this.household();
    if (!household || this.feedBusy()) return;
    this.confirmingRotate.set(false);
    this.resetFeedFeedback();
    this.feedRevoked.set(false);
    this.feedBusy.set(true);
    this.http.post<{ household: Household; feedPath: string }>('/api/household/calendar-feed', null, {
      headers: { 'If-Match': `"${household.version}"` },
    }).subscribe({
      next: ({ household: updated, feedPath }) => {
        this.household.set(updated);
        // The language at the time of the rotate decides the feed text language.
        const origin = this.document.defaultView?.location.origin ?? '';
        this.feedUrl.set(`${origin}${feedPath}?lang=${this.i18n.language()}`);
        this.feedBusy.set(false);
      },
      error: (error: HttpErrorResponse) => this.feedFailed(error),
    });
  }

  revokeFeed(): void {
    const household = this.household();
    if (!household || this.feedBusy()) return;
    this.confirmingRotate.set(false);
    this.resetFeedFeedback();
    this.feedBusy.set(true);
    this.http.delete<Household>('/api/household/calendar-feed', {
      headers: { 'If-Match': `"${household.version}"` },
    }).subscribe({
      next: (updated) => {
        this.household.set(updated);
        this.feedUrl.set(null);
        this.feedRevoked.set(true);
        this.feedBusy.set(false);
      },
      error: (error: HttpErrorResponse) => this.feedFailed(error),
    });
  }

  hideFeedUrl(): void {
    this.feedUrl.set(null);
    this.feedCopyMessage.set(null);
  }

  async copyFeedUrl(input: HTMLInputElement): Promise<void> {
    try {
      await navigator.clipboard.writeText(input.value);
      this.feedCopyMessage.set({ key: 'household.feedCopied' });
    } catch {
      input.focus();
      input.select();
      this.feedCopyMessage.set({ key: 'household.feedCopyUnavailable' });
    }
  }

  private feedFailed(error: HttpErrorResponse): void {
    this.feedBusy.set(false);
    const detail = this.errorDetail(error);
    if (error.status === 412) this.feedConflict.set(true);
    else this.feedError.set({ key: 'household.saveFailed' });
    this.feedTraceId.set(detail?.traceId);
  }

  private resetZoneFeedback(): void {
    this.zoneError.set(null);
    this.zoneConflict.set(false);
    this.zoneTraceId.set(undefined);
  }

  private resetFeedFeedback(): void {
    this.feedError.set(null);
    this.feedConflict.set(false);
    this.feedTraceId.set(undefined);
    this.feedCopyMessage.set(null);
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
    if (Object.hasOwn(body.errors, 'timeZoneId')) errors.timeZoneId = [];
    return Object.keys(errors).length > 0 ? errors : undefined;
  }
}
