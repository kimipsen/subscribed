import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { csrfInterceptor } from '../session';
import { PaymentOptionsService } from './payment-options.service';
import { paymentOptionListFixture, suggestionListFixture } from './testing/fixtures';

describe('PaymentOptionsService', () => {
  let http: HttpTestingController;
  let service: PaymentOptionsService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([csrfInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(PaymentOptionsService);
  });
  afterEach(() => http.verify());

  it('lists every option, removed ones included, from GET /api/payment-options', () => {
    let received: unknown;
    service.list().subscribe((options) => (received = options));
    const req = http.expectOne('/api/payment-options');
    expect(req.request.method).toBe('GET');
    req.flush(paymentOptionListFixture());
    expect(received).toEqual(paymentOptionListFixture().options);
    expect((received as { removed: boolean }[]).some((o) => o.removed)).toBe(true);
  });

  it('loads the suggestions from GET /api/payment-options/suggestions', () => {
    let received: unknown;
    service.suggestions().subscribe((suggestions) => (received = suggestions));
    const req = http.expectOne('/api/payment-options/suggestions');
    expect(req.request.method).toBe('GET');
    req.flush(suggestionListFixture());
    expect(received).toEqual(suggestionListFixture().suggestions);
  });

  it('creates with POST, the CSRF header and no If-Match, sending both keys', () => {
    service.create({ name: 'Visa', label: null }).subscribe();
    const req = http.expectOne('/api/payment-options');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('X-CSRF')).toBe('1');
    expect(req.request.headers.has('If-Match')).toBe(false);
    expect(req.request.body).toEqual({ name: 'Visa', label: null });
    req.flush(paymentOptionListFixture().options[0], { status: 201, statusText: 'Created' });
  });

  it('updates with PUT and the version in a quoted If-Match', () => {
    service.update('abc', 4, { name: 'Visa Gold', label: 'Joint' }).subscribe();
    const req = http.expectOne('/api/payment-options/abc');
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-Match')).toBe('"4"');
    expect(req.request.headers.get('X-CSRF')).toBe('1');
    expect(req.request.body).toEqual({ name: 'Visa Gold', label: 'Joint' });
    req.flush(paymentOptionListFixture().options[0]);
  });

  it('removes with DELETE and the version in a quoted If-Match', () => {
    service.remove('abc', 7).subscribe();
    const req = http.expectOne('/api/payment-options/abc');
    expect(req.request.method).toBe('DELETE');
    expect(req.request.headers.get('If-Match')).toBe('"7"');
    expect(req.request.headers.get('X-CSRF')).toBe('1');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });
});
