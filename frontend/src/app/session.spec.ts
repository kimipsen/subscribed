import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpClient } from '@angular/common/http';
import { NAVIGATE, SessionService, csrfInterceptor, logoutDestination, validHouseholdName } from './session';
import { vi } from 'vitest';

describe('Session foundation', () => {
  let service: SessionService;
  let http: HttpTestingController;
  const navigate = vi.fn();

  beforeEach(() => {
    navigate.mockClear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        { provide: NAVIGATE, useValue: navigate },
      ],
    });
    service = TestBed.inject(SessionService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('starts loading and sends the CSRF header', () => {
    expect(service.current().state).toBe('loading');
    service.load();
    const request = http.expectOne('/bff/session');
    expect(request.request.headers.get('X-CSRF')).toBe('1');
    request.flush({ state: 'anonymous' });
    expect(service.current().state).toBe('anonymous');
  });

  for (const state of ['member', 'not-member'] as const) {
    it(`loads ${state} without treating it as anonymous`, () => {
      service.load();
      http.expectOne('/bff/session').flush({
        state, user: { displayName: 'Synthetic member', subject: 'synthetic-id', memberId: 'synthetic-member' },
        expiresAt: '2026-11-12T00:00:00Z',
      });
      expect(service.current().state).toBe(state);
    });
  }

  for (const status of [403, 500, 503]) {
    it(`makes session failure ${status} unavailable, not anonymous`, () => {
      service.load();
      http.expectOne('/bff/session').flush({ traceId: 'test-reference' }, { status, statusText: 'Failure' });
      expect(service.current()).toEqual({ state: 'unavailable', traceId: 'test-reference' });
      expect(service.unavailableReference()).toBe('test-reference');
    });
  }

  it('reports a network failure and supports retry', () => {
    service.load();
    http.expectOne('/bff/session').error(new ProgressEvent('error'));
    expect(service.current().state).toBe('unavailable');
    service.load();
    http.expectOne('/bff/session').flush({ state: 'anonymous' });
    expect(service.current().state).toBe('anonymous');
  });

  it('rejects unknown response states explicitly', () => {
    service.load();
    http.expectOne('/bff/session').flush({ state: 'unexpected' });
    expect(service.current().state).toBe('unavailable');
  });

  it('rejects a malformed member session rather than rendering protected UI', () => {
    service.load();
    http.expectOne('/bff/session').flush({ state: 'member' });
    expect(service.current().state).toBe('unavailable');
  });

  it('does not send CSRF to other origins', () => {
    TestBed.inject(HttpClient).get('https://example.invalid/api/data').subscribe();
    const request = http.expectOne('https://example.invalid/api/data');
    expect(request.request.headers.has('X-CSRF')).toBe(false);
    request.flush({});
  });

  it('expires the local session on API 401', () => {
    TestBed.inject(HttpClient).get('/api/household').subscribe({ error: () => undefined });
    http.expectOne('/api/household').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(service.expired()).toBe(true);
    expect(service.current().state).toBe('anonymous');
  });

  it('retains explicit sign-out failure and enables retry', () => {
    service.signOut();
    http.expectOne('/bff/logout').flush({}, { status: 503, statusText: 'Unavailable' });
    expect(service.logoutError()).toContain('may still be active');
    expect(service.logoutPending()).toBe(false);
    expect(navigate).not.toHaveBeenCalled();
    service.signOut();
    http.expectOne('/bff/logout').flush({ redirectUrl: '/signed-out?idp=0' });
    expect(navigate).toHaveBeenCalledWith('/signed-out?idp=0');
  });

  it('rejects unsafe login return paths', () => {
    service.signIn('//evil.invalid');
    expect(navigate).toHaveBeenCalledWith('/bff/login?returnUrl=%2F');
    service.signIn('/household');
    expect(navigate).toHaveBeenLastCalledWith('/bff/login?returnUrl=%2Fhousehold');
  });
});

describe('Input and navigation boundaries', () => {
  for (const url of ['//evil.invalid', 'javascript:alert(1)', '/other', '/signed-out?bad=1',
    'https://name:password@example.invalid', ' https://example.invalid', 'https:\\evil.invalid',
    'http://example.invalid']) {
    it(`rejects logout destination ${url}`, () => {
      expect(logoutDestination(url)).toBe('/signed-out?idp=0');
    });
  }
  it('allows configured HTTP during development only', () => {
    expect(logoutDestination('http://localhost:8180/logout', true)).toBe('http://localhost:8180/logout');
    expect(logoutDestination('https://example.invalid/logout')).toBe('https://example.invalid/logout');
    expect(logoutDestination('/signed-out')).toBe('/signed-out');
  });
  it('validates trimmed names and control characters', () => {
    expect(validHouseholdName(' Household ')).toBe(true);
    expect(validHouseholdName(' '.repeat(5))).toBe(false);
    expect(validHouseholdName('a'.repeat(81))).toBe(false);
    expect(validHouseholdName('bad\nname')).toBe(false);
    expect(validHouseholdName('a'.repeat(80))).toBe(true);
  });
});
