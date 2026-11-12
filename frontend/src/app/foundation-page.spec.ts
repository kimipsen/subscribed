import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FoundationPage } from './foundation-page';
import { Household, SessionService, csrfInterceptor } from './session';
import { routes } from './app.routes';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService } from './i18n/i18n';
import { en } from './i18n/catalog.en';
import { da } from './i18n/catalog.da';
import { vi } from 'vitest';
import './app.config';

describe('Foundation UI', () => {
  let http: HttpTestingController;
  let session: SessionService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
  });
  afterEach(() => http.verify());

  it('labels loading, anonymous, unavailable and access-denied states', async () => {
    const harness = await RouterTestingHarness.create('/');
    expect(harness.routeNativeElement?.querySelector('[role=status]')?.textContent).toContain('Checking');
    session.current.set({ state: 'anonymous' });
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Sign in with');
    session.current.set({ state: 'unavailable', traceId: 'synthetic-ref' });
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('synthetic-ref');
    session.current.set({ state: 'not-member', user: { displayName: 'Synthetic', subject: 'ref' }, expiresAt: '' });
    harness.detectChanges();
    expect(harness.routeNativeElement?.querySelector('label[for=reference]')).toBeTruthy();
    expect(harness.routeNativeElement?.textContent).toContain('Household access needed');
  });

  it('renders empty member home without subscription controls', async () => {
    session.current.set({ state: 'member', user: { displayName: 'Synthetic' }, expiresAt: '' });
    const harness = await RouterTestingHarness.create('/');
    expect(harness.routeNativeElement?.textContent).toContain('next milestone');
    expect(harness.routeNativeElement?.querySelector('a[href="/household"]')).toBeTruthy();
  });

  it('guards household routes while preserving the sign-in return path', async () => {
    session.current.set({ state: 'anonymous' });
    await RouterTestingHarness.create('/household');
    expect(TestBed.inject(Router).url).toBe('/welcome?returnUrl=%2Fhousehold');
    http.expectNone('/api/household');
  });

  it('routes non-members to access denied without loading household data', async () => {
    session.current.set({ state: 'not-member', user: { displayName: 'Synthetic', subject: 'ref' }, expiresAt: '' });
    await RouterTestingHarness.create('/household');
    expect(TestBed.inject(Router).url).toBe('/access-denied');
    http.expectNone('/api/household');
  });

  it('keeps session failures unavailable rather than redirecting to sign in', async () => {
    session.current.set({ state: 'unavailable', traceId: 'ref' });
    const harness = await RouterTestingHarness.create('/household');
    expect(harness.routeNativeElement?.textContent).toContain('could not be checked');
    expect(TestBed.inject(Router).url).toBe('/household');
    http.expectNone('/api/household');
  });

  it('retains input on concurrency failure and reloads the latest version', async () => {
    session.current.set({ state: 'member', user: { displayName: 'Synthetic' }, expiresAt: '' });
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl('/household', FoundationPage);
    http.expectOne('/api/household').flush({ id: 'household-default', name: 'Before', version: 3, members: [] });
    harness.detectChanges();
    expect(harness.routeNativeElement?.querySelector('label[for=household-name]')).toBeTruthy();
    page.name = 'My edit';
    page.save();
    const save = http.expectOne('/api/household/name');
    expect(save.request.headers.get('If-Match')).toBe('"3"');
    expect(save.request.headers.get('X-CSRF')).toBe('1');
    save.flush({ currentVersion: 4 }, { status: 412, statusText: 'Precondition failed' });
    expect(page.name).toBe('My edit');
    expect(page.conflict()).toBe(true);
    page.load();
    http.expectOne('/api/household').flush({ id: 'household-default', name: 'Latest', version: 4, members: [] });
    expect(page.name).toBe('Latest');
    expect(page.conflict()).toBe(false);
    page.name = 'Saved edit';
    page.save();
    http.expectOne('/api/household/name').flush({ id: 'household-default', name: 'Saved edit', version: 5, members: [] });
    expect(page.saved()).toBe(true);
    expect(page.household()?.version).toBe(5);
  });

  it('maps server validation errors and explicitly reports load failures', async () => {
    session.current.set({ state: 'member', user: { displayName: 'Synthetic' }, expiresAt: '' });
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl('/household', FoundationPage);
    http.expectOne('/api/household').flush({}, { status: 503, statusText: 'Unavailable' });
    expect(page.problem()).toBeTruthy();
    page.load();
    http.expectOne('/api/household').flush({ id: 'household-default', name: 'Before', version: 1, members: [] });
    page.name = 'Valid name';
    page.save();
    http.expectOne('/api/household/name').flush({
      type: 'urn:subscribed:problem:validation-failed', errors: { name: ['Server validation'] },
    },
      { status: 400, statusText: 'Bad request' });
    harness.detectChanges();
    expect(page.fieldError()).toEqual({ key: 'household.nameInvalid' });
    expect(harness.routeNativeElement?.textContent).not.toContain('Server validation');
    expect(harness.routeNativeElement?.querySelector('[aria-invalid=true]')).toBeTruthy();
    page.name = '';
    page.save();
    expect(page.fieldError()).toEqual({ key: 'household.nameInvalid' });
  });

  async function openHousehold(members: Household['members'] = []) {
    session.current.set({ state: 'member', user: { displayName: 'Synthetic' }, expiresAt: '' });
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl('/household', FoundationPage);
    http.expectOne('/api/household').flush({ id: 'household-default', name: 'Before', version: 1, members });
    return { harness, page };
  }

  it('switches dates and status text without losing input', async () => {
    const { harness, page } = await openHousehold([
      { memberId: '1', displayLabel: 'Åse Ørnsø', grantedAt: '2026-03-05T12:00:00Z' },
    ]);
    harness.detectChanges();
    await harness.fixture.whenStable();
    const input = harness.routeNativeElement?.querySelector('input[name=householdName]') as HTMLInputElement;
    input.value = 'Unsaved Øst';
    input.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();
    expect(page.name).toBe('Unsaved Øst');
    expect(harness.routeNativeElement?.querySelector('.members')?.textContent).toContain('Joined 5 Mar 2026');
    page.saved.set(true);
    TestBed.inject(LanguageService).setLanguage('da');
    await harness.fixture.whenStable();
    expect(input.value).toBe('Unsaved Øst');
    expect(page.name).toBe('Unsaved Øst');
    expect(harness.routeNativeElement?.querySelector('.members')?.textContent).toContain('Medlem siden 5. mar. 2026');
    expect(harness.routeNativeElement?.textContent).toContain('Husstandens navn er gemt.');
    expect(harness.routeNativeElement?.textContent).toContain('Åse Ørnsø');
    http.expectNone('/api/household');
    expect(TestBed.inject(Router).url).toBe('/household');
  });

  for (const { status, body, block, key } of [
    { status: 400, body: { type: 'urn:subscribed:problem:validation-failed', errors: { name: ['Backend English'] } },
      block: '#name-error', key: 'household.nameInvalid' },
    { status: 412, body: {}, block: '.notice', key: 'household.conflict' },
    { status: 500, body: {}, block: '.error', key: 'household.saveFailed' },
    { status: 400, body: { errors: { name: ['Backend English'] } },
      block: '.error', key: 'household.saveFailed' },
  ] as const) {
    it(`localizes ${status} ${key} and preserves/clears references`, async () => {
      const { harness, page } = await openHousehold();
      page.name = 'Edited';
      page.save();
      http.expectOne('/api/household/name').flush({ ...body, traceId: '00-abc-01' },
        { status, statusText: 'Failure' });
      harness.detectChanges();
      const error = harness.routeNativeElement?.querySelector(block);
      expect(error?.textContent).toContain(en[key]);
      expect(error?.querySelector('code')?.textContent).toBe('00-abc-01');
      expect(error?.textContent).not.toContain('Backend English');
      TestBed.inject(LanguageService).setLanguage('da');
      await harness.fixture.whenStable();
      expect(error?.textContent).toContain(da[key]);
      expect(error?.textContent).toContain('Fejlreference:');
      expect(error?.querySelector('code')?.textContent).toBe('00-abc-01');
      page.name = '';
      page.save();
      harness.detectChanges();
      expect(page.traceId()).toBeUndefined();
      expect(page.problem()).toBeNull();
      expect(page.conflict()).toBe(false);
      expect(harness.routeNativeElement?.querySelector('code')).toBeNull();
      expect(harness.routeNativeElement?.textContent).toContain(da['household.nameInvalid']);
    });
  }

  it('localizes unknown and malformed API failures', async () => {
    const { harness, page } = await openHousehold();
    TestBed.inject(LanguageService).setLanguage('da');
    for (const body of ['Backend English', { type: 'urn:other', errors: { name: ['Backend English'] } },
      { type: 'urn:subscribed:problem:validation-failed', errors: { ifMatch: ['Backend English'] } }]) {
      page.save();
      http.expectOne('/api/household/name').flush(body, { status: 400, statusText: 'Bad request' });
      harness.detectChanges();
      expect(harness.routeNativeElement?.textContent).toContain(da['household.saveFailed']);
      expect(harness.routeNativeElement?.textContent).not.toContain('Backend English');
      expect(page.fieldError()).toBeNull();
    }
  });

  it('retranslates clipboard feedback, not account references', async () => {
    session.current.set({ state: 'not-member', user: { displayName: 'Synthetic', subject: 'unchanged-ref' }, expiresAt: '' });
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl('/access-denied', FoundationPage);
    const input = harness.routeNativeElement?.querySelector('#reference') as HTMLInputElement;
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    try {
      await page.copy(input);
      expect(writeText).toHaveBeenCalledWith('unchanged-ref');
      harness.detectChanges();
      expect(harness.routeNativeElement?.textContent).toContain(en['access.copied']);
      TestBed.inject(LanguageService).setLanguage('da');
      await harness.fixture.whenStable();
      expect(harness.routeNativeElement?.textContent).toContain(da['access.copied']);
      writeText.mockRejectedValue(new Error('Unavailable'));
      await page.copy(input);
      harness.detectChanges();
      expect(harness.routeNativeElement?.textContent).toContain(da['access.copyUnavailable']);
      expect(input.value).toBe('unchanged-ref');
      expect(input.selectionStart).toBe(0);
      expect(input.selectionEnd).toBe(input.value.length);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  for (const { state, url, key } of [
    { state: { state: 'loading' }, url: '/', key: 'loading.heading' },
    { state: { state: 'unavailable', traceId: 'ref' }, url: '/', key: 'unavailable.heading' },
    { state: { state: 'anonymous' }, url: '/welcome?authError=cancelled', key: 'auth.cancelled' },
    { state: { state: 'anonymous' }, url: '/welcome?authError=failed', key: 'auth.failed' },
    { state: { state: 'not-member', user: { displayName: 'Test', subject: 'ref' }, expiresAt: '' },
      url: '/access-denied', key: 'access.heading' },
    { state: { state: 'member', user: { displayName: 'Test' }, expiresAt: '' },
      url: '/', key: 'home.emptyHeading' },
    { state: { state: 'member', user: { displayName: 'Test' }, expiresAt: '' },
      url: '/missing', key: 'notFound.heading' },
    { state: { state: 'anonymous' }, url: '/signed-out?idp=0', key: 'signedOut.idpActive' },
  ] as const) {
    it(`renders ${url} ${state.state} in Danish without English application copy`, async () => {
      session.current.set(state);
      TestBed.inject(LanguageService).setLanguage('da');
      const harness = await RouterTestingHarness.create(url);
      const text = harness.routeNativeElement?.textContent ?? '';
      expect(text).toContain(da[key]);
      for (const value of Object.values(en)) {
        if (value === 'Menu' || value.includes('{')) continue;
        // Check complete visible messages, not substrings such as "Name" inside other words.
        const nodes = Array.from(harness.routeNativeElement?.querySelectorAll('h1,h2,p,button,a,label') ?? []);
        expect(nodes.some((node) => node.textContent?.trim() === value), value).toBe(false);
      }
    });
  }
});
