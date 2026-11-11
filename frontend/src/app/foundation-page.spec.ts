import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FoundationPage } from './foundation-page';
import { SessionService, csrfInterceptor } from './session';
import { routes } from './app.routes';

describe('Foundation UI', () => {
  let http: HttpTestingController;
  let session: SessionService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
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
    http.expectOne('/api/household/name').flush({ errors: { name: ['Server validation'] } },
      { status: 400, statusText: 'Bad request' });
    harness.detectChanges();
    expect(page.fieldError()).toBe('Server validation');
    expect(harness.routeNativeElement?.querySelector('[aria-invalid=true]')).toBeTruthy();
    page.name = '';
    page.save();
    expect(page.fieldError()).toContain('1-80');
  });
});
