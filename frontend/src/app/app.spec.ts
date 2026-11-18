import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { App } from './app';
import { routes } from './app.routes';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService } from './i18n/i18n';
import { Session, SessionService, csrfInterceptor } from './session';
import { SIDEBAR_STORAGE } from './sidebar/sidebar';
import { COLOR_SCHEME_QUERY, THEME_STORAGE, ThemeService } from './theme/theme';
import { APP_VERSION } from './version';
import { query, queryAll } from './testing/query';
import { flushPeriods, periodTestProviders } from './subscriptions/testing/period-providers';
import './app.config';

function fakeSidebarStorage(initial?: string) {
  const values = new Map<string, string>(initial === undefined ? [] : [['subscribed.sidebar', initial]]);
  return {
    values,
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
}

describe('Language selector', () => {
  let http: HttpTestingController;
  const setItem = vi.fn();
  const getItem = vi.fn<() => string | null>();

  beforeEach(() => {
    setItem.mockReset();
    getItem.mockReset().mockReturnValue(null);
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem, setItem } },
        { provide: THEME_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
        { provide: COLOR_SCHEME_QUERY, useValue: null },
        { provide: SIDEBAR_STORAGE, useValue: fakeSidebarStorage() },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  async function render(session: Session = { state: 'anonymous' }) {
    const fixture = TestBed.createComponent(App);
    if (session.state !== 'loading') {
      http.expectOne('/bff/session').flush(session);
    }
    await TestBed.inject(Router).navigateByUrl('/');
    await fixture.whenStable();
    if (session.state === 'member') await flushOverview(fixture);
    return fixture;
  }

  async function flushOverview(fixture: ComponentFixture<App>) {
    http.expectOne('/api/subscriptions?period=year-2026').flush({ asOf: '2026-11-13', period: { key: 'year-2026', from: '2026-01-01', to: '2026-12-31', months: 12 }, subscriptions: [], totals: [] });
    flushPeriods(http);
    await fixture.whenStable();
  }

  function select(fixture: ComponentFixture<App>, value: string) {
    const select = fixture.nativeElement.querySelector('#language-select') as HTMLSelectElement;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    return select;
  }

  it('shows the saved Danish selection on the initial render', async () => {
    getItem.mockReturnValue('da');
    const fixture = await render();
    expect(fixture.nativeElement.querySelector('#language-select').value).toBe('da');
    expect(fixture.nativeElement.querySelector('h1').textContent).toBe('Velkommen til Subscribed');
    expect(document.documentElement.lang).toBe('da');
  });

  it('switches text and accessibility labels without navigating or reloading', async () => {
    const member: Session = { state: 'member', user: { displayName: '<b>Åse Ørn</b>', memberId: '1' }, expiresAt: '' };
    const fixture = await render(member);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl');
    const load = vi.spyOn(TestBed.inject(SessionService), 'load');
    expect(fixture.nativeElement.querySelector('label[for=language-select]').textContent).toBe('Language');
    const selector = select(fixture, 'da');
    await fixture.whenStable();
    expect(selector.value).toBe('da');
    expect(fixture.nativeElement.querySelector('label[for=language-select]').textContent).toBe('Sprog');
    expect(fixture.nativeElement.querySelector('nav').getAttribute('aria-label')).toBe('Primær');
    expect(fixture.nativeElement.querySelector('h1').textContent).toBe('Velkommen, <b>Åse Ørn</b>');
    expect(fixture.nativeElement.querySelector('h1 b')).toBeNull();
    expect(document.documentElement.lang).toBe('da');
    expect(router.url).toBe('/');
    expect(navigate).not.toHaveBeenCalled();
    expect(load).not.toHaveBeenCalled();
    expect(TestBed.inject(SessionService).current()).toEqual(member);
    expect(setItem).toHaveBeenCalledWith('subscribed.language', 'da');
    http.expectNone(() => true);
  });

  it('renders the selector while loading and after sign-out', async () => {
    const fixture = await render({ state: 'loading' });
    expect(fixture.nativeElement.querySelector('#language-select')).toBeTruthy();
    http.expectOne('/bff/session').flush({ state: 'anonymous' });
    await TestBed.inject(Router).navigateByUrl('/signed-out?idp=0');
    select(fixture, 'da');
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('h1').textContent).toBe('Du er logget ud');
    expect(fixture.nativeElement.querySelector('#language-select').value).toBe('da');
  });

  it('retranslates an already-visible sign-out error', async () => {
    const fixture = await render({ state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' });
    TestBed.inject(SessionService).signOut();
    http.expectOne('/bff/logout').flush({}, { status: 503, statusText: 'Unavailable' });
    await fixture.whenStable();
    expect(query(fixture, 'app-notice [role=alert]').textContent).toContain('Sign out failed');
    select(fixture, 'da');
    await fixture.whenStable();
    expect(query(fixture, 'app-notice [role=alert]').textContent).toContain('Udlogningen mislykkedes');
  });

  it('reports persistence failure with a dismissible localized notice', async () => {
    setItem.mockImplementation(() => { throw new DOMException('Full', 'QuotaExceededError'); });
    const fixture = await render();
    select(fixture, 'da');
    await fixture.whenStable();
    const notice = query(fixture, 'app-notice [role=status]');
    expect(notice.textContent).toContain('Dit sprogvalg kunne ikke gemmes');
    query<HTMLButtonElement>(notice, 'button').click();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('app-notice')).toBeNull();
    expect(TestBed.inject(LanguageService).language()).toBe('da');
  });
});

describe('Theme selector', () => {
  let http: HttpTestingController;
  let values: Map<string, string>;
  let failWrites: boolean;
  const storage = {
    getItem: vi.fn((key: string) => values.get(key) ?? null),
    setItem: vi.fn((key: string, value: string) => {
      if (failWrites) throw new DOMException('Full', 'QuotaExceededError');
      values.set(key, value);
    }),
  };
  const member: Session = { state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' };

  beforeEach(() => {
    values = new Map();
    failWrites = false;
    storage.getItem.mockClear();
    storage.setItem.mockClear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: storage },
        { provide: THEME_STORAGE, useValue: storage },
        { provide: COLOR_SCHEME_QUERY, useValue: null },
        { provide: SIDEBAR_STORAGE, useValue: fakeSidebarStorage() },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    document.documentElement.removeAttribute('data-theme');
  });

  async function render(session: Session = { state: 'anonymous' }, url = '/') {
    const fixture = TestBed.createComponent(App);
    if (session.state !== 'loading') http.expectOne('/bff/session').flush(session);
    await TestBed.inject(Router).navigateByUrl(url);
    await fixture.whenStable();
    if (session.state === 'member' && url === '/') {
      http.expectOne('/api/subscriptions?period=year-2026').flush({ asOf: '2026-11-13', period: { key: 'year-2026', from: '2026-01-01', to: '2026-12-31', months: 12 }, subscriptions: [], totals: [] });
      flushPeriods(http);
      await fixture.whenStable();
    }
    return fixture;
  }

  const themeSelect = (fixture: ComponentFixture<App>) =>
    fixture.nativeElement.querySelector('#theme-select') as HTMLSelectElement;

  function choose(fixture: ComponentFixture<App>, id: string, value: string) {
    const select = fixture.nativeElement.querySelector('#' + id) as HTMLSelectElement;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    return select;
  }

  it.each<Session>([
    { state: 'loading' },
    { state: 'anonymous' },
    { state: 'unavailable', traceId: 'synthetic-ref' },
    { state: 'not-member', user: { displayName: 'Synthetic', memberId: '2' }, expiresAt: '' },
    member,
  ])('renders a labelled System/Light/Dark selector for the $state state', async (session) => {
    const fixture = await render(session);
    const select = themeSelect(fixture);
    expect(select).toBeTruthy();
    expect(fixture.nativeElement.querySelector('label[for=theme-select]').textContent).toBe('Theme');
    expect(Array.from(select.options).map((option) => [option.value, option.textContent])).toEqual([
      ['system', 'System'], ['light', 'Light'], ['dark', 'Dark'],
    ]);
    expect(select.value).toBe('system');
    expect(select.closest('nav')).toBeNull();
    if (session.state === 'loading') http.expectOne('/bff/session').flush({ state: 'anonymous' });
  });

  it('renders the selector after sign-out', async () => {
    const fixture = await render({ state: 'anonymous' }, '/signed-out?idp=0');
    expect(themeSelect(fixture)).toBeTruthy();
    choose(fixture, 'theme-select', 'dark');
    await fixture.whenStable();
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('applies a choice live without navigation, session reload, or losing input and focus', async () => {
    const fixture = await render(member, '/household');
    http.expectOne('/api/household').flush({
      id: 'household-default', name: 'Before', version: 1, timeZoneId: 'UTC',
      calendarFeed: { enabled: false, rotatedAt: null }, members: [],
    });
    http.expectOne('/api/household/time-zones').flush({ timeZones: ['UTC'] });
    await fixture.whenStable();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl');
    const navigateTo = vi.spyOn(router, 'navigate');
    const load = vi.spyOn(TestBed.inject(SessionService), 'load');
    const input = fixture.nativeElement.querySelector('#household-name') as HTMLInputElement;
    input.value = 'Typed but unsaved';
    input.dispatchEvent(new Event('input'));
    input.focus();

    choose(fixture, 'theme-select', 'dark');
    await fixture.whenStable();

    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(themeSelect(fixture).value).toBe('dark');
    expect(router.url).toBe('/household');
    expect(navigate).not.toHaveBeenCalled();
    expect(navigateTo).not.toHaveBeenCalled();
    expect(load).not.toHaveBeenCalled();
    expect((fixture.nativeElement.querySelector('#household-name') as HTMLInputElement).value).toBe('Typed but unsaved');
    expect(document.activeElement).toBe(input);
    expect(values.get('subscribed.theme')).toBe('dark');
    http.expectNone(() => true);
  });

  it('ignores values outside the supported modes', async () => {
    const fixture = await render();
    fixture.componentInstance.onTheme('auto');
    await fixture.whenStable();
    expect(TestBed.inject(ThemeService).mode()).toBe('system');
    expect(storage.setItem).not.toHaveBeenCalled();
  });

  it('localizes the selector and a dismissible persistence notice in Danish', async () => {
    failWrites = true;
    values.set('subscribed.language', 'da');
    const fixture = await render();
    expect(fixture.nativeElement.querySelector('label[for=theme-select]').textContent).toBe('Tema');
    expect(Array.from(themeSelect(fixture).options).map((option) => option.textContent))
      .toEqual(['Følg systemet', 'Lys', 'Mørk']);
    choose(fixture, 'theme-select', 'light');
    await fixture.whenStable();
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    const notice = query(fixture, 'app-notice [role=status]');
    expect(notice.textContent).toContain('Dit valg af tema kunne ikke gemmes');
    const dismiss = query<HTMLButtonElement>(notice, 'button');
    expect(dismiss.textContent).toBe('Luk');
    dismiss.click();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('app-notice')).toBeNull();
    expect(TestBed.inject(ThemeService).mode()).toBe('light');
  });

  it('keeps theme and language preferences independent', async () => {
    const fixture = await render();
    choose(fixture, 'theme-select', 'dark');
    await fixture.whenStable();
    expect(storage.setItem.mock.calls).toEqual([['subscribed.theme', 'dark']]);
    storage.setItem.mockClear();
    choose(fixture, 'language-select', 'da');
    await fixture.whenStable();
    expect(storage.setItem.mock.calls).toEqual([['subscribed.language', 'da']]);
    expect(TestBed.inject(ThemeService).mode()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });
});

describe('Footer', () => {
  let http: HttpTestingController;
  const member: Session = { state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
        { provide: THEME_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
        { provide: COLOR_SCHEME_QUERY, useValue: null },
        { provide: SIDEBAR_STORAGE, useValue: fakeSidebarStorage() },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  async function render(session: Session = { state: 'anonymous' }) {
    const fixture = TestBed.createComponent(App);
    if (session.state !== 'loading') http.expectOne('/bff/session').flush(session);
    await TestBed.inject(Router).navigateByUrl('/');
    await fixture.whenStable();
    if (session.state === 'member') {
      http.expectOne('/api/subscriptions?period=year-2026').flush({ asOf: '2026-11-13', period: { key: 'year-2026', from: '2026-01-01', to: '2026-12-31', months: 12 }, subscriptions: [], totals: [] });
      flushPeriods(http);
      await fixture.whenStable();
    }
    return fixture;
  }

  it.each<Session>([
    { state: 'loading' },
    { state: 'anonymous' },
    { state: 'not-member', user: { displayName: 'Synthetic', memberId: '2' }, expiresAt: '' },
    member,
  ])('renders one footer with a safe GitHub link and the version for the $state state', async (session) => {
    const fixture = await render(session);
    const footers = fixture.nativeElement.querySelectorAll('footer');
    expect(footers.length).toBe(1);
    const link = footers[0].querySelector('a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://github.com/kimipsen/subscribed');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toContain('noopener');
    expect(link.getAttribute('rel')).toContain('noreferrer');
    expect(link.textContent).toBe('Source on GitHub');
    expect(footers[0].textContent).toContain('Version');
    expect(footers[0].querySelector('code')?.textContent).toBe(APP_VERSION);
    if (session.state === 'loading') http.expectOne('/bff/session').flush({ state: 'anonymous' });
  });

  it('localizes the link label in Danish', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const fixture = await render();
    expect(fixture.nativeElement.querySelector('footer a').textContent).toBe('Kildekode på GitHub');
  });
});

describe('Collapsible sidebar', () => {
  let http: HttpTestingController;
  let storage: ReturnType<typeof fakeSidebarStorage>;
  const member: Session = { state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' };

  function configure(sidebarStorage: unknown) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
        { provide: THEME_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
        { provide: COLOR_SCHEME_QUERY, useValue: null },
        { provide: SIDEBAR_STORAGE, useValue: sidebarStorage },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  }

  beforeEach(() => {
    storage = fakeSidebarStorage();
    configure(storage);
  });
  afterEach(() => http.verify());

  async function render(session: Session = member, url = '/') {
    const fixture = TestBed.createComponent(App);
    http.expectOne('/bff/session').flush(session);
    await TestBed.inject(Router).navigateByUrl(url);
    await fixture.whenStable();
    if (session.state === 'member') {
      if (url === '/payment-options') {
        http.expectOne('/api/payment-options').flush({ options: [] });
        http.match((request) => request.url.startsWith('/api/payment-options/')).forEach((r) => r.flush({ suggestions: [] }));
      } else {
        http.expectOne('/api/subscriptions?period=year-2026').flush({ asOf: '2026-11-13', period: { key: 'year-2026', from: '2026-01-01', to: '2026-12-31', months: 12 }, subscriptions: [], totals: [] });
        flushPeriods(http);
      }
      await fixture.whenStable();
    }
    return fixture;
  }

  const el = (fixture: ComponentFixture<App>) => fixture.nativeElement as HTMLElement;
  const toggle = (fixture: ComponentFixture<App>) => query<HTMLButtonElement>(fixture, 'nav button[aria-controls="primary-nav"]');
  const menuButton = (fixture: ComponentFixture<App>) => query<HTMLButtonElement>(fixture, 'header button[aria-controls="primary-nav"]');
  const layout = (fixture: ComponentFixture<App>) => query(fixture, '[data-collapsed]');
  const collapsed = (fixture: ComponentFixture<App>) => layout(fixture).getAttribute('data-collapsed');
  const links = (fixture: ComponentFixture<App>) => queryAll<HTMLAnchorElement>(fixture, 'nav a', { min: 3 });
  const labelOf = (link: HTMLElement) => query(link, '[data-nav-label]').textContent?.trim();
  const toggleLabel = (fixture: ComponentFixture<App>) => query(toggle(fixture), '[data-nav-toggle-label]').textContent;

  async function click(fixture: ComponentFixture<App>) {
    toggle(fixture).click();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('renders an expanded toggle with the expected attributes', async () => {
    const fixture = await render();
    const button = toggle(fixture);
    expect(button.getAttribute('type')).toBe('button');
    expect(button.getAttribute('aria-controls')).toBe('primary-nav');
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(button.getAttribute('aria-label')).toBe('Collapse sidebar');
    expect(query(fixture, 'nav').id).toBe('primary-nav');
    expect(collapsed(fixture)).toBe('false');
    expect(layout(fixture).classList.contains('group')).toBe(true);
  });

  it('flips aria-expanded, the label and the icon on toggle', async () => {
    const fixture = await render();
    const iconBefore = query(toggle(fixture), 'svg').outerHTML;
    await click(fixture);
    expect(toggle(fixture).getAttribute('aria-expanded')).toBe('false');
    expect(toggle(fixture).getAttribute('aria-label')).toBe('Expand sidebar');
    expect(toggleLabel(fixture)).toBe('Expand sidebar');
    const iconAfter = query(toggle(fixture), 'svg').outerHTML;
    expect(iconAfter).toBeTruthy();
    expect(iconAfter).not.toBe(iconBefore);
    await click(fixture);
    expect(toggle(fixture).getAttribute('aria-expanded')).toBe('true');
    expect(toggle(fixture).getAttribute('aria-label')).toBe('Collapse sidebar');
    expect(query(toggle(fixture), 'svg').outerHTML).toBe(iconBefore);
  });

  it('shows the collapse label inside the toggle', async () => {
    const fixture = await render();
    expect(toggleLabel(fixture)).toBe('Collapse sidebar');
  });

  it('puts data-collapsed and the group class on the layout wrapper and never on the nav', async () => {
    const fixture = await render();
    await click(fixture);
    expect(collapsed(fixture)).toBe('true');
    expect(layout(fixture).classList.contains('group')).toBe(true);
    expect(layout(fixture).contains(query(fixture, 'nav'))).toBe(true);
    expect(query(fixture, 'nav').hasAttribute('data-collapsed')).toBe(false);
    await click(fixture);
    expect(collapsed(fixture)).toBe('false');
  });

  it('keeps every nav label in the DOM, never hidden, in both states', async () => {
    const fixture = await render();
    for (const expectedCollapsed of ['false', 'true']) {
      expect(collapsed(fixture)).toBe(expectedCollapsed);
      expect(queryAll(fixture, '[data-nav-label]', { min: 3 }).map((label) => label.textContent?.trim())).toEqual(['Home', 'Household', 'Payment options']);
      for (const label of queryAll(fixture, '[data-nav-label]')) {
        for (let node: HTMLElement | null = label; node && node.id !== 'primary-nav'; node = node.parentElement) {
          expect(node.classList.contains('hidden'), node.outerHTML).toBe(false);
        }
        expect(label.classList.contains('nav:group-data-[collapsed=true]:sr-only')).toBe(true);
      }
      if (expectedCollapsed === 'false') await click(fixture);
    }
  });

  it('gives each tooltip as an aria-hidden child and never a data-tip attribute', async () => {
    const fixture = await render();
    await click(fixture);
    for (const link of links(fixture)) {
      const tip = query(link, '.tooltip-content');
      expect(tip.getAttribute('aria-hidden')).toBe('true');
      expect(tip.textContent?.trim()).toBe(labelOf(link));
      expect(link.hasAttribute('data-tip')).toBe(false);
    }
  });

  it('renders an svg in the toggle and in each link, plus a data-nav-label with no title attribute', async () => {
    const fixture = await render();
    expect(query(toggle(fixture), 'svg')).toBeTruthy();
    const all = links(fixture);
    expect(all.map((a) => labelOf(a))).toEqual(['Home', 'Household', 'Payment options']);
    for (const link of all) {
      expect(query(link, 'svg')).toBeTruthy();
      expect(link.hasAttribute('title')).toBe(false);
      expect(query(link, '[data-nav-label]').hasAttribute('title')).toBe(false);
    }
    expect(toggle(fixture).hasAttribute('title')).toBe(false);
  });

  it('localizes the toggle and the Payment options link in Danish', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const fixture = await render();
    expect(toggle(fixture).getAttribute('aria-label')).toBe('Skjul sidepanel');
    expect(links(fixture).map((a) => labelOf(a))).toEqual(['Forside', 'Husstand', 'Betalingsmetoder']);
    await click(fixture);
    expect(toggle(fixture).getAttribute('aria-label')).toBe('Vis sidepanel');
    expect(toggleLabel(fixture)).toBe('Vis sidepanel');
  });

  it('links Payment options to /payment-options and marks it active with aria-current', async () => {
    const fixture = await render(member, '/payment-options');
    const [home, household, payment] = links(fixture);
    expect(payment.getAttribute('href')).toBe('/payment-options');
    expect(payment.getAttribute('aria-current')).toBe('page');
    expect(household.hasAttribute('aria-current')).toBe(false);
    expect(home.hasAttribute('aria-current')).toBe(false);
  });

  it('persists the state across a new App fixture', async () => {
    const first = await render();
    await click(first);
    expect(storage.values.get('subscribed.sidebar')).toBe('collapsed');
    first.destroy();
    TestBed.resetTestingModule();
    configure(storage);
    const second = await render();
    expect(collapsed(second)).toBe('true');
    expect(toggle(second).getAttribute('aria-expanded')).toBe('false');
  });

  it('still toggles for the session when storage throws', async () => {
    TestBed.resetTestingModule();
    configure({
      getItem: () => { throw new DOMException('Blocked', 'SecurityError'); },
      setItem: () => { throw new DOMException('Full', 'QuotaExceededError'); },
    });
    const fixture = await render();
    await click(fixture);
    expect(collapsed(fixture)).toBe('true');
    expect(el(fixture).querySelector('app-notice')).toBeNull();
    await click(fixture);
    expect(collapsed(fixture)).toBe('false');
  });

  it('keeps the Menu button working with a stored collapsed value', async () => {
    TestBed.resetTestingModule();
    configure(fakeSidebarStorage('collapsed'));
    const fixture = await render();
    const menu = menuButton(fixture);
    const nav = query(fixture, 'nav');
    expect(menu.getAttribute('aria-expanded')).toBe('false');
    expect(nav.getAttribute('data-open')).toBe('false');
    menu.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(menu.getAttribute('aria-expanded')).toBe('true');
    expect(nav.getAttribute('data-open')).toBe('true');
    expect(collapsed(fixture)).toBe('true');
  });

  it.each<Session>([
    { state: 'anonymous' },
    { state: 'unavailable', traceId: 'synthetic-ref' },
    { state: 'not-member', user: { displayName: 'Synthetic', memberId: '2' }, expiresAt: '' },
  ])('renders no nav or toggle for the $state state', async (session) => {
    const fixture = await render(session);
    expect(el(fixture).querySelector('nav')).toBeNull();
    expect(el(fixture).querySelector('[data-nav-label]')).toBeNull();
    expect(el(fixture).querySelector('[aria-controls=primary-nav]')).toBeNull();
  });
});
