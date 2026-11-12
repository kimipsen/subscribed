import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { App } from './app';
import { routes } from './app.routes';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService } from './i18n/i18n';
import { Session, SessionService, csrfInterceptor } from './session';
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
    return fixture;
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
    const notice = fixture.nativeElement.querySelector('[role=status].notice') as HTMLElement;
    expect(notice.textContent).toContain('Dit sprogvalg kunne ikke gemmes');
    notice.querySelector('button')?.click();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[role=status].notice')).toBeNull();
    expect(TestBed.inject(LanguageService).language()).toBe('da');
  });
});
