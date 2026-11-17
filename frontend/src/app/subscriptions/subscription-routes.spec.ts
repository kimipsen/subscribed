import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { routes } from '../app.routes';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE } from '../i18n/i18n';
import { Session, SessionService, csrfInterceptor } from '../session';
import { PaymentOptionsPage } from './payment-options-page';
import { SubscriptionDetailPage } from './subscription-detail-page';
import { SubscriptionFormPage } from './subscription-form-page';
import { catalogFixture, detailFixture, optionsFixture, overviewFixture, paymentOptionListFixture } from './testing/fixtures';
import '../app.config';

describe('Subscription routes', () => {
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

  const member: Session = { state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' };
  const noSubscriptionRequests = () => http.expectNone((request) => request.url.startsWith('/api/subscriptions') || request.url.startsWith('/api/payment-options'));

  it('/subscriptions/new renders the form in add mode, requests the options and never "new" as an id', async () => {
    session.current.set(member);
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/subscriptions/new', SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush(optionsFixture());
    http.expectOne('/api/subscriptions').flush(overviewFixture());
    http.expectOne('/api/subscriptions/catalog').flush(catalogFixture());
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectNone('/api/subscriptions/new');
    expect(component.editing).toBe(false);
    expect(component.id).toBeNull();
  });

  it('/subscriptions/<id>/edit renders the form in edit mode, requesting the options and the detail', async () => {
    session.current.set(member);
    const detail = detailFixture();
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl(`/subscriptions/${detail.id}/edit`, SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush(optionsFixture());
    http.expectOne('/api/subscriptions').flush(overviewFixture());
    http.expectOne('/api/subscriptions/catalog').flush(catalogFixture());
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne(`/api/subscriptions/${detail.id}`).flush(detail);
    expect(component.editing).toBe(true);
    expect(component.id).toBe(detail.id);
  });

  it('/subscriptions/<id> renders the detail page and requests that subscription', async () => {
    session.current.set(member);
    const detail = detailFixture();
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl(`/subscriptions/${detail.id}`, SubscriptionDetailPage);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne(`/api/subscriptions/${detail.id}`).flush(detail);
    expect(component.id).toBe(detail.id);
  });

  it('keeps /subscriptions/new on the form even though /subscriptions/:id exists (route order)', async () => {
    session.current.set(member);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/subscriptions/new', SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush(optionsFixture());
    http.expectOne('/api/subscriptions').flush(overviewFixture());
    http.expectOne('/api/subscriptions/catalog').flush(catalogFixture());
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectNone('/api/subscriptions/new');
  });

  it('/payment-options renders the options page and requests the options and the suggestions', async () => {
    session.current.set(member);
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/payment-options', PaymentOptionsPage);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/payment-options/suggestions').flush({ suggestions: [] });
    expect(component).toBeInstanceOf(PaymentOptionsPage);
  });

  for (const url of ['/subscriptions/new', `/subscriptions/${'a'.repeat(32)}/edit`, `/subscriptions/${'a'.repeat(32)}`, '/payment-options']) {
    it(`${url}: anonymous users go to sign-in with the return path and nothing is requested`, async () => {
      session.current.set({ state: 'anonymous' });
      await RouterTestingHarness.create(url);
      expect(TestBed.inject(Router).url).toBe(`/welcome?returnUrl=${encodeURIComponent(url)}`);
      noSubscriptionRequests();
    });

    it(`${url}: non-members go to access denied and nothing is requested`, async () => {
      session.current.set({ state: 'not-member', user: { displayName: 'Synthetic', subject: 'ref' }, expiresAt: '' });
      await RouterTestingHarness.create(url);
      expect(TestBed.inject(Router).url).toBe('/access-denied');
      noSubscriptionRequests();
    });

    it(`${url}: an unavailable session shows the shared panel and nothing is requested`, async () => {
      session.current.set({ state: 'unavailable', traceId: 'ref' });
      const harness = await RouterTestingHarness.create(url);
      expect(harness.routeNativeElement?.textContent).toContain('could not be checked');
      expect(TestBed.inject(Router).url).toBe(url);
      noSubscriptionRequests();
    });
  }
});
