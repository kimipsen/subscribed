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
import { query, queryAll } from './testing/query';
import { flushPeriods, periodTestProviders } from './subscriptions/testing/period-providers';
import './app.config';

const ZONES = ['Europe/Copenhagen', 'Europe/London', 'UTC'];
const FEED_OFF = { enabled: false, rotatedAt: null };
const zoneFields = { timeZoneId: 'Europe/Copenhagen', calendarFeed: FEED_OFF };

describe('Foundation UI', () => {
  let http: HttpTestingController;
  let session: SessionService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
  });
  afterEach(() => http.verify());

  /** The page asks for the time zone list once, right after the first successful household load. */
  function flushZones(zones: string[] = ZONES): void {
    http.expectOne('/api/household/time-zones').flush({ timeZones: zones });
  }

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

  it('renders the subscription overview on member home', async () => {
    session.current.set({ state: 'member', user: { displayName: 'Synthetic' }, expiresAt: '' });
    const harness = await RouterTestingHarness.create('/');
    http.expectOne('/api/subscriptions?period=year-2026').flush({ asOf: '2026-11-13', period: { key: 'year-2026', from: '2026-01-01', to: '2026-12-31', months: 12 }, subscriptions: [], totals: [] });
    flushPeriods(http);
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('No subscriptions yet');
    expect(harness.routeNativeElement?.querySelector('a[href="/subscriptions/new"]')).toBeTruthy();
    expect(harness.routeNativeElement?.textContent).not.toContain('next milestone');
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
    http.expectOne('/api/household').flush({ id: 'household-default', name: 'Before', version: 3, ...zoneFields, members: [] });
    flushZones();
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
    http.expectOne('/api/household').flush({ id: 'household-default', name: 'Latest', version: 4, ...zoneFields, members: [] });
    expect(page.name).toBe('Latest');
    expect(page.conflict()).toBe(false);
    page.name = 'Saved edit';
    page.save();
    http.expectOne('/api/household/name').flush({ id: 'household-default', name: 'Saved edit', version: 5, ...zoneFields, members: [] });
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
    http.expectOne('/api/household').flush({ id: 'household-default', name: 'Before', version: 1, ...zoneFields, members: [] });
    flushZones();
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
    http.expectOne('/api/household').flush({ id: 'household-default', name: 'Before', version: 1, ...zoneFields, members });
    flushZones();
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
    expect(query(harness.fixture, '[data-member-list]').textContent).toContain('Joined 5 Mar 2026');
    page.saved.set(true);
    TestBed.inject(LanguageService).setLanguage('da');
    await harness.fixture.whenStable();
    expect(input.value).toBe('Unsaved Øst');
    expect(page.name).toBe('Unsaved Øst');
    expect(query(harness.fixture, '[data-member-list]').textContent).toContain('Medlem siden 5. mar. 2026');
    expect(harness.routeNativeElement?.textContent).toContain('Husstandens navn er gemt.');
    expect(harness.routeNativeElement?.textContent).toContain('Åse Ørnsø');
    http.expectNone('/api/household');
    expect(TestBed.inject(Router).url).toBe('/household');
  });

  for (const { status, body, block, key } of [
    { status: 400, body: { type: 'urn:subscribed:problem:validation-failed', errors: { name: ['Backend English'] } },
      block: '#name-error', key: 'household.nameInvalid' },
    { status: 412, body: {}, block: '[data-conflict]', key: 'household.conflict' },
    { status: 500, body: {}, block: '[data-problem]', key: 'household.saveFailed' },
    { status: 400, body: { errors: { name: ['Backend English'] } },
      block: '[data-problem]', key: 'household.saveFailed' },
  ] as const) {
    it(`localizes ${status} ${key} and preserves/clears references`, async () => {
      const { harness, page } = await openHousehold();
      page.name = 'Edited';
      page.save();
      http.expectOne('/api/household/name').flush({ ...body, traceId: '00-abc-01' },
        { status, statusText: 'Failure' });
      harness.detectChanges();
      const error = query(harness.fixture, block);
      expect(error.textContent).toContain(en[key]);
      expect(query(error, 'code').textContent).toBe('00-abc-01');
      expect(error.textContent).not.toContain('Backend English');
      TestBed.inject(LanguageService).setLanguage('da');
      await harness.fixture.whenStable();
      expect(error.textContent).toContain(da[key]);
      expect(error.textContent).toContain('Fejlreference:');
      expect(query(error, 'code').textContent).toBe('00-abc-01');
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

  describe('member label editor', () => {
    const members = [
      { memberId: 'aa11', displayLabel: 'Kim', grantedAt: '2026-03-05T12:00:00Z' },
      { memberId: 'b/b 2', displayLabel: 'Sam', grantedAt: '2026-03-06T12:00:00Z' },
    ];
    const household = (version: number, list: Household['members'] = members, name = 'Before') =>
      ({ id: 'household-default', name, version, ...zoneFields, members: list });

    function text(harness: RouterTestingHarness, selector: string): string {
      return query(harness.fixture, selector).textContent ?? '';
    }

    it('opens with the current label, saves with URL, body and If-Match and shows the new label', async () => {
      const { harness, page } = await openHousehold(members);
      harness.detectChanges();
      const edit = queryAll<HTMLButtonElement>(harness.fixture, '[data-member-list] li button')[0];
      expect(edit.textContent).toContain(en['household.memberEdit']);
      edit.click();
      harness.detectChanges();
      await harness.fixture.whenStable();
      const input = harness.routeNativeElement?.querySelector('input[name=memberLabel]') as HTMLInputElement;
      expect(input.value).toBe('Kim');
      expect(harness.routeNativeElement?.querySelector(`label[for=${input.id}]`)).toBeTruthy();
      expect(input.getAttribute('maxlength')).toBe('64');
      input.value = '  Kim Ipsen ';
      input.dispatchEvent(new Event('input'));
      await harness.fixture.whenStable();
      page.saveLabel(members[0]);
      const request = http.expectOne('/api/household/members/aa11/label');
      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({ displayLabel: 'Kim Ipsen' });
      expect(request.request.headers.get('If-Match')).toBe('"1"');
      expect(request.request.headers.get('X-CSRF')).toBe('1');
      request.flush(household(2, [{ ...members[0], displayLabel: 'Kim Ipsen' }, members[1]]));
      harness.detectChanges();
      expect(page.editingMemberId()).toBeNull();
      expect(page.household()?.version).toBe(2);
      expect(text(harness, '[data-member-list]')).toContain('Kim Ipsen');
      expect(text(harness, '[data-member-list]')).toContain('Sam');
      expect(harness.routeNativeElement?.textContent).toContain(en['household.memberSaved']);
      expect(page.name).toBe('Before');
    });

    it('url-encodes the member id path segment', async () => {
      const { page } = await openHousehold(members);
      page.startEdit(members[1]);
      page.labelDraft = 'Sam 2';
      page.saveLabel(members[1]);
      http.expectOne('/api/household/members/b%2Fb%202/label').flush(household(2));
    });

    it('cancel restores the list without a request', async () => {
      const { harness, page } = await openHousehold(members);
      page.startEdit(members[0]);
      page.labelDraft = 'Changed';
      page.cancelEdit();
      harness.detectChanges();
      expect(page.editingMemberId()).toBeNull();
      expect(harness.routeNativeElement?.querySelector('input[name=memberLabel]')).toBeNull();
      expect(text(harness, '[data-member-list]')).toContain('Kim');
      http.expectNone('/api/household/members/aa11/label');
    });

    it('disables Save for invalid labels and does not send them', async () => {
      const { harness, page } = await openHousehold(members);
      page.startEdit(members[0]);
      for (const bad of ['', '   ', 'x'.repeat(65), 'bad\u0007label']) {
        page.labelDraft = bad;
        harness.detectChanges();
        const save = query<HTMLButtonElement>(harness.fixture, 'form[data-member-edit] button[type=submit]');
        expect(save.disabled).toBe(true);
        page.saveLabel(members[0]);
        expect(page.labelError()).toEqual({ key: 'household.memberLabelInvalid' });
      }
      http.expectNone('/api/household/members/aa11/label');
    });

    it('maps a 400 displayLabel error to the field error', async () => {
      const { harness, page } = await openHousehold(members);
      page.startEdit(members[0]);
      page.labelDraft = 'Valid';
      page.saveLabel(members[0]);
      http.expectOne('/api/household/members/aa11/label').flush({
        type: 'urn:subscribed:problem:validation-failed', errors: { displayLabel: ['Backend English'] }, traceId: 'ref-1',
      }, { status: 400, statusText: 'Bad request' });
      harness.detectChanges();
      expect(page.labelError()).toEqual({ key: 'household.memberLabelInvalid' });
      expect(harness.routeNativeElement?.querySelector('input[name=memberLabel][aria-invalid=true]')).toBeTruthy();
      expect(text(harness, '#member-label-error')).toContain(en['household.memberLabelInvalid']);
      expect(text(harness, '#member-label-error')).not.toContain('Backend English');
      expect(text(harness, '#member-label-error code')).toBe('ref-1');
      expect(page.fieldError()).toBeNull();
    });

    it('keeps the draft on 412, offers reload, and resets the draft and conflict after load()', async () => {
      const { harness, page } = await openHousehold(members);
      page.startEdit(members[0]);
      page.labelDraft = 'My edit';
      page.saveLabel(members[0]);
      http.expectOne('/api/household/members/aa11/label').flush({ currentVersion: 2 }, { status: 412, statusText: 'Precondition failed' });
      harness.detectChanges();
      expect(page.labelConflict()).toBe(true);
      expect(page.labelDraft).toBe('My edit');
      expect(text(harness, '[data-member-list] [data-conflict]')).toContain(en['household.conflict']);
      expect(page.conflict()).toBe(false);
      page.load();
      http.expectOne('/api/household').flush(household(2, [{ ...members[0], displayLabel: 'Kim (server)' }, members[1]]));
      harness.detectChanges();
      expect(page.labelConflict()).toBe(false);
      expect(page.labelDraft).toBe('Kim (server)');
      expect(page.editingMemberId()).toBe('aa11');
    });

    it('shows household.saveFailed for 404, 428 and other errors', async () => {
      const { harness, page } = await openHousehold(members);
      page.startEdit(members[0]);
      page.labelDraft = 'Valid';
      for (const status of [404, 428, 500]) {
        page.saveLabel(members[0]);
        http.expectOne('/api/household/members/aa11/label').flush({}, { status, statusText: 'Failure' });
        harness.detectChanges();
        expect(page.labelError()).toEqual({ key: 'household.saveFailed' });
        expect(page.labelConflict()).toBe(false);
        expect(text(harness, '#member-label-error')).toContain(en['household.saveFailed']);
      }
    });

    it('ignores a second submit while saving', async () => {
      const { page } = await openHousehold(members);
      page.startEdit(members[0]);
      page.labelDraft = 'Valid';
      page.saveLabel(members[0]);
      page.saveLabel(members[0]);
      const request = http.expectOne('/api/household/members/aa11/label');
      expect(page.labelSaving()).toBe(true);
      page.cancelEdit();
      expect(page.editingMemberId()).toBe('aa11');
      request.flush(household(2));
      expect(page.labelSaving()).toBe(false);
    });

    it('clears the editor when load() removes the member being edited', async () => {
      const { page } = await openHousehold(members);
      page.startEdit(members[1]);
      page.load();
      http.expectOne('/api/household').flush(household(2, [members[0]]));
      expect(page.editingMemberId()).toBeNull();
      expect(page.labelDraft).toBe('');
    });

    it('never shows or clears the rename banners and does not touch the rename field', async () => {
      const { harness, page } = await openHousehold(members);
      page.name = 'Unsaved rename';
      page.saved.set(true);
      page.conflict.set(true);
      page.problem.set({ key: 'household.saveFailed' });
      page.fieldError.set({ key: 'household.nameInvalid' });
      page.startEdit(members[0]);
      page.labelDraft = 'Valid';
      page.saveLabel(members[0]);
      http.expectOne('/api/household/members/aa11/label').flush({}, { status: 412, statusText: 'Precondition failed' });
      harness.detectChanges();
      expect(page.labelConflict()).toBe(true);
      expect(page.conflict()).toBe(true);
      expect(page.saved()).toBe(true);
      expect(page.problem()).toEqual({ key: 'household.saveFailed' });
      expect(page.fieldError()).toEqual({ key: 'household.nameInvalid' });
      expect(page.name).toBe('Unsaved rename');
      page.labelDraft = 'Valid';
      page.saveLabel(members[0]);
      http.expectOne('/api/household/members/aa11/label').flush(household(2, [{ ...members[0], displayLabel: 'Valid' }, members[1]], 'Server name'));
      expect(page.name).toBe('Unsaved rename');
      expect(page.saved()).toBe(true);
      expect(page.conflict()).toBe(true);
    });

    it('keeps the rename 400 name mapping working with errorDetail', async () => {
      const { page } = await openHousehold(members);
      page.name = 'Valid name';
      page.save();
      http.expectOne('/api/household/name').flush({
        type: 'urn:subscribed:problem:validation-failed', errors: { name: ['Server validation'] },
      }, { status: 400, statusText: 'Bad request' });
      expect(page.fieldError()).toEqual({ key: 'household.nameInvalid' });
      expect(page.labelError()).toBeNull();
    });

    it('has matching en and da keys for the label editor', () => {
      const keys = Object.keys(en).filter((key) => key.startsWith('household.member'));
      expect(keys.length).toBeGreaterThanOrEqual(8);
      for (const key of keys) {
        expect(Object.hasOwn(da, key)).toBe(true);
        expect((da as Record<string, string>)[key]).not.toBe('');
      }
      expect(Object.keys(da).sort()).toEqual(Object.keys(en).sort());
    });
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
      url: '/', key: 'subscriptions.emptyHeading' },
    { state: { state: 'member', user: { displayName: 'Test' }, expiresAt: '' },
      url: '/missing', key: 'notFound.heading' },
    { state: { state: 'anonymous' }, url: '/signed-out?idp=0', key: 'signedOut.idpActive' },
  ] as const) {
    it(`renders ${url} ${state.state} in Danish without English application copy`, async () => {
      session.current.set(state);
      TestBed.inject(LanguageService).setLanguage('da');
      const harness = await RouterTestingHarness.create(url);
      if (state.state === 'member' && url === '/') {
        http.expectOne('/api/subscriptions?period=year-2026').flush({ asOf: '2026-11-13', period: { key: 'year-2026', from: '2026-01-01', to: '2026-12-31', months: 12 }, subscriptions: [], totals: [] });
        flushPeriods(http);
        harness.detectChanges();
      }
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
  describe('time zone selector and calendar feed panel', () => {
    const FEED_ON = { enabled: true, rotatedAt: '2026-11-10T09:00:00Z' };
    type Harness = Awaited<ReturnType<typeof RouterTestingHarness.create>>;

    async function open(over: Partial<Household> = {}, zones: string[] = ZONES) {
      session.current.set({ state: 'member', user: { displayName: 'Synthetic' }, expiresAt: '' });
      const harness = await RouterTestingHarness.create();
      const page = await harness.navigateByUrl('/household', FoundationPage);
      http.expectOne('/api/household').flush({
        id: 'household-default', name: 'Before', version: 1, ...zoneFields, members: [], ...over,
      });
      flushZones(zones);
      harness.detectChanges();
      await harness.fixture.whenStable();
      return { harness, page };
    }

    const root = (harness: Harness) => harness.routeNativeElement as HTMLElement;
    const q = <T extends HTMLElement = HTMLElement>(harness: Harness, selector: string) =>
      root(harness).querySelector(selector) as T | null;
    const click = (harness: Harness, selector: string) => {
      const element = q<HTMLButtonElement>(harness, selector);
      expect(element, selector).toBeTruthy();
      element?.click();
      harness.detectChanges();
    };
    const household = (over: Partial<Household>): Household =>
      ({ id: 'household-default', name: 'Before', version: 2, ...zoneFields, members: [], ...over });

    describe('time zone', () => {
      it('loads the server list, selects the current zone and keeps save off until it changes', async () => {
        const { harness, page } = await open();
        const select = q<HTMLSelectElement>(harness, 'select#household-time-zone');
        expect(select).toBeTruthy();
        expect(root(harness).querySelector('label[for=household-time-zone]')).toBeTruthy();
        expect(Array.from(select?.options ?? []).map((option) => option.value)).toEqual(ZONES);
        expect(select?.value).toBe('Europe/Copenhagen');
        const save = q<HTMLButtonElement>(harness, 'form[ng-reflect-form] button[type=submit]') ??
          Array.from(root(harness).querySelectorAll('button[type=submit]')).find((b) =>
            b.textContent?.includes(en['household.timeZoneSave'])) as HTMLButtonElement;
        expect(save.disabled).toBe(true);
        page.zoneDraft = 'UTC';
        harness.detectChanges();
        expect(save.disabled).toBe(false);
        expect(root(harness).textContent).toContain(en['household.timeZoneHint']);
      });

      it('lists the current zone even when the server list does not carry it', async () => {
        const { harness } = await open({ timeZoneId: 'Asia/Kolkata' });
        const select = q<HTMLSelectElement>(harness, '#household-time-zone');
        expect(Array.from(select?.options ?? []).map((option) => option.value)).toEqual(['Asia/Kolkata', ...ZONES]);
        expect(select?.value).toBe('Asia/Kolkata');
      });

      it('saves through PUT /api/household/time-zone with If-Match and X-CSRF, then uses the new version', async () => {
        const { harness, page } = await open();
        page.zoneDraft = 'Europe/London';
        page.saveTimeZone();
        const request = http.expectOne('/api/household/time-zone');
        expect(request.request.method).toBe('PUT');
        expect(request.request.body).toEqual({ timeZoneId: 'Europe/London' });
        expect(request.request.headers.get('If-Match')).toBe('"1"');
        expect(request.request.headers.get('X-CSRF')).toBe('1');
        request.flush(household({ timeZoneId: 'Europe/London' }));
        harness.detectChanges();
        expect(page.household()?.timeZoneId).toBe('Europe/London');
        expect(page.zoneDraft).toBe('Europe/London');
        expect(page.zoneSaved()).toBe(true);
        expect(root(harness).textContent).toContain(en['household.timeZoneSaved']);
        page.name = 'Renamed';
        page.save();
        expect(http.expectOne('/api/household/name').request.headers.get('If-Match')).toBe('"2"');
      });

      it('does not send a second save while one is in flight', async () => {
        const { page } = await open();
        page.zoneDraft = 'UTC';
        page.saveTimeZone();
        page.saveTimeZone();
        const requests = http.match('/api/household/time-zone');
        expect(requests).toHaveLength(1);
        requests[0].flush(household({ timeZoneId: 'UTC' }));
      });

      it('keeps the choice on 412 and offers the latest household', async () => {
        const { harness, page } = await open();
        page.zoneDraft = 'UTC';
        page.saveTimeZone();
        http.expectOne('/api/household/time-zone').flush(
          { currentVersion: 2, traceId: 'zone-412' }, { status: 412, statusText: 'Precondition failed' });
        harness.detectChanges();
        expect(page.zoneConflict()).toBe(true);
        expect(page.zoneDraft).toBe('UTC');
        const notice = q(harness, '[data-conflict]');
        expect(notice?.textContent).toContain(en['household.conflict']);
        expect(notice?.querySelector('code')?.textContent).toBe('zone-412');
        click(harness, '[data-conflict] button');
        http.expectOne('/api/household').flush(household({ version: 2, timeZoneId: 'Europe/London' }));
        expect(page.zoneDraft).toBe('Europe/London');
        expect(page.zoneConflict()).toBe(false);
        http.expectNone('/api/household/time-zones');
      });

      it('maps a 400 timeZoneId error and other failures to localized messages', async () => {
        const { harness, page } = await open();
        page.zoneDraft = 'UTC';
        page.saveTimeZone();
        http.expectOne('/api/household/time-zone').flush({
          type: 'urn:subscribed:problem:validation-failed', errors: { timeZoneId: ['Backend English'] }, traceId: 'zone-400',
        }, { status: 400, statusText: 'Bad request' });
        harness.detectChanges();
        expect(page.zoneError()).toEqual({ key: 'household.timeZoneInvalid' });
        expect(q(harness, '#time-zone-error')?.textContent).toContain(en['household.timeZoneInvalid']);
        expect(q(harness, '#time-zone-error')?.textContent).not.toContain('Backend English');
        expect(q(harness, '#time-zone-error code')?.textContent).toBe('zone-400');
        expect(q(harness, '#household-time-zone')?.getAttribute('aria-invalid')).toBe('true');
        page.saveTimeZone();
        http.expectOne('/api/household/time-zone').flush({}, { status: 500, statusText: 'Failure' });
        harness.detectChanges();
        expect(page.zoneError()).toEqual({ key: 'household.saveFailed' });
        expect(page.zoneSaving()).toBe(false);
        expect(q(harness, '#household-time-zone')?.getAttribute('aria-invalid')).toBeNull();
      });

      it('refuses a zone that is not in the list without a request', async () => {
        const { page } = await open();
        page.zoneDraft = 'Mars/Olympus_Mons';
        page.saveTimeZone();
        http.expectNone('/api/household/time-zone');
        expect(page.zoneError()).toEqual({ key: 'household.timeZoneInvalid' });
      });

      it('reports a failed zone list, disables save and retries on request', async () => {
        session.current.set({ state: 'member', user: { displayName: 'Synthetic' }, expiresAt: '' });
        const harness = await RouterTestingHarness.create();
        const page = await harness.navigateByUrl('/household', FoundationPage);
        http.expectOne('/api/household').flush({ id: 'household-default', name: 'Before', version: 1, ...zoneFields, members: [] });
        http.expectOne('/api/household/time-zones').flush({}, { status: 503, statusText: 'Unavailable' });
        harness.detectChanges();
        expect(page.timeZonesFailed()).toBe(true);
        expect(root(harness).textContent).toContain(en['household.timeZoneLoadFailed']);
        expect(q<HTMLSelectElement>(harness, '#household-time-zone')?.options.length).toBe(1);
        page.zoneDraft = 'UTC';
        page.saveTimeZone();
        http.expectNone('/api/household/time-zone');
        const retry = Array.from(root(harness).querySelectorAll('button')).find((b) =>
          b.textContent?.trim() === en['household.timeZoneRetry']) as HTMLButtonElement;
        retry.click();
        flushZones();
        harness.detectChanges();
        expect(page.timeZonesFailed()).toBe(false);
        expect(q<HTMLSelectElement>(harness, '#household-time-zone')?.options.length).toBe(ZONES.length);
      });
    });

    describe('calendar feed', () => {
      it('shows the not-created state with the privacy statement and a create button', async () => {
        const { harness } = await open();
        expect(q(harness, '#feed-state')?.textContent).toContain(en['household.feedNone']);
        expect(root(harness).textContent).toContain(en['household.feedIntro']);
        expect(en['household.feedIntro']).toContain('anyone with the link can see subscription names and dates');
        expect(q(harness, '#feed-create')?.textContent?.trim()).toBe(en['household.feedCreate']);
        expect(q(harness, '#feed-revoke')).toBeNull();
        expect(q(harness, '#feed-url')).toBeNull();
      });

      it('shows the active state with the created date and rotate and revoke buttons', async () => {
        const { harness } = await open({ calendarFeed: FEED_ON });
        expect(q(harness, '#feed-state')?.textContent).toContain('created 10 Nov 2026');
        expect(q(harness, '#feed-create')?.textContent?.trim()).toBe(en['household.feedRotate']);
        expect(q(harness, '#feed-revoke')).toBeTruthy();
        expect(q(harness, '#feed-url')).toBeNull();
      });

      it('creates a link without asking, then shows the full URL once with a webcal subscribe link', async () => {
        const { harness, page } = await open();
        click(harness, '#feed-create');
        expect(q(harness, '#feed-rotate-confirm')).toBeNull();
        const request = http.expectOne('/api/household/calendar-feed');
        expect(request.request.method).toBe('POST');
        expect(request.request.headers.get('If-Match')).toBe('"1"');
        expect(request.request.headers.get('X-CSRF')).toBe('1');
        expect(request.request.body).toBeNull();
        request.flush({ household: household({ calendarFeed: FEED_ON }), feedPath: '/calendar/SECRET/subscribed.ics' });
        harness.detectChanges();
        const expected = `${location.origin}/calendar/SECRET/subscribed.ics?lang=en`;
        expect(q<HTMLInputElement>(harness, '#feed-url')?.value).toBe(expected);
        expect(q(harness, '#feed-url-once')?.textContent).toContain(en['household.feedUrlOnce']);
        const webcal = q<HTMLAnchorElement>(harness, '#feed-subscribe');
        expect(webcal?.getAttribute('href')).toBe(expected.replace(/^https?:/u, 'webcal:'));
        expect(webcal?.getAttribute('href')).toMatch(/^webcal:\/\//u);
        expect(page.household()?.calendarFeed.enabled).toBe(true);
        expect(q(harness, '#feed-state')?.textContent).toContain('created 10 Nov 2026');
        expect(root(harness).textContent).toContain(en['household.feedRotate']);
        // The URL is component state only: nothing is kept in the household or in storage.
        expect(JSON.stringify(page.household())).not.toContain('SECRET');
      });

      it('offers no webcal link when the feed path would move the link to another host', async () => {
        const { harness } = await open();
        click(harness, '#feed-create');
        http.expectOne('/api/household/calendar-feed').flush({
          household: household({ calendarFeed: FEED_ON }), feedPath: '@evil.example/subscribed.ics',
        });
        harness.detectChanges();
        expect(q(harness, '#feed-url')).toBeTruthy();
        expect(q(harness, '#feed-subscribe')).toBeNull();
      });

      it('follows the active language at the time of the rotate', async () => {
        const { harness } = await open();
        TestBed.inject(LanguageService).setLanguage('da');
        harness.detectChanges();
        click(harness, '#feed-create');
        http.expectOne('/api/household/calendar-feed').flush({
          household: household({ calendarFeed: FEED_ON }), feedPath: '/calendar/SECRET/subscribed.ics',
        });
        harness.detectChanges();
        expect(q<HTMLInputElement>(harness, '#feed-url')?.value).toBe(`${location.origin}/calendar/SECRET/subscribed.ics?lang=da`);
        expect(q(harness, '#feed-url-once')?.textContent).toContain(da['household.feedUrlOnce']);
        TestBed.inject(LanguageService).setLanguage('en');
        harness.detectChanges();
        // Switching language later does not rewrite the link that was issued.
        expect(q<HTMLInputElement>(harness, '#feed-url')?.value).toContain('?lang=da');
      });

      it('drops the URL on hide, on revoke and on navigation', async () => {
        const { harness, page } = await open();
        const issue = (path: string) => {
          click(harness, '#feed-create');
          if (q(harness, '#feed-rotate-confirm')) click(harness, '[data-rotate-yes]');
          http.expectOne('/api/household/calendar-feed').flush({ household: household({ calendarFeed: FEED_ON }), feedPath: path });
          harness.detectChanges();
          expect(q(harness, '#feed-url')).toBeTruthy();
        };
        issue('/calendar/ONE/subscribed.ics');
        click(harness, '#feed-hide');
        expect(q(harness, '#feed-url')).toBeNull();
        expect(page.feedUrl()).toBeNull();

        issue('/calendar/TWO/subscribed.ics');
        click(harness, '#feed-revoke');
        http.expectOne('/api/household/calendar-feed').flush(household({ version: 4 }));
        harness.detectChanges();
        expect(q(harness, '#feed-url')).toBeNull();

        issue('/calendar/THREE/subscribed.ics');
        await harness.navigateByUrl('/');
        http.expectOne('/api/subscriptions?period=year-2026').flush({ asOf: '2026-11-13', period: { key: 'year-2026', from: '2026-01-01', to: '2026-12-31', months: 12 }, subscriptions: [], totals: [] });
        flushPeriods(http);
        const again = await harness.navigateByUrl('/household', FoundationPage);
        http.expectOne('/api/household').flush(household({ version: 5, calendarFeed: FEED_ON }));
        http.expectOne('/api/household/time-zones').flush({ timeZones: ZONES });
        harness.detectChanges();
        expect(again.feedUrl()).toBeNull();
        expect(root(harness).textContent).not.toContain('THREE');
        expect(q(harness, '#feed-url')).toBeNull();
        expect(q(harness, '#feed-state')?.textContent).toContain(en['household.feedActive'].split('{')[0].trim());
      });

      it('copies the link and reports success', async () => {
        const writeText = vi.fn().mockResolvedValue(undefined);
        Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
        const { harness } = await open();
        click(harness, '#feed-create');
        http.expectOne('/api/household/calendar-feed').flush({
          household: household({ calendarFeed: FEED_ON }), feedPath: '/calendar/SECRET/subscribed.ics',
        });
        harness.detectChanges();
        const copy = Array.from(root(harness).querySelectorAll('#feed-url-block button')).find((b) =>
          b.textContent?.trim() === en['household.feedCopy']) as HTMLButtonElement;
        copy.click();
        await harness.fixture.whenStable();
        harness.detectChanges();
        expect(writeText).toHaveBeenCalledWith(`${location.origin}/calendar/SECRET/subscribed.ics?lang=en`);
        expect(q(harness, '#feed-url-block')?.textContent).toContain(en['household.feedCopied']);
      });

      it('falls back to a selected, visible link when the clipboard is unavailable', async () => {
        Object.defineProperty(navigator, 'clipboard', {
          value: { writeText: vi.fn().mockRejectedValue(new DOMException('Denied', 'NotAllowedError')) }, configurable: true,
        });
        const { harness } = await open();
        click(harness, '#feed-create');
        http.expectOne('/api/household/calendar-feed').flush({
          household: household({ calendarFeed: FEED_ON }), feedPath: '/calendar/SECRET/subscribed.ics',
        });
        harness.detectChanges();
        const input = q<HTMLInputElement>(harness, '#feed-url') as HTMLInputElement;
        const select = vi.spyOn(input, 'select');
        const copy = Array.from(root(harness).querySelectorAll('#feed-url-block button')).find((b) =>
          b.textContent?.trim() === en['household.feedCopy']) as HTMLButtonElement;
        copy.click();
        await harness.fixture.whenStable();
        harness.detectChanges();
        expect(select).toHaveBeenCalled();
        expect(input.readOnly).toBe(true);
        expect(input.hidden).toBe(false);
        expect(q(harness, '#feed-url-block')?.textContent).toContain(en['household.feedCopyUnavailable']);
      });

      it('asks before rotating, says old links stop working, and sends nothing until confirmed', async () => {
        const { harness, page } = await open({ calendarFeed: FEED_ON });
        click(harness, '#feed-create');
        http.expectNone('/api/household/calendar-feed');
        expect(q(harness, '#feed-rotate-confirm')?.textContent).toContain(en['household.feedRotateConfirm']);
        expect(en['household.feedRotateConfirm']).toContain('old link stops working');
        expect(q(harness, '#feed-create')).toBeNull();
        // Cancel keeps the current link and sends nothing.
        click(harness, '[data-rotate-no]');
        expect(page.confirmingRotate()).toBe(false);
        expect(q(harness, '#feed-rotate-confirm')).toBeNull();
        http.expectNone('/api/household/calendar-feed');
        // Confirm rotates.
        click(harness, '#feed-create');
        click(harness, '[data-rotate-yes]');
        const request = http.expectOne('/api/household/calendar-feed');
        expect(request.request.method).toBe('POST');
        request.flush({ household: household({ calendarFeed: { enabled: true, rotatedAt: '2026-11-17T08:00:00Z' } }), feedPath: '/calendar/NEW/subscribed.ics' });
        harness.detectChanges();
        expect(q<HTMLInputElement>(harness, '#feed-url')?.value).toContain('/calendar/NEW/');
        expect(q(harness, '#feed-rotate-confirm')).toBeNull();
        expect(q(harness, '#feed-state')?.textContent).toContain('created 17 Nov 2026');
      });

      it('revokes with DELETE and If-Match and shows the revoked state', async () => {
        const { harness, page } = await open({ calendarFeed: FEED_ON });
        click(harness, '#feed-revoke');
        const request = http.expectOne('/api/household/calendar-feed');
        expect(request.request.method).toBe('DELETE');
        expect(request.request.headers.get('If-Match')).toBe('"1"');
        expect(request.request.headers.get('X-CSRF')).toBe('1');
        request.flush(household({}));
        harness.detectChanges();
        expect(page.household()?.calendarFeed.enabled).toBe(false);
        expect(q(harness, '#feed-state')?.textContent).toContain(en['household.feedNone']);
        expect(root(harness).textContent).toContain(en['household.feedRevoked']);
        expect(q(harness, '#feed-revoke')).toBeNull();
        expect(q(harness, '#feed-create')?.textContent?.trim()).toBe(en['household.feedCreate']);
      });

      it('shows conflict and failure messages for create and revoke and keeps the state', async () => {
        const { harness, page } = await open({ calendarFeed: FEED_ON });
        click(harness, '#feed-revoke');
        http.expectOne('/api/household/calendar-feed').flush(
          { currentVersion: 2, traceId: 'feed-412' }, { status: 412, statusText: 'Precondition failed' });
        harness.detectChanges();
        expect(page.feedConflict()).toBe(true);
        expect(q(harness, '[data-conflict]')?.textContent).toContain(en['household.conflict']);
        expect(q(harness, '[data-conflict] code')?.textContent).toBe('feed-412');
        expect(page.household()?.calendarFeed.enabled).toBe(true);
        click(harness, '[data-conflict] button');
        http.expectOne('/api/household').flush(household({ version: 2, calendarFeed: FEED_ON }));
        harness.detectChanges();
        expect(page.feedConflict()).toBe(false);
        expect(page.feedTraceId()).toBeUndefined();

        click(harness, '#feed-create');
        click(harness, '[data-rotate-yes]');
        http.expectOne('/api/household/calendar-feed').flush({}, { status: 500, statusText: 'Failure' });
        harness.detectChanges();
        expect(page.feedError()).toEqual({ key: 'household.saveFailed' });
        expect(page.feedBusy()).toBe(false);
        expect(page.feedUrl()).toBeNull();
        expect(root(harness).textContent).toContain(en['household.saveFailed']);
      });

      it('ignores a second click while a request is in flight', async () => {
        const { harness, page } = await open();
        page.createOrRotateFeed();
        page.createOrRotateFeed();
        page.revokeFeed();
        harness.detectChanges();
        expect(q<HTMLButtonElement>(harness, '#feed-create')?.disabled).toBe(true);
        http.expectOne('/api/household/calendar-feed').flush({
          household: household({ calendarFeed: FEED_ON }), feedPath: '/calendar/SECRET/subscribed.ics',
        });
      });
    });

    it('renders both sections in Danish without English application copy', async () => {
      const { harness } = await open({ calendarFeed: FEED_ON });
      TestBed.inject(LanguageService).setLanguage('da');
      harness.detectChanges();
      const text = root(harness).textContent ?? '';
      for (const key of ['household.timeZoneHeading', 'household.timeZoneLabel', 'household.timeZoneHint',
        'household.feedHeading', 'household.feedIntro', 'household.feedRotate', 'household.feedRevoke'] as const) {
        expect(text, key).toContain(da[key]);
      }
      expect(text).not.toContain(en['household.feedIntro']);
      expect(text).toContain('oprettet 10. nov. 2026');
    });

    it('keeps the en and da catalogs in step for every new household key', () => {
      const keys = Object.keys(en).filter((key) => /^household\.(timeZone|feed)/u.test(key));
      expect(keys).toHaveLength(28);
      for (const key of keys) {
        expect(Object.hasOwn(da, key), key).toBe(true);
        expect(da[key as keyof typeof da]).not.toBe('');
      }
    });
  });
});
