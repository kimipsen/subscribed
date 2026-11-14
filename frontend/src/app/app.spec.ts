import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { App } from './app';
import { routes } from './app.routes';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService } from './i18n/i18n';
import { Session, SessionService, csrfInterceptor } from './session';
import { COLOR_SCHEME_QUERY, THEME_STORAGE, ThemeService } from './theme/theme';
import './app.config';

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
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem, setItem } },
        { provide: THEME_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
        { provide: COLOR_SCHEME_QUERY, useValue: null },
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
    http.expectOne('/api/subscriptions').flush({ asOf: '2026-11-13', subscriptions: [], totals: [] });
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
    expect(fixture.nativeElement.querySelector('.error').textContent).toContain('Sign out failed');
    select(fixture, 'da');
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('.error').textContent).toContain('Udlogningen mislykkedes');
  });

  it('reports persistence failure with a dismissible localized notice', async () => {
    setItem.mockImplementation(() => { throw new DOMException('Full', 'QuotaExceededError'); });
    const fixture = await render();
    select(fixture, 'da');
    await fixture.whenStable();
    const notice = fixture.nativeElement.querySelector('output.notice') as HTMLElement;
    expect(notice.textContent).toContain('Dit sprogvalg kunne ikke gemmes');
    notice.querySelector('button')?.click();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('output.notice')).toBeNull();
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
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: storage },
        { provide: THEME_STORAGE, useValue: storage },
        { provide: COLOR_SCHEME_QUERY, useValue: null },
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
      http.expectOne('/api/subscriptions').flush({ asOf: '2026-11-13', subscriptions: [], totals: [] });
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
    http.expectOne('/api/household').flush({ id: 'household-default', name: 'Before', version: 1, members: [] });
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
    const notice = fixture.nativeElement.querySelector('output.notice') as HTMLElement;
    expect(notice.textContent).toContain('Dit valg af tema kunne ikke gemmes');
    const dismiss = notice.querySelector('button') as HTMLButtonElement;
    expect(dismiss.textContent).toBe('Luk');
    dismiss.click();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('output.notice')).toBeNull();
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
