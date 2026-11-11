import { HttpClient, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, InjectionToken, computed, inject, isDevMode, signal } from '@angular/core';
import { catchError, throwError } from 'rxjs';

export interface User {
  displayName: string;
  memberId?: string;
  subject?: string;
}

export type Session =
  | { state: 'loading' | 'anonymous' }
  | { state: 'member' | 'not-member'; user: User; expiresAt: string }
  | { state: 'unavailable'; traceId?: string };

export interface Problem {
  traceId?: string;
  errors?: { name?: string[] };
  currentVersion?: number;
}

export interface Household {
  id: string;
  name: string;
  version: number;
  members: { memberId: string; displayLabel: string; grantedAt: string }[];
}

function isSessionResponse(value: unknown): value is Session {
  if (!value || typeof value !== 'object' || !('state' in value)) return false;
  if (value.state === 'anonymous') return true;
  if (value.state !== 'member' && value.state !== 'not-member') return false;
  if (!('user' in value) || !value.user || typeof value.user !== 'object' ||
      !('displayName' in value.user) || typeof value.user.displayName !== 'string' ||
      !('expiresAt' in value) || typeof value.expiresAt !== 'string') return false;
  return value.state === 'member' ?
    'memberId' in value.user && typeof value.user.memberId === 'string' :
    'subject' in value.user && typeof value.user.subject === 'string';
}

export const NAVIGATE = new InjectionToken<(url: string) => void>('full-page navigation', {
  providedIn: 'root',
  factory: () => (url) => window.location.assign(url),
});

function containsControlCharacters(value: string): boolean {
  return Array.from(value).some((character) => {
    const code = character.codePointAt(0) ?? 0;
    return code < 32 || code === 127;
  });
}

export function logoutDestination(value: unknown, development = false): string {
  if (value === '/signed-out' || value === '/signed-out?idp=0') return value;
  if (typeof value !== 'string' || value !== value.trim() ||
      containsControlCharacters(value) || value.includes('\\') || value.includes(' ')) {
    return '/signed-out?idp=0';
  }
  try {
    const url = new URL(value);
    if (url.username || url.password) return '/signed-out?idp=0';
    if (url.protocol === 'https:' || (development && url.protocol === 'http:')) return url.href;
  } catch {
    // Relative and malformed URLs are not identity-provider logout destinations.
  }
  return '/signed-out?idp=0';
}

export function validHouseholdName(name: string): boolean {
  const trimmed = name.trim();
  return trimmed.length >= 1 && trimmed.length <= 80 && !containsControlCharacters(name);
}

@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly http = inject(HttpClient);
  private readonly navigate = inject(NAVIGATE);
  readonly current = signal<Session>({ state: 'loading' });
  readonly expired = signal(false);
  readonly logoutPending = signal(false);
  readonly logoutError = signal<string | null>(null);
  readonly unavailableReference = computed(() => {
    const session = this.current();
    return session.state === 'unavailable' ? session.traceId : undefined;
  });

  load(): void {
    this.current.set({ state: 'loading' });
    this.http.get<unknown>('/bff/session').subscribe({
      next: (session) => {
        if (!isSessionResponse(session)) {
          this.current.set({ state: 'unavailable' });
          return;
        }
        this.current.set(session);
      },
      error: (error: HttpErrorResponse) => {
        this.current.set({ state: 'unavailable', traceId: (error.error as Problem)?.traceId });
      },
    });
  }

  expire(): void {
    this.expired.set(true);
    this.current.set({ state: 'anonymous' });
  }

  signIn(returnUrl = '/'): void {
    const local = returnUrl.startsWith('/') && !returnUrl.startsWith('//') &&
      !returnUrl.includes('\\') && !containsControlCharacters(returnUrl) &&
      returnUrl.length <= 512 ? returnUrl : '/';
    this.navigate(`/bff/login?returnUrl=${encodeURIComponent(local)}`);
  }

  signOut(): void {
    if (this.logoutPending()) return;
    this.logoutPending.set(true);
    this.logoutError.set(null);
    this.http.post<{ redirectUrl: string }>('/bff/logout', {}).subscribe({
      next: ({ redirectUrl }) => this.navigate(logoutDestination(redirectUrl, isDevMode())),
      error: () => {
        this.logoutPending.set(false);
        this.logoutError.set('Sign out failed. Your session may still be active. Please retry.');
      },
    });
  }
}

export const csrfInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(SessionService);
  const protectedRequest = /^\/(?:api(?:\/|$)|bff(?:\/|$))/u.test(request.url);
  return next(protectedRequest ? request.clone({ setHeaders: { 'X-CSRF': '1' } }) : request).pipe(
    catchError((error: HttpErrorResponse) => {
      if (protectedRequest && error.status === 401 && request.url !== '/bff/session') session.expire();
      return throwError(() => error);
    }),
  );
};
