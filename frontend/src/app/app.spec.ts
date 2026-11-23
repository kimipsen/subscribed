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
import { installDialogStub, restoreDialogStub } from './testing/dialog-stub';
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
    expect(fixture.nativeElement.querySelector('#primary-nav').getAttribute('aria-label')).toBe('Primær');
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
    // The tokens panel is a lazy chunk; its list request follows once it has mounted.
    const tokens = await vi.waitFor(() => {
      fixture.detectChanges();
      return http.expectOne('/api/household/mcp-tokens');
    });
    tokens.flush({ tokens: [] });
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
    installDialogStub();
    storage = fakeSidebarStorage();
    configure(storage);
  });
  afterEach(() => {
    http.verify();
    // Destroying the fixture is what releases the scroll lock of a drawer a spec left open.
    TestBed.resetTestingModule();
    restoreDialogStub();
    expect(document.body.classList.contains('overflow-hidden')).toBe(false);
  });

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
  const toggle = (fixture: ComponentFixture<App>) => query<HTMLButtonElement>(fixture, '#primary-nav button[aria-controls="primary-nav"]');
  const menuButton = (fixture: ComponentFixture<App>) => query<HTMLButtonElement>(fixture, 'header button[aria-controls="mobile-nav"]');
  const layout = (fixture: ComponentFixture<App>) => query(fixture, '[data-collapsed]');
  const collapsed = (fixture: ComponentFixture<App>) => layout(fixture).getAttribute('data-collapsed');
  const links = (fixture: ComponentFixture<App>) => queryAll<HTMLAnchorElement>(fixture, '#primary-nav a', { min: 3 });
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
    expect(query(fixture, '#primary-nav').tagName).toBe('NAV');
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
    expect(layout(fixture).contains(query(fixture, '#primary-nav'))).toBe(true);
    expect(query(fixture, '#primary-nav').hasAttribute('data-collapsed')).toBe(false);
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
    const nav = query(fixture, '#primary-nav');
    const dialog = query<HTMLDialogElement>(fixture, 'dialog#mobile-nav');
    expect(menu.getAttribute('aria-expanded')).toBe('false');
    expect(nav.classList.contains('max-nav:hidden')).toBe(true);
    expect(nav.hasAttribute('data-open')).toBe(false);
    expect(dialog.open).toBe(false);
    menu.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(menu.getAttribute('aria-expanded')).toBe('true');
    expect(dialog.open).toBe(true);
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
    expect(el(fixture).querySelector('[aria-controls=mobile-nav]')).toBeNull();
  });
});

describe('Mobile navigation drawer', () => {
  let http: HttpTestingController;
  let matchMediaBefore: typeof window.matchMedia | undefined;
  const member: Session = { state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' };
  const notMember: Session = { state: 'not-member', user: { displayName: 'Synthetic', subject: 'synthetic-subject' }, expiresAt: '' };

  beforeEach(() => {
    installDialogStub();
    matchMediaBefore = window.matchMedia;
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
  afterEach(() => {
    http.verify();
    // Destroying the fixture is what releases the scroll lock of a drawer a spec left open.
    TestBed.resetTestingModule();
    restoreDialogStub();
    if (matchMediaBefore) window.matchMedia = matchMediaBefore;
    else delete (window as { matchMedia?: unknown }).matchMedia;
    vi.restoreAllMocks();
    expect(document.body.classList.contains('overflow-hidden')).toBe(false);
  });

  async function render(session: Session = member, url = '/') {
    const fixture = TestBed.createComponent(App);
    http.expectOne('/bff/session').flush(session);
    await TestBed.inject(Router).navigateByUrl(url);
    await fixture.whenStable();
    if (session.state === 'member' && url === '/') {
      http.expectOne('/api/subscriptions?period=year-2026').flush({ asOf: '2026-11-13', period: { key: 'year-2026', from: '2026-01-01', to: '2026-12-31', months: 12 }, subscriptions: [], totals: [] });
      flushPeriods(http);
      await fixture.whenStable();
    }
    return fixture;
  }

  /** Change detection runs the component effect that opens or closes the dialog. */
  async function settle(fixture: ComponentFixture<App>) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(fixture: ComponentFixture<App>) {
    fixture.componentInstance.menuOpen.set(true);
    await settle(fixture);
  }

  function fakeMatchMedia() {
    const listeners = new Set<(event: MediaQueryListEvent) => void>();
    const list = {
      matches: false,
      media: '(min-width: 900px)',
      addEventListener: vi.fn((_type: string, listener: (event: MediaQueryListEvent) => void) => { listeners.add(listener); }),
      removeEventListener: vi.fn((_type: string, listener: (event: MediaQueryListEvent) => void) => { listeners.delete(listener); }),
    };
    window.matchMedia = vi.fn(() => list as unknown as MediaQueryList);
    return { list, listeners, fire: (matches: boolean) => listeners.forEach((listener) => listener({ matches } as MediaQueryListEvent)) };
  }

  const dialog = (fixture: ComponentFixture<App>) => query<HTMLDialogElement>(fixture, 'dialog#mobile-nav');
  const menuButton = (fixture: ComponentFixture<App>) => query<HTMLButtonElement>(fixture, 'header button[aria-controls="mobile-nav"]');
  const drawerButtons = (fixture: ComponentFixture<App>) => queryAll<HTMLButtonElement>(dialog(fixture), 'button');
  const signOutButton = (fixture: ComponentFixture<App>) => drawerButtons(fixture).find((button) => button.textContent?.trim() === 'Sign out')!;
  const closeButton = (fixture: ComponentFixture<App>) => drawerButtons(fixture).find((button) => button.textContent?.trim() === 'Close menu')!;
  const bodyLocked = () => document.body.classList.contains('overflow-hidden');

  describe('header below the nav breakpoint', () => {
    it('hides the control container for a member and gives the Menu button its touch size and ARIA', async () => {
      const fixture = await render();
      const container = query(fixture, 'header > div');
      expect(container.classList.contains('max-nav:hidden')).toBe(true);
      const menu = menuButton(fixture);
      for (const name of ['nav:hidden', 'max-nav:min-h-11', 'max-nav:min-w-11', 'max-nav:ms-auto']) expect(menu.classList.contains(name), name).toBe(true);
      expect(menu.getAttribute('aria-haspopup')).toBe('dialog');
      expect(menu.getAttribute('aria-controls')).toBe('mobile-nav');
      expect(menu.getAttribute('aria-expanded')).toBe('false');
      expect(query(fixture, '#language-select')).toBeTruthy();
      expect(query(fixture, '#theme-select')).toBeTruthy();
      await open(fixture);
      expect(menu.getAttribute('aria-expanded')).toBe('true');
    });

    it('keeps the controls visible and touch-sized, with no Menu button and no dialog, for not-member', async () => {
      const fixture = await render(notMember);
      expect(query(fixture, 'header > div').classList.contains('max-nav:hidden')).toBe(false);
      expect(fixture.nativeElement.querySelector('dialog')).toBeNull();
      expect(fixture.nativeElement.querySelector('[aria-controls=mobile-nav]')).toBeNull();
      for (const control of queryAll(fixture, 'header select, header button', { min: 3 })) {
        expect(control.classList.contains('max-nav:min-h-11'), control.outerHTML).toBe(true);
      }
    });

    it.each<Session>([{ state: 'anonymous' }, { state: 'unavailable', traceId: 'synthetic-ref' }])('renders no dialog for the $state state', async (session) => {
      const fixture = await render(session);
      expect(fixture.nativeElement.querySelector('dialog')).toBeNull();
      expect(query(fixture, 'header > div').classList.contains('max-nav:hidden')).toBe(false);
    });
  });

  describe('markup', () => {
    it('is a labelled native dialog placed after the layout wrapper', async () => {
      const fixture = await render();
      const drawer = dialog(fixture);
      expect(drawer.getAttribute('aria-label')).toBe('Main menu');
      expect(drawer.hasAttribute('open')).toBe(false);
      expect(drawer.previousElementSibling?.hasAttribute('data-collapsed')).toBe(true);
      expect(fixture.nativeElement.querySelector('nav')?.id).toBe('primary-nav');
    });

    it('carries nav:open:hidden and open:flex, never a bare or nav: display utility, and no data-nav-label', async () => {
      const fixture = await render();
      const drawer = dialog(fixture);
      const classes = Array.from(drawer.classList);
      expect(classes).toContain('nav:open:hidden');
      expect(classes).toContain('open:flex');
      expect(classes).not.toContain('nav:hidden');
      expect(classes).not.toContain('flex');
      expect(drawer.querySelector('[data-nav-label]')).toBeNull();
      expect(drawer.querySelector('#primary-nav')).toBeNull();
    });

    it('holds the three nav links, the language and theme selects and Sign out, sized for touch', async () => {
      const fixture = await render();
      const drawer = dialog(fixture);
      const links = queryAll<HTMLAnchorElement>(drawer, 'nav a', { min: 3 });
      expect(links.map((link) => [link.textContent?.trim(), link.getAttribute('href')])).toEqual([
        ['Home', '/'], ['Household', '/household'], ['Payment options', '/payment-options'],
      ]);
      for (const link of links) expect(link.classList.contains('min-h-11')).toBe(true);
      expect(query(drawer, 'nav').getAttribute('aria-label')).toBe('Primary');
      expect(query(drawer, 'h2').textContent).toBe('Settings');
      for (const id of ['drawer-language-select', 'drawer-theme-select']) {
        const select = query<HTMLSelectElement>(drawer, `#${id}`);
        expect(select.classList.contains('min-h-11')).toBe(true);
        expect(select.classList.contains('w-full')).toBe(true);
        expect(query(drawer, `label[for=${id}]`)).toBeTruthy();
      }
      expect(closeButton(fixture).classList.contains('max-nav:min-h-11')).toBe(true);
      expect(signOutButton(fixture).classList.contains('min-h-11')).toBe(true);
    });

    it('localizes the new strings in Danish', async () => {
      TestBed.inject(LanguageService).setLanguage('da');
      const fixture = await render();
      expect(dialog(fixture).getAttribute('aria-label')).toBe('Hovedmenu');
      expect(drawerButtons(fixture).map((button) => button.textContent?.trim())).toContain('Luk menu');
      expect(query(dialog(fixture), 'h2').textContent).toBe('Indstillinger');
    });
  });

  describe('shared handlers', () => {
    it('changes language from the drawer and leaves it open', async () => {
      const fixture = await render();
      await open(fixture);
      const select = query<HTMLSelectElement>(dialog(fixture), '#drawer-language-select');
      select.value = 'da';
      select.dispatchEvent(new Event('change'));
      await settle(fixture);
      expect(TestBed.inject(LanguageService).language()).toBe('da');
      expect(query<HTMLSelectElement>(fixture, '#language-select').value).toBe('da');
      expect(dialog(fixture).open).toBe(true);
      expect(fixture.componentInstance.menuOpen()).toBe(true);
    });

    it('changes theme from the drawer and leaves it open', async () => {
      const fixture = await render();
      await open(fixture);
      const select = query<HTMLSelectElement>(dialog(fixture), '#drawer-theme-select');
      select.value = 'dark';
      select.dispatchEvent(new Event('change'));
      await settle(fixture);
      expect(TestBed.inject(ThemeService).mode()).toBe('dark');
      expect(query<HTMLSelectElement>(fixture, '#theme-select').value).toBe('dark');
      expect(dialog(fixture).open).toBe(true);
      document.documentElement.removeAttribute('data-theme');
    });
  });

  describe('Sign out', () => {
    it('closes the drawer before it calls signOut()', async () => {
      const fixture = await render();
      await open(fixture);
      const calls: boolean[] = [];
      vi.spyOn(TestBed.inject(SessionService), 'signOut').mockImplementation(() => { calls.push(fixture.componentInstance.menuOpen()); });
      signOutButton(fixture).click();
      expect(calls).toEqual([false]);
      await settle(fixture);
      expect(dialog(fixture).open).toBe(false);
    });

    it('shows the failure notice in main with the drawer already closed when the request fails', async () => {
      const fixture = await render();
      await open(fixture);
      signOutButton(fixture).click();
      http.expectOne('/bff/logout').flush({}, { status: 500, statusText: 'Server Error' });
      await settle(fixture);
      expect(dialog(fixture).open).toBe(false);
      expect(TestBed.inject(SessionService).current().state).toBe('member');
      const notice = query(fixture, 'main app-notice [role=alert]');
      expect(notice.textContent).toContain('Sign out failed');
      expect(notice.closest('dialog')).toBeNull();
      expect(bodyLocked()).toBe(false);
    });
  });

  describe('open and close', () => {
    it('opens from the Menu button and syncs menuOpen, the dialog and the scroll lock', async () => {
      const fixture = await render();
      menuButton(fixture).click();
      await settle(fixture);
      expect(fixture.componentInstance.menuOpen()).toBe(true);
      expect(dialog(fixture).open).toBe(true);
      expect(bodyLocked()).toBe(true);
      menuButton(fixture).click();
      await settle(fixture);
      expect(dialog(fixture).open).toBe(false);
      expect(bodyLocked()).toBe(false);
    });

    it('closes on the close event, the close button and a navigation', async () => {
      const fixture = await render();
      await open(fixture);
      dialog(fixture).close();
      await settle(fixture);
      expect(fixture.componentInstance.menuOpen()).toBe(false);
      expect(bodyLocked()).toBe(false);

      await open(fixture);
      closeButton(fixture).click();
      await settle(fixture);
      expect(dialog(fixture).open).toBe(false);
      expect(fixture.componentInstance.menuOpen()).toBe(false);

      await open(fixture);
      await TestBed.inject(Router).navigateByUrl('/payment-options');
      await settle(fixture);
      http.expectOne('/api/payment-options').flush({ options: [] });
      http.match((request) => request.url.startsWith('/api/payment-options/')).forEach((r) => r.flush({ suggestions: [] }));
      await settle(fixture);
      expect(dialog(fixture).open).toBe(false);
      expect(fixture.componentInstance.menuOpen()).toBe(false);
    });

    it('closes when the current page link is picked again', async () => {
      const fixture = await render();
      await open(fixture);
      query<HTMLAnchorElement>(dialog(fixture), 'nav a[href="/"]').click();
      await settle(fixture);
      expect(dialog(fixture).open).toBe(false);
    });

    it('ends focus on the page h1 after a link is picked in the drawer', async () => {
      const fixture = await render();
      await open(fixture);
      query<HTMLAnchorElement>(dialog(fixture), 'nav a[href="/payment-options"]').click();
      await fixture.whenStable();
      http.expectOne('/api/payment-options').flush({ options: [] });
      http.match((request) => request.url.startsWith('/api/payment-options/')).forEach((r) => r.flush({ suggestions: [] }));
      await settle(fixture);
      await new Promise<void>((resolve) => setTimeout(resolve));
      expect(dialog(fixture).open).toBe(false);
      expect(document.activeElement).toBe(query(fixture, 'h1'));
    });

    it('does not close for a click inside the content', async () => {
      const fixture = await render();
      await open(fixture);
      const heading = query(dialog(fixture), 'h2');
      heading.dispatchEvent(new Event('pointerdown', { bubbles: true }));
      heading.click();
      await settle(fixture);
      expect(dialog(fixture).open).toBe(true);
      expect(fixture.componentInstance.menuOpen()).toBe(true);
    });

    it('closes when the press and the release are both on the dialog element (backdrop tap)', async () => {
      const fixture = await render();
      await open(fixture);
      dialog(fixture).dispatchEvent(new Event('pointerdown', { bubbles: true }));
      dialog(fixture).click();
      await settle(fixture);
      expect(dialog(fixture).open).toBe(false);
      expect(fixture.componentInstance.menuOpen()).toBe(false);
    });

    it('keeps it open when the press starts in the content and the release lands on the backdrop', async () => {
      const fixture = await render();
      await open(fixture);
      query(dialog(fixture), 'h2').dispatchEvent(new Event('pointerdown', { bubbles: true }));
      dialog(fixture).click();
      await settle(fixture);
      expect(dialog(fixture).open).toBe(true);
      expect(fixture.componentInstance.menuOpen()).toBe(true);
    });

    it('ignores a stale close event that arrives after the dialog was re-opened', async () => {
      const fixture = await render();
      const drawer = dialog(fixture);
      const pending: (() => void)[] = [];
      drawer.close = function (this: HTMLDialogElement) {
        if (!this.open) return;
        this.open = false;
        pending.push(() => this.dispatchEvent(new Event('close')));
      };
      await open(fixture);
      fixture.componentInstance.menuOpen.set(false);
      await settle(fixture);
      expect(drawer.open).toBe(false);
      expect(pending).toHaveLength(1);
      await open(fixture);
      expect(drawer.open).toBe(true);
      pending.shift()!();
      await settle(fixture);
      expect(fixture.componentInstance.menuOpen()).toBe(true);
      expect(drawer.open).toBe(true);
      expect(bodyLocked()).toBe(true);
    });

    it('calls showModal() once for a double open and never opens when toggled open and closed in one tick', async () => {
      const fixture = await render();
      const showModal = vi.spyOn(dialog(fixture), 'showModal');
      fixture.componentInstance.menuOpen.set(true);
      fixture.componentInstance.menuOpen.set(true);
      await settle(fixture);
      fixture.componentInstance.menuOpen.set(true);
      await settle(fixture);
      expect(showModal).toHaveBeenCalledTimes(1);

      fixture.componentInstance.menuOpen.set(false);
      await settle(fixture);
      showModal.mockClear();
      fixture.componentInstance.menuOpen.set(true);
      fixture.componentInstance.menuOpen.set(false);
      await settle(fixture);
      expect(showModal).not.toHaveBeenCalled();
      expect(dialog(fixture).open).toBe(false);
    });
  });

  describe('session changes', () => {
    it('leaves no open modal, no menuOpen and no scroll lock when the session ends with the drawer open', async () => {
      const fixture = await render();
      await open(fixture);
      expect(bodyLocked()).toBe(true);
      TestBed.inject(SessionService).current.set({ state: 'anonymous' });
      await settle(fixture);
      await settle(fixture);
      expect(fixture.nativeElement.querySelector('dialog')).toBeNull();
      expect(fixture.componentInstance.menuOpen()).toBe(false);
      expect(bodyLocked()).toBe(false);
    });

    it('opens the newly created dialog when a session starts with menuOpen already true', async () => {
      const fixture = await render({ state: 'anonymous' });
      fixture.componentInstance.menuOpen.set(true);
      TestBed.inject(SessionService).current.set(member);
      await settle(fixture);
      http.match(() => true);
      expect(dialog(fixture).open).toBe(true);
      expect(bodyLocked()).toBe(true);
    });
  });

  describe('scroll lock', () => {
    it('is released when the component is destroyed with the drawer open', async () => {
      const fixture = await render();
      await open(fixture);
      expect(bodyLocked()).toBe(true);
      fixture.destroy();
      expect(bodyLocked()).toBe(false);
    });

    it('is never set when showModal() throws', async () => {
      const fixture = await render();
      vi.spyOn(dialog(fixture), 'showModal').mockImplementation(() => { throw new DOMException('Detached', 'InvalidStateError'); });
      await open(fixture);
      expect(bodyLocked()).toBe(false);
      expect(fixture.componentInstance.menuOpen()).toBe(false);
    });
  });

  describe('viewport reaching the nav breakpoint', () => {
    it('closes an open drawer when the media query starts to match and clears the scroll lock', async () => {
      const media = fakeMatchMedia();
      const fixture = await render();
      expect(window.matchMedia).toHaveBeenCalledWith('(min-width: 900px)');
      await open(fixture);
      media.fire(false);
      await settle(fixture);
      expect(dialog(fixture).open).toBe(true);
      media.fire(true);
      await settle(fixture);
      expect(dialog(fixture).open).toBe(false);
      expect(fixture.componentInstance.menuOpen()).toBe(false);
      expect(bodyLocked()).toBe(false);
    });

    it('removes the listener on destroy', async () => {
      const media = fakeMatchMedia();
      const fixture = await render();
      const [added] = media.list.addEventListener.mock.calls;
      fixture.destroy();
      expect(media.list.removeEventListener).toHaveBeenCalledWith('change', added[1]);
      expect(media.listeners.size).toBe(0);
    });

    it('is a no-op when matchMedia is missing', async () => {
      expect(window.matchMedia).toBeUndefined();
      const fixture = await render();
      await open(fixture);
      expect(dialog(fixture).open).toBe(true);
      dialog(fixture).close();
      await settle(fixture);
    });
  });

  describe('focus', () => {
    it('falls back to the Menu button when nothing has focus after the drawer closes', async () => {
      const fixture = await render();
      await open(fixture);
      (document.activeElement as HTMLElement | null)?.blur();
      expect(document.activeElement).toBe(document.body);
      fixture.componentInstance.menuOpen.set(false);
      await settle(fixture);
      await Promise.resolve();
      expect(document.activeElement).toBe(menuButton(fixture));
    });

    it('leaves focus alone when something else already has it', async () => {
      const fixture = await render();
      await open(fixture);
      const other = document.createElement('button');
      document.body.appendChild(other);
      other.focus();
      fixture.componentInstance.menuOpen.set(false);
      await settle(fixture);
      await Promise.resolve();
      expect(document.activeElement).toBe(other);
      other.remove();
    });
  });
});
