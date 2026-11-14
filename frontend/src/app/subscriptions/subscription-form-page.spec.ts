import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { routes } from '../app.routes';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService } from '../i18n/i18n';
import { en } from '../i18n/catalog.en';
import { da } from '../i18n/catalog.da';
import { SessionService, csrfInterceptor } from '../session';
import { SubscriptionFormPage } from './subscription-form-page';
import { SubscriptionDetail, SubscriptionOptions } from './subscription.models';
import { englishLeftovers } from './testing/english-leftovers';
import { detailFixture, optionsFixture, validationProblemFixture } from './testing/fixtures';
import './../app.config';

describe('SubscriptionFormPage', () => {
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
    session.current.set({ state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' });
  });
  afterEach(() => http.verify());

  const options = (defaultCurrency: string | null = 'DKK'): SubscriptionOptions => ({ ...optionsFixture(), defaultCurrency });

  async function openAdd(data: SubscriptionOptions = options()) {
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/subscriptions/new', SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush(data);
    await settle(harness);
    return { harness, component, root: harness.routeNativeElement as HTMLElement };
  }

  async function openEdit(detail: SubscriptionDetail = detailFixture()) {
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl(`/subscriptions/${detail.id}/edit`, SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush(options());
    http.expectOne(`/api/subscriptions/${detail.id}`).flush(detail);
    await settle(harness);
    return { harness, component, root: harness.routeNativeElement as HTMLElement };
  }

  async function settle(harness: RouterTestingHarness) {
    harness.detectChanges();
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  const input = (root: HTMLElement, id: string) => root.querySelector(`#${id}`) as HTMLInputElement & HTMLSelectElement & HTMLTextAreaElement;

  function type(root: HTMLElement, id: string, value: string, event = 'input') {
    const field = input(root, id);
    field.value = value;
    field.dispatchEvent(new Event(event));
  }

  async function submit(harness: RouterTestingHarness) {
    (harness.routeNativeElement?.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(harness);
  }

  async function fillValidAdd(root: HTMLElement, amount = '129.00') {
    type(root, 'name', 'StreamCo');
    type(root, 'startDate', '2024-03-01');
    type(root, 'amount', amount);
  }

  const lastRequest = (url: string): TestRequest => http.expectOne(url);

  it('adds with the expected body: a string amount, null for empty optionals, and the CSRF header', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    await submit(harness);
    const req = lastRequest('/api/subscriptions');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('X-CSRF')).toBe('1');
    expect(req.request.body).toEqual({
      name: 'StreamCo', category: null, notes: null, currency: 'DKK', startDate: '2024-03-01', endDate: null,
      amount: '129.00', cadenceUnit: 'month', cadenceCount: 1,
    });
    const created = detailFixture();
    req.flush(created, { status: 201, statusText: 'Created' });
    await settle(harness);
    expect(TestBed.inject(Router).url).toBe(`/subscriptions/${created.id}`);
  });

  it('sends the canonical amount, never the field text, when a Danish member types a decimal comma (D-7)', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { harness, root } = await openAdd();
    await fillValidAdd(root, '129,50');
    await submit(harness);
    const req = lastRequest('/api/subscriptions');
    expect((req.request.body as { amount: string }).amount).toBe('129.50');
    req.flush(detailFixture(), { status: 201, statusText: 'Created' });
  });

  it('preselects the default currency', async () => {
    const { root } = await openAdd(options('EUR'));
    expect(input(root, 'currency').value).toBe('EUR');
  });

  it('asks the member to choose when there is no default currency', async () => {
    const { harness, root } = await openAdd(options(null));
    expect(input(root, 'currency').value).toBe('');
    await fillValidAdd(root);
    await submit(harness);
    http.expectNone('/api/subscriptions');
    expect(input(root, 'currency').getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#currency-error')?.textContent).toContain(en['subscriptions.error.currency']);
  });

  it('adds a custom cadence', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    input(root, 'cadence-custom').click();
    await settle(harness);
    type(root, 'cadence-count', '3');
    type(root, 'cadence-unit', 'year', 'change');
    await submit(harness);
    const req = lastRequest('/api/subscriptions');
    expect(req.request.body).toMatchObject({ cadenceUnit: 'year', cadenceCount: 3 });
    req.flush(detailFixture(), { status: 201, statusText: 'Created' });
  });

  it('shows localized client errors next to their fields with aria-invalid and aria-describedby, and sends nothing', async () => {
    const { harness, root } = await openAdd();
    type(root, 'name', '   ');
    type(root, 'startDate', '');
    type(root, 'amount', '1.234,5');
    await submit(harness);
    http.expectNone('/api/subscriptions');
    for (const [field, key] of [['name', 'subscriptions.error.name'], ['startDate', 'subscriptions.error.startDate'], ['amount', 'subscriptions.error.amount']] as const) {
      expect(input(root, field).getAttribute('aria-invalid'), field).toBe('true');
      expect(input(root, field).getAttribute('aria-describedby'), field).toContain(`${field}-error`);
      expect(root.querySelector(`#${field}-error`)?.textContent, field).toContain(en[key].replace('{example}', '129.00'));
    }
  });

  it('validates the amount by the currency minor unit (JPY takes whole numbers)', async () => {
    const { harness, root } = await openAdd(options('JPY'));
    await fillValidAdd(root, '1200.5');
    await submit(harness);
    http.expectNone('/api/subscriptions');
    expect(input(root, 'amount').getAttribute('aria-invalid')).toBe('true');
    type(root, 'amount', '1200');
    await submit(harness);
    lastRequest('/api/subscriptions').flush(detailFixture(), { status: 201, statusText: 'Created' });
  });

  it('maps server field errors to localized messages and never renders backend English', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    await submit(harness);
    lastRequest('/api/subscriptions').flush(validationProblemFixture(), { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(root.querySelector('#name-error')?.textContent).toContain(en['subscriptions.error.name']);
    expect(root.querySelector('#amount-error')).toBeTruthy();
    expect(root.textContent).not.toContain('Name must be');
    expect(input(root, 'name').value).toBe('StreamCo');
  });

  it('falls back to a generic failure for unknown server keys and keeps the input', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    await submit(harness);
    lastRequest('/api/subscriptions').flush({ type: 'urn:subscribed:problem:validation-failed', traceId: 't', errors: { somethingNew: ['Backend English'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(root.textContent).toContain(en['subscriptions.form.saveFailed']);
    expect(root.textContent).not.toContain('Backend English');
    expect(input(root, 'name').value).toBe('StreamCo');
  });

  it('shows the localized message when access is revoked during the session (403)', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    await submit(harness);
    lastRequest('/api/subscriptions').flush({ traceId: 't' }, { status: 403, statusText: 'Forbidden' });
    await settle(harness);
    expect(root.textContent).toContain(en['subscriptions.form.forbidden']);
  });

  it('edits with If-Match and exactly six keys: non-null category and notes are echoed, endDate is an explicit null (D-10)', async () => {
    const detail = detailFixture();
    const { harness, root } = await openEdit(detail);
    expect(input(root, 'name').value).toBe(detail.name);
    expect(input(root, 'category').value).toBe('Streaming');
    expect(input(root, 'notes').value).toBe('Family plan');
    type(root, 'name', 'FilmPlus HD');
    await submit(harness);
    const req = lastRequest(`/api/subscriptions/${detail.id}`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-Match')).toBe(`"${detail.version}"`);
    expect(req.request.body).toEqual({
      name: 'FilmPlus HD', category: 'Streaming', notes: 'Family plan', currency: detail.currency, startDate: detail.startDate, endDate: null,
    });
    req.flush({ ...detail, name: 'FilmPlus HD', version: detail.version + 1 });
    await settle(harness);
    expect(TestBed.inject(Router).url).toBe(`/subscriptions/${detail.id}`);
  });

  it('keeps the input on a 412 and reloads the latest on request (BR-022)', async () => {
    const detail = detailFixture();
    const { harness, root } = await openEdit(detail);
    type(root, 'name', 'My edit');
    await submit(harness);
    lastRequest(`/api/subscriptions/${detail.id}`).flush({ traceId: 't', currentVersion: detail.version + 1 }, { status: 412, statusText: 'Precondition Failed' });
    await settle(harness);
    expect(root.textContent).toContain(en['subscriptions.form.conflict']);
    expect(input(root, 'name').value).toBe('My edit');
    (Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim() === en['subscriptions.form.reloadLatest']) as HTMLButtonElement).click();
    http.expectOne(`/api/subscriptions/${detail.id}`).flush({ ...detail, name: 'Server name', version: detail.version + 1 });
    await settle(harness);
    expect(input(root, 'name').value).toBe('Server name');
    expect(root.textContent).not.toContain(en['subscriptions.form.conflict']);
  });

  it('catches an end date before the start date and a start date that crosses a later price entry when editing', async () => {
    const detail = detailFixture();
    const { harness, root } = await openEdit(detail);
    const later = detail.priceHistory.find((e) => !e.initial)!;
    type(root, 'startDate', later.effectiveFrom);
    type(root, 'endDate', '2000-01-01');
    await submit(harness);
    http.expectNone(`/api/subscriptions/${detail.id}`);
    expect(root.querySelector('#startDate-error')?.textContent).toContain(en['subscriptions.error.startAfterEntries']);
    expect(root.querySelector('#endDate-error')?.textContent).toContain(en['subscriptions.error.endDate']);
  });

  it('re-renders a valid amount in the new language, never grouped, and leaves an invalid one flagged as typed (D-7)', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { harness, root } = await openAdd();
    type(root, 'amount', '129,00');
    TestBed.inject(LanguageService).setLanguage('en');
    await settle(harness);
    expect(input(root, 'amount').value).toBe('129.00');
    expect(input(root, 'amount').getAttribute('aria-invalid')).toBeNull();

    TestBed.inject(LanguageService).setLanguage('da');
    await settle(harness);
    type(root, 'amount', '1490,00');
    TestBed.inject(LanguageService).setLanguage('en');
    await settle(harness);
    expect(input(root, 'amount').value).toBe('1490.00');

    type(root, 'amount', '1.490');
    await submit(harness);
    expect(input(root, 'amount').getAttribute('aria-invalid')).toBe('true');
    TestBed.inject(LanguageService).setLanguage('da');
    await settle(harness);
    expect(input(root, 'amount').value).toBe('1.490');
    expect(input(root, 'amount').getAttribute('aria-invalid')).toBe('true');
  });

  it('renders in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { root } = await openAdd();
    expect(root.querySelector('h1')?.textContent).toContain(da['subscriptions.form.addHeading']);
    expect(englishLeftovers(root)).toEqual([]);
  });
});
