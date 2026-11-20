import { DOCUMENT, DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { LanguageService, Message } from './i18n/i18n';
import { TranslatePipe } from './i18n/translate.pipe';
import { Household, McpToken, McpTokenCreated, McpTokenRevoked, validMcpTokenName } from './session';
import { Notice } from './ui/notice';
import { Panel } from './ui/panel';
import { SkeletonBlock } from './ui/skeleton-block';

const URN = 'urn:subscribed:problem:';

/**
 * The MCP tokens section of the household page. It is loaded lazily (`@defer`) so it stays out of the initial bundle.
 *
 * The raw token exists only in `mcpSecret`: it is never stored, never part of the list, and is dropped on hide, on
 * revoke of that token and when the component is destroyed (leaving the page, sign-out or session expiry removes the
 * whole page content, which destroys this component).
 *
 * Create and revoke do not send If-Match, but every response carries the updated household. The page owns the household,
 * so each response is handed back through `householdChange`; that keeps the cached version, and the If-Match of the next
 * rename, current.
 */
@Component({
  selector: 'app-mcp-tokens-panel',
  imports: [FormsModule, DatePipe, TranslatePipe, Notice, Panel, SkeletonBlock],
  host: { class: 'block' },
  templateUrl: './mcp-tokens-panel.html',
})
export class McpTokensPanel {
  readonly i18n = inject(LanguageService);
  private readonly http = inject(HttpClient);
  private readonly document = inject(DOCUMENT);

  /** Changes whenever the page (re)loads the household; the list is fetched again each time. */
  readonly reload = input.required<number>();
  readonly householdChange = output<Household>();

  readonly mcpTokens = signal<McpToken[] | null>(null);
  readonly mcpLoadFailed = signal(false);
  readonly mcpBusy = signal(false);
  readonly mcpError = signal<Message | null>(null);
  readonly mcpTraceId = signal<string | undefined>(undefined);
  readonly mcpSecret = signal<{ id: string; name: string; value: string } | null>(null);
  readonly mcpCopyMessage = signal<Message | null>(null);
  readonly mcpRevoked = signal(false);
  readonly confirmingRevokeId = signal<string | null>(null);
  mcpName = '';
  readonly validMcpName = validMcpTokenName;
  readonly mcpUrl = computed(() => `${this.document.defaultView?.location.origin ?? ''}/mcp`);
  readonly mcpCommand = computed(() => {
    const secret = this.mcpSecret();
    return secret === null ? null :
      `claude mcp add --transport http subscribed ${this.mcpUrl()} --header "Authorization: Bearer ${secret.value}"`;
  });

  constructor() {
    effect(() => {
      this.reload();
      untracked(() => {
        this.resetFeedback();
        this.confirmingRevokeId.set(null);
        this.loadMcpTokens();
      });
    });
    inject(DestroyRef).onDestroy(() => this.dropMcpSecret());
  }

  loadMcpTokens(): void {
    this.mcpLoadFailed.set(false);
    this.http.get<{ tokens: McpToken[] }>('/api/household/mcp-tokens').subscribe({
      next: ({ tokens }) => {
        this.mcpTokens.set(Array.isArray(tokens) ? tokens : []);
        this.mcpLoadFailed.set(!Array.isArray(tokens));
      },
      error: () => {
        this.mcpTokens.set(null);
        this.mcpLoadFailed.set(true);
      },
    });
  }

  /** Creates a token. The response carries the secret once. */
  createMcpToken(): void {
    if (this.mcpBusy()) return;
    this.resetFeedback();
    this.mcpRevoked.set(false);
    this.confirmingRevokeId.set(null);
    if (!validMcpTokenName(this.mcpName)) {
      this.mcpError.set({ key: 'household.mcpNameInvalid' });
      return;
    }
    this.mcpBusy.set(true);
    this.http.post<McpTokenCreated>('/api/household/mcp-tokens', { name: this.mcpName.trim() }).subscribe({
      next: ({ household, token, secret }) => {
        this.householdChange.emit(household);
        this.mcpTokens.update((tokens) => [...(tokens ?? []).filter((t) => t.id !== token.id), token]);
        this.mcpSecret.set({ id: token.id, name: token.name, value: secret });
        this.mcpName = '';
        this.mcpBusy.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.mcpBusy.set(false);
        const { type, traceId, nameInvalid } = this.errorDetail(error);
        if (error.status === 400 && type === `${URN}validation-failed` && nameInvalid) {
          this.mcpError.set({ key: 'household.mcpNameInvalid' });
        } else if (error.status === 409 && type === `${URN}too-many-tokens`) {
          this.mcpError.set({ key: 'household.mcpTooMany' });
        } else if (error.status === 409 && type === `${URN}mcp-disabled`) {
          this.mcpError.set({ key: 'household.mcpDisabled' });
        } else this.mcpError.set({ key: 'household.mcpFailed' });
        this.mcpTraceId.set(traceId);
      },
    });
  }

  askRevokeMcpToken(token: McpToken): void {
    if (this.mcpBusy()) return;
    this.resetFeedback();
    this.mcpRevoked.set(false);
    this.confirmingRevokeId.set(token.id);
  }

  cancelRevokeMcpToken(): void {
    this.confirmingRevokeId.set(null);
  }

  revokeMcpToken(token: McpToken): void {
    if (this.mcpBusy() || this.confirmingRevokeId() !== token.id) return;
    this.resetFeedback();
    this.mcpBusy.set(true);
    this.http.delete<McpTokenRevoked>(`/api/household/mcp-tokens/${encodeURIComponent(token.id)}`).subscribe({
      next: ({ household }) => {
        this.householdChange.emit(household);
        this.mcpTokens.update((tokens) => (tokens ?? []).filter((t) => t.id !== token.id));
        if (this.mcpSecret()?.id === token.id) this.dropMcpSecret();
        this.confirmingRevokeId.set(null);
        this.mcpRevoked.set(true);
        this.mcpBusy.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.mcpBusy.set(false);
        this.confirmingRevokeId.set(null);
        this.mcpError.set({ key: 'household.mcpFailed' });
        this.mcpTraceId.set(this.errorDetail(error).traceId);
      },
    });
  }

  hideMcpSecret(): void {
    this.dropMcpSecret();
  }

  async copyMcpSecret(input: HTMLInputElement): Promise<void> {
    try {
      await navigator.clipboard.writeText(input.value);
      this.mcpCopyMessage.set({ key: 'household.mcpCopied' });
    } catch {
      input.focus();
      input.select();
      this.mcpCopyMessage.set({ key: 'household.mcpCopyUnavailable' });
    }
  }

  private dropMcpSecret(): void {
    this.mcpSecret.set(null);
    this.mcpCopyMessage.set(null);
  }

  private resetFeedback(): void {
    this.mcpError.set(null);
    this.mcpTraceId.set(undefined);
  }

  private errorDetail(error: HttpErrorResponse): { type?: string; traceId?: string; nameInvalid: boolean } {
    const body: unknown = error.error;
    if (!body || typeof body !== 'object') return { nameInvalid: false };
    const errors = 'errors' in body && body.errors && typeof body.errors === 'object' ? body.errors : null;
    return {
      type: 'type' in body && typeof body.type === 'string' ? body.type : undefined,
      traceId: 'traceId' in body && typeof body.traceId === 'string' ? body.traceId : undefined,
      nameInvalid: errors !== null && Object.hasOwn(errors, 'name'),
    };
  }
}
