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
import { query, queryAll } from '../testing/query';
import { SubscriptionDetailPage } from './subscription-detail-page';
import { SubscriptionFormPage } from './subscription-form-page';
import { Catalog, CatalogPlan, CatalogService, PaymentOptionList, SubscriptionDetail, SubscriptionOptions, SubscriptionOverview } from './subscription.models';
import { englishLeftovers } from './testing/english-leftovers';
import { catalogFixture, detailFixture, optionsFixture, overviewFixture, overviewWith, paymentOptionListFixture, validationProblemFixture } from './testing/fixtures';
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

  /** The overview the form reads for category suggestions, or 'fail' for a server error. */
  type OverviewReply = SubscriptionOverview | 'fail';
  const flushOverview = (reply: OverviewReply) => {
    const req = http.expectOne('/api/subscriptions');
    if (reply === 'fail') req.flush({ traceId: 't' }, { status: 503, statusText: 'Unavailable' });
    else req.flush(reply);
  };

  /** The catalog request is answered with the fixture, a custom catalog, or a 500 ('fail'). */
  function flushCatalog(catalog: Catalog | 'fail') {
    const req = http.expectOne('/api/subscriptions/catalog');
    if (catalog === 'fail') req.flush({}, { status: 500, statusText: 'Server Error' });
    else req.flush(catalog);
  }

  function flushPaymentOptions(reply: PaymentOptionList | 'fail') {
    const req = http.expectOne('/api/payment-options');
    if (reply === 'fail') req.flush({ traceId: 't' }, { status: 500, statusText: 'Server Error' });
    else req.flush(reply);
  }

  async function openAdd(
    data: SubscriptionOptions = options(),
    overview: OverviewReply = overviewFixture(),
    catalog: Catalog | 'fail' = catalogFixture(),
    paymentOptions: PaymentOptionList | 'fail' = paymentOptionListFixture(),
  ) {
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/subscriptions/new', SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush(data);
    flushOverview(overview);
    flushCatalog(catalog);
    const paymentRequest = http.expectOne('/api/payment-options');
    if (paymentOptions === 'fail') paymentRequest.flush({ traceId: 't' }, { status: 500, statusText: 'Server Error' });
    else paymentRequest.flush(paymentOptions);
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    await settle(harness);
    return { harness, component, root: harness.routeNativeElement as HTMLElement };
  }

  async function openEdit(
    detail: SubscriptionDetail = detailFixture(),
    overview: OverviewReply = overviewFixture(),
    catalog: Catalog | 'fail' = catalogFixture(),
    paymentOptions: PaymentOptionList | 'fail' = paymentOptionListFixture(),
  ) {
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl(`/subscriptions/${detail.id}/edit`, SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush(options());
    flushOverview(overview);
    flushCatalog(catalog);
    flushPaymentOptions(paymentOptions);
    http.expectOne(`/api/subscriptions/${detail.id}`).flush(detail);
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
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

  /** A saved subscription lands on its detail page, which loads the detail. */
  async function finishSave(harness: RouterTestingHarness, req: TestRequest, saved: SubscriptionDetail, status = 200) {
    req.flush(saved, status === 201 ? { status, statusText: 'Created' } : {});
    await settle(harness);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    http.expectOne(`/api/subscriptions/${saved.id}`).flush(saved);
    await settle(harness);
  }

  it('adds with the expected body: a string amount, null for empty optionals, and the CSRF header', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    await submit(harness);
    const req = lastRequest('/api/subscriptions');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('X-CSRF')).toBe('1');
    expect(req.request.body).toEqual({
      name: 'StreamCo', category: null, notes: null, currency: 'DKK', startDate: '2024-03-01', endDate: null,
      amount: '129.00', cadenceUnit: 'month', cadenceCount: 1, catalogId: null, paymentOptionId: null, unsubscribeUrl: null, cancelBy: null,
    });
    const created = detailFixture();
    await finishSave(harness, req, created, 201);
    expect(TestBed.inject(Router).url).toBe(`/subscriptions/${created.id}`);
    expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(SubscriptionDetailPage);
  });

  // ---- payment options (add mode only) ---------------------------------------------------------------

  const choices = (root: HTMLElement) =>
    Array.from(root.querySelectorAll<HTMLOptionElement>('#paymentOptionId option')).map((o) => ({ value: o.value, text: (o.textContent ?? '').trim() }));

  it('offers None and the non-removed options on the add form, and sends the picked one with the initial price', async () => {
    const { harness, root } = await openAdd();
    expect(choices(root)).toEqual([
      { value: '', text: en['paymentOptions.select.none'] },
      { value: '0192f3c1a1b27c4e8d2e5f60718293b5', text: 'Visa · Joint account' },
    ]);
    await fillValidAdd(root);
    const select = input(root, 'paymentOptionId');
    select.value = '0192f3c1a1b27c4e8d2e5f60718293b5';
    select.dispatchEvent(new Event('change'));
    await submit(harness);
    const req = lastRequest('/api/subscriptions');
    expect((req.request.body as { paymentOptionId: string | null }).paymentOptionId).toBe('0192f3c1a1b27c4e8d2e5f60718293b5');
    await finishSave(harness, req, detailFixture(), 201);
  });

  it('sends an explicit null when no option is picked', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    await submit(harness);
    const req = lastRequest('/api/subscriptions');
    expect(Object.hasOwn(req.request.body as object, 'paymentOptionId')).toBe(true);
    expect((req.request.body as { paymentOptionId: string | null }).paymentOptionId).toBeNull();
    await finishSave(harness, req, detailFixture(), 201);
  });

  it('still adds, with just None, when the options fail to load', async () => {
    const { harness, root } = await openAdd(options(), overviewFixture(), catalogFixture(), 'fail');
    expect(choices(root)).toEqual([{ value: '', text: en['paymentOptions.select.none'] }]);
    await fillValidAdd(root);
    await submit(harness);
    await finishSave(harness, lastRequest('/api/subscriptions'), detailFixture(), 201);
  });

  it('keeps the payment option select text only: no logo inside or beside it', async () => {
    const { root } = await openAdd();
    const field = root.querySelector('#paymentOptionId') as HTMLElement;
    expect(field.querySelector('img')).toBeNull();
    expect(field.closest('[data-field]')?.querySelector('img, app-payment-option-logo')).toBeNull();
  });

  // ---- payment option (edit mode, #39) ----------------------------------------------------------------

  const VISA = '0192f3c1a1b27c4e8d2e5f60718293b5';
  const REMOVED = '0192f3c1a1b27c4e8d2e5f60718293c6';
  const EDIT_KEYS = ['name', 'category', 'notes', 'currency', 'startDate', 'endDate', 'catalogId', 'unsubscribeUrl', 'cancelBy'];

  function detailWith(optionId: string | null, over: Partial<SubscriptionDetail> = {}): SubscriptionDetail {
    const detail = detailFixture();
    detail.currentPrice.paymentOptionId = optionId;
    return { ...detail, ...over };
  }

  function pick(root: HTMLElement, value: string) {
    const select = input(root, 'paymentOptionId');
    select.value = value;
    select.dispatchEvent(new Event('change'));
  }

  it('requests the payment options in edit mode and shows the select prefilled with None', async () => {
    const detail = detailWith(null);
    const { root, component } = await openEdit(detail);
    expect(component.paymentOptions()).toHaveLength(2);
    expect(input(root, 'paymentOptionId').value).toBe('');
    expect(choices(root)).toEqual([
      { value: '', text: en['paymentOptions.select.none'] },
      { value: VISA, text: 'Visa · Joint account' },
    ]);
    expect(root.querySelector('#paymentOptionId-hint')?.textContent).toContain(en['subscriptions.field.paymentOptionEditHint']);
  });

  it('prefills the select with the current price option', async () => {
    const { root } = await openEdit(detailWith(VISA));
    expect(input(root, 'paymentOptionId').value).toBe(VISA);
  });

  it('sends no paymentOption when the select was not touched', async () => {
    const detail = detailWith(VISA);
    const { harness } = await openEdit(detail);
    await submit(harness);
    const req = lastRequest(`/api/subscriptions/${detail.id}`);
    expect(Object.keys(req.request.body as object)).toEqual(EDIT_KEYS);
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('sends paymentOption { id } when another option is picked, and { id: null } for None', async () => {
    const detail = detailWith(null);
    const first = await openEdit(detail);
    pick(first.root, VISA);
    await submit(first.harness);
    const req = lastRequest(`/api/subscriptions/${detail.id}`);
    expect((req.request.body as { paymentOption: unknown }).paymentOption).toEqual({ id: VISA });
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(first.harness);
  });

  it('sends { id: null } when None is picked over an existing option', async () => {
    const detail = detailWith(VISA);
    const { harness, root } = await openEdit(detail);
    pick(root, '');
    await submit(harness);
    const req = lastRequest(`/api/subscriptions/${detail.id}`);
    expect((req.request.body as { paymentOption: unknown }).paymentOption).toEqual({ id: null });
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('shows a removed current option as a disabled "(removed)" choice and does not send it unless another is picked', async () => {
    const detail = detailWith(REMOVED);
    const { harness, root } = await openEdit(detail);
    expect(choices(root).map((c) => c.value)).toEqual(['', VISA, REMOVED]);
    const removed = root.querySelector<HTMLOptionElement>(`#paymentOptionId option[value="${REMOVED}"]`)!;
    expect(removed.disabled).toBe(true);
    expect(removed.textContent?.trim()).toBe(en['paymentOptions.select.removed'].replace('{name}', 'Old card'));
    expect(input(root, 'paymentOptionId').value).toBe(REMOVED);
    await submit(harness);
    const req = lastRequest(`/api/subscriptions/${detail.id}`);
    expect(Object.hasOwn(req.request.body as object, 'paymentOption')).toBe(false);
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('does not offer a removed option that is not the current one', async () => {
    const { root } = await openEdit(detailWith(VISA));
    expect(choices(root).map((c) => c.value)).toEqual(['', VISA]);
  });

  it('hides the select for an ended subscription and sends no paymentOption', async () => {
    const detail = detailWith(VISA, { status: 'ended' });
    const { harness, root } = await openEdit(detail);
    expect(root.querySelector('#paymentOptionId')).toBeNull();
    await submit(harness);
    const req = lastRequest(`/api/subscriptions/${detail.id}`);
    expect(Object.hasOwn(req.request.body as object, 'paymentOption')).toBe(false);
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('hides the select when the options fail to load, and a save never clears the current option', async () => {
    const detail = detailWith(VISA);
    const { harness, root, component } = await openEdit(detail, overviewFixture(), catalogFixture(), 'fail');
    expect(component.loadFailed()).toBe(false);
    expect(root.querySelector('#paymentOptionId')).toBeNull();
    type(root, 'name', 'Renamed');
    await submit(harness);
    const req = lastRequest(`/api/subscriptions/${detail.id}`);
    expect(Object.keys(req.request.body as object)).toEqual(EDIT_KEYS);
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('sends nothing when an option is changed and then changed back', async () => {
    const detail = detailWith(null);
    const { harness, root } = await openEdit(detail);
    pick(root, VISA);
    pick(root, '');
    await submit(harness);
    const req = lastRequest(`/api/subscriptions/${detail.id}`);
    expect(Object.hasOwn(req.request.body as object, 'paymentOption')).toBe(false);
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  async function conflictAfterPickingVisa(detail: SubscriptionDetail) {
    const opened = await openEdit(detail);
    pick(opened.root, VISA);
    await submit(opened.harness);
    lastRequest(`/api/subscriptions/${detail.id}`).flush({ traceId: 't', currentVersion: detail.version + 1 }, { status: 412, statusText: 'Precondition Failed' });
    await settle(opened.harness);
    expect(opened.root.textContent).toContain(en['subscriptions.form.conflict']);
    return opened;
  }

  it('refreshes the baseline after a 412 reload, so a second save sends no stale paymentOption', async () => {
    const detail = detailWith(null);
    const { harness, root } = await conflictAfterPickingVisa(detail);
    (Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim() === en['subscriptions.form.reloadLatest']) as HTMLButtonElement).click();
    http.expectOne(`/api/subscriptions/${detail.id}`).flush(detailWith(VISA, { version: detail.version + 1 }));
    http.expectNone('/api/payment-options');
    await settle(harness);
    expect(input(root, 'paymentOptionId').value).toBe(VISA);
    await submit(harness);
    const req = lastRequest(`/api/subscriptions/${detail.id}`);
    expect(Object.hasOwn(req.request.body as object, 'paymentOption')).toBe(false);
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('keeps the form filled and shows the conflict banner on a 412 with a changed option', async () => {
    const { root } = await conflictAfterPickingVisa(detailWith(null));
    expect(input(root, 'paymentOptionId').value).toBe(VISA);
  });

  it('maps a paymentOption error (ended subscription) to a localized message beside the select', async () => {
    const detail = detailWith(null);
    const { harness, root } = await openEdit(detail);
    pick(root, VISA);
    await submit(harness);
    lastRequest(`/api/subscriptions/${detail.id}`).flush({ traceId: 't', errors: { paymentOption: ['English from the server'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(root.querySelector('#paymentOption-error')?.textContent).toContain(en['subscriptions.error.paymentOptionEnded']);
    expect(root.textContent).not.toContain('English from the server');
    expect(input(root, 'paymentOptionId').getAttribute('aria-invalid')).toBe('true');
    expect(input(root, 'paymentOptionId').value).toBe(VISA);
    expect(document.activeElement).toBe(input(root, 'paymentOptionId'));
  });

  it('maps a paymentOptionId error (unusable option) beside the select in edit mode', async () => {
    const detail = detailWith(null);
    const { harness, root } = await openEdit(detail);
    pick(root, VISA);
    await submit(harness);
    lastRequest(`/api/subscriptions/${detail.id}`).flush({ traceId: 't', errors: { paymentOptionId: ['English from the server'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(root.querySelector('#paymentOptionId-error')?.textContent).toContain(en['subscriptions.error.paymentOptionId']);
    expect(input(root, 'paymentOptionId').getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(input(root, 'paymentOptionId'));
  });

  it('maps a paymentOptionId error from the server to a localized message beside the select', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    await submit(harness);
    lastRequest('/api/subscriptions').flush({ traceId: 't', errors: { paymentOptionId: ['English from the server'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(root.querySelector('#paymentOptionId-error')?.textContent).toContain(en['subscriptions.error.paymentOptionId']);
    expect(root.textContent).not.toContain('English from the server');
    expect(input(root, 'paymentOptionId').getAttribute('aria-invalid')).toBe('true');
  });

  it('renders the select in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { root } = await openAdd();
    expect(root.querySelector('label[for="paymentOptionId"]')?.textContent).toContain(da['paymentOptions.select.label']);
    expect(choices(root)[0].text).toBe(da['paymentOptions.select.none']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  it('sends the canonical amount, never the field text, when a Danish member types a decimal comma (D-7)', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { harness, root } = await openAdd();
    await fillValidAdd(root, '129,50');
    await submit(harness);
    const req = lastRequest('/api/subscriptions');
    expect((req.request.body as { amount: string }).amount).toBe('129.50');
    await finishSave(harness, req, detailFixture(), 201);
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
    await finishSave(harness, req, detailFixture(), 201);
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
    await finishSave(harness, lastRequest('/api/subscriptions'), detailFixture(), 201);
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

  it('edits with If-Match and exactly nine keys: non-null category and notes are echoed, endDate is an explicit null (D-10)', async () => {
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
      name: 'FilmPlus HD', category: 'Streaming', notes: 'Family plan', currency: detail.currency, startDate: detail.startDate, endDate: null, catalogId: null,
      unsubscribeUrl: 'https://example.com/cancel', cancelBy: null,
    });
    await finishSave(harness, req, { ...detail, name: 'FilmPlus HD', version: detail.version + 1 });
    expect(TestBed.inject(Router).url).toBe(`/subscriptions/${detail.id}`);
    expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(SubscriptionDetailPage);
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
    // The overview from the first load is reused, like the options: no second GET /api/subscriptions.
    http.expectNone('/api/subscriptions');
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
    expect(query(root, 'label[for="name"]').textContent).toContain(da['subscriptions.field.name']);
    expect(query(root, '#notes-hint').textContent).toContain(da['subscriptions.field.notesHint']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  describe('category suggestions', () => {
    const datalistValues = (root: HTMLElement) =>
      Array.from(root.querySelectorAll('datalist#category-suggestions option')).map((option) => (option as HTMLOptionElement).value);

    it('offers the distinct existing categories, never Uncategorized (AC6, AC3)', async () => {
      const overview = overviewWith([
        { category: 'Streaming' },
        { category: ' streaming ' },
        { category: 'Food  Court' },
        { category: 'food court' },
        { category: null },
        { category: '' },
        { category: '   ' },
      ]);
      const { root } = await openAdd(options(), overview);
      expect(input(root, 'category').getAttribute('list')).toBe('category-suggestions');
      expect(datalistValues(root)).toEqual(['Streaming', 'Food Court']);
    });

    it('still submits free text that is not in the list', async () => {
      const { harness, root } = await openAdd();
      await fillValidAdd(root);
      type(root, 'category', 'Brand new category');
      await submit(harness);
      const req = lastRequest('/api/subscriptions');
      expect((req.request.body as { category: string }).category).toBe('Brand new category');
      await finishSave(harness, req, detailFixture(), 201);
    });

    it('also suggests when editing, including the subscription\'s own category', async () => {
      const { root } = await openEdit();
      expect(datalistValues(root)).toContain('Streaming');
    });

    it('works without suggestions when the overview request fails', async () => {
      const { harness, root } = await openAdd(options(), 'fail');
      expect(root.querySelector('form')).toBeTruthy();
      expect(root.querySelector('[role=alert]')).toBeNull();
      expect(datalistValues(root)).toEqual([]);
      await fillValidAdd(root);
      type(root, 'category', 'Anything');
      await submit(harness);
      const req = lastRequest('/api/subscriptions');
      expect(req.request.body).toMatchObject({ category: 'Anything' });
      await finishSave(harness, req, detailFixture(), 201);
    });

    it('refetches the overview on retry after a failed load, then reuses it', async () => {
      const detail = detailFixture();
      const harness = await RouterTestingHarness.create();
      const component = await harness.navigateByUrl(`/subscriptions/${detail.id}/edit`, SubscriptionFormPage);
      http.expectOne('/api/subscriptions/options').flush(options());
      flushOverview('fail');
      flushCatalog(catalogFixture());
      flushPaymentOptions(paymentOptionListFixture());
      http.expectOne(`/api/subscriptions/${detail.id}`).flush({ traceId: 't' }, { status: 500, statusText: 'Server Error' });
      await settle(harness);
      expect(component.loadFailed()).toBe(true);
      expect(component.overview()).toBeNull();

      component.load();
      // The options came with an unsuccessful load, so they are fetched again too.
      http.expectOne('/api/subscriptions/options').flush(options());
      flushOverview(overviewFixture());
      flushCatalog(catalogFixture());
      flushPaymentOptions(paymentOptionListFixture());
      http.expectOne(`/api/subscriptions/${detail.id}`).flush(detail);
      // The failed first load left its (cancelled) zone request behind: only the retry's request can be answered.
      http.match('/api/household').filter((r) => !r.cancelled).forEach((r) => r.flush({ timeZoneId: 'Europe/Copenhagen' }));
      await settle(harness);
      expect(datalistValues(harness.routeNativeElement as HTMLElement)).toContain('Streaming');

      component.load();
      http.expectOne(`/api/subscriptions/${detail.id}`).flush(detail);
      http.expectNone('/api/subscriptions');
      await settle(harness);
    });
  });

  // ---- cancel by -------------------------------------------------------------------------------------

  const cancelBody = (req: TestRequest) => req.request.body as { cancelBy: unknown };

  it('renders an optional datetime-local cancel-by field whose hint names the household zone', async () => {
    const { root } = await openAdd();
    const field = input(root, 'cancelBy');
    expect(field.type).toBe('datetime-local');
    expect(field.required).toBe(false);
    expect(field.value).toBe('');
    expect(root.querySelector('label[for="cancelBy"]')?.textContent).toContain(en['subscriptions.field.cancelBy']);
    expect(root.querySelector('#cancelBy-hint')?.textContent).toContain('Europe/Copenhagen');
    expect(field.getAttribute('aria-describedby')).toBe('cancelBy-hint');
  });

  it('shows a hint without a zone when the household cannot be loaded, and still saves', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/subscriptions/new', SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush(options());
    flushOverview(overviewFixture());
    flushCatalog(catalogFixture());
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/household').flush({ traceId: 't' }, { status: 500, statusText: 'Server Error' });
    await settle(harness);
    const root = harness.routeNativeElement as HTMLElement;
    expect(root.querySelector('#cancelBy-hint')?.textContent).toContain(en['subscriptions.field.cancelByHintNoZone']);
    await fillValidAdd(root);
    await submit(harness);
    expect(cancelBody(http.expectOne('/api/subscriptions')).cancelBy).toBeNull();
  });

  it('sends the typed wall time as cancelBy on add', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    type(root, 'cancelBy', '2024-04-01T09:30');
    await submit(harness);
    expect(cancelBody(http.expectOne('/api/subscriptions')).cancelBy).toBe('2024-04-01T09:30');
  });

  it('sends an explicit null when the field is empty', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    await submit(harness);
    expect(cancelBody(http.expectOne('/api/subscriptions')).cancelBy).toBeNull();
  });

  it('accepts a deadline on the start date and rejects one before it, without sending anything', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    type(root, 'cancelBy', '2024-02-29T23:59');
    await submit(harness);
    http.expectNone('/api/subscriptions');
    expect(root.querySelector('#cancelBy-error')?.textContent).toContain(en['subscriptions.error.cancelBy']);
    expect(input(root, 'cancelBy').getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement?.id).toBe('cancelBy');

    type(root, 'cancelBy', '2024-03-01T00:00');
    await submit(harness);
    expect(cancelBody(http.expectOne('/api/subscriptions')).cancelBy).toBe('2024-03-01T00:00');
  });

  it('rejects a cancel-by value that is not a complete date and time', async () => {
    const { harness, root, component } = await openAdd();
    await fillValidAdd(root);
    // A datetime-local control never yields a partial value, so set the model the way a pasted or scripted value would.
    component.cancelBy = '2024-04-01';
    await submit(harness);
    http.expectNone('/api/subscriptions');
    expect(root.querySelector('#cancelBy-error')).not.toBeNull();
  });

  it('maps a server cancelBy error to a localized message beside the field', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    type(root, 'cancelBy', '2024-04-01T09:30');
    await submit(harness);
    http.expectOne('/api/subscriptions').flush({ traceId: 't', errors: { cancelBy: ['English from the server'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(root.querySelector('#cancelBy-error')?.textContent).toContain(en['subscriptions.error.cancelBy']);
    expect(root.textContent).not.toContain('English from the server');
    expect(input(root, 'cancelBy').getAttribute('aria-invalid')).toBe('true');
  });

  it('pre-fills the edit field from detail.cancelBy and sends it back unchanged', async () => {
    const detail = { ...detailFixture(), cancelBy: '2026-12-01T09:00', cancelByInstant: '2026-12-01T08:00:00Z' };
    const { harness, root } = await openEdit(detail);
    expect(input(root, 'cancelBy').value).toBe('2026-12-01T09:00');
    type(root, 'name', 'FilmPlus HD');
    await submit(harness);
    expect(cancelBody(lastRequest(`/api/subscriptions/${detail.id}`)).cancelBy).toBe('2026-12-01T09:00');
  });

  it('clears the deadline: an emptied field sends an explicit null on edit', async () => {
    const detail = { ...detailFixture(), cancelBy: '2026-12-01T09:00', cancelByInstant: '2026-12-01T08:00:00Z' };
    const { harness, root } = await openEdit(detail);
    type(root, 'cancelBy', '');
    await submit(harness);
    expect(cancelBody(lastRequest(`/api/subscriptions/${detail.id}`)).cancelBy).toBeNull();
  });

  it('shows the cancel-by field in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { root } = await openAdd();
    expect(root.querySelector('label[for="cancelBy"]')?.textContent).toContain(da['subscriptions.field.cancelBy']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  // ---- unsubscribe link ------------------------------------------------------------------------------

  const putBody = (req: TestRequest) => req.request.body as { unsubscribeUrl: unknown };

  it('pre-fills the edit field from the detail and sends the trimmed value', async () => {
    const detail = detailFixture();
    const { harness, root } = await openEdit(detail);
    expect(input(root, 'unsubscribeUrl').value).toBe('https://example.com/cancel');
    type(root, 'unsubscribeUrl', '  https://other.example/stop  ');
    await submit(harness);
    const req = http.expectOne(`/api/subscriptions/${detail.id}`);
    expect(putBody(req).unsubscribeUrl).toBe('https://other.example/stop');
    req.flush({}, { status: 500, statusText: 'x' });
  });

  it('sets the link on add', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    type(root, 'unsubscribeUrl', 'https://example.com/cancel');
    await submit(harness);
    expect(putBody(http.expectOne('/api/subscriptions')).unsubscribeUrl).toBe('https://example.com/cancel');
  });

  it('clears the link: an emptied field sends an explicit null in the real request body', async () => {
    const detail = detailFixture();
    const { harness, root } = await openEdit(detail);
    type(root, 'unsubscribeUrl', '   ');
    await submit(harness);
    const req = http.expectOne(`/api/subscriptions/${detail.id}`);
    expect(req.request.body).toHaveProperty('unsubscribeUrl', null);
    expect(putBody(req).unsubscribeUrl).toBeNull();
  });

  it.each(['HTTPS://Example.com/cancel', 'Https://example.com/cancel'])('accepts %s and sends it as typed', async (url) => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    type(root, 'unsubscribeUrl', url);
    await submit(harness);
    expect(putBody(http.expectOne('/api/subscriptions')).unsubscribeUrl).toBe(url);
  });

  it.each([
    'http://example.com/cancel',
    'javascript:alert(1)',
    'https://',
    'https://u@a.example/x',
    'https://a.example\\@b.example',
    'https://example.com/a b',
    'example.com/cancel',
    'https://example.com/' + 'a'.repeat(2048),
  ])('rejects %s before sending anything and shows a localized error', async (url) => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    type(root, 'unsubscribeUrl', url);
    await submit(harness);
    http.expectNone('/api/subscriptions');
    expect(root.querySelector('#unsubscribeUrl-error')?.textContent).toContain(en['subscriptions.error.unsubscribeUrl']);
    expect(input(root, 'unsubscribeUrl').getAttribute('aria-invalid')).toBe('true');
  });

  it('maps a server unsubscribeUrl error to a localized message', async () => {
    const { harness, root } = await openAdd();
    await fillValidAdd(root);
    await submit(harness);
    http.expectOne('/api/subscriptions').flush({ traceId: 't', errors: { unsubscribeUrl: ['x'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(root.querySelector('#unsubscribeUrl-error')?.textContent).toContain(en['subscriptions.error.unsubscribeUrl']);
    expect(root.textContent).not.toContain('"x"');
  });

  it('does not pre-fill the field from the catalog: the effective URL already falls back server-side', async () => {
    const { root } = await openEdit({ ...detailFixture(), unsubscribeUrl: null, effectiveUnsubscribeUrl: 'https://catalog.example/cancel' });
    expect(input(root, 'unsubscribeUrl').value).toBe('');
  });
});

describe('SubscriptionFormPage catalog picker', () => {
  let http: HttpTestingController;

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
    TestBed.inject(SessionService).current.set({ state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' });
  });
  afterEach(() => http.verify());

  const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);

  const plan = (over: Partial<CatalogPlan> = {}): CatalogPlan => ({
    country: 'DK', name: 'Basic', currency: 'DKK', minorUnit: 2, amount: '99.00', cadenceUnit: 'month', cadenceCount: 1, upcomingChange: null, ...over,
  });
  const service = (id: string, name: string, plans: CatalogPlan[], category = 'Streaming'): CatalogService => ({
    id, name, category, logoUrl: null, lastVerified: '2026-11-01', plans, unsubscribeUrl: null,
  });
  const catalogOf = (...services: CatalogService[]): Catalog => ({ services, defaultCountry: 'DK' });

  async function settle(harness: RouterTestingHarness) {
    harness.detectChanges();
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  async function openAdd(catalog: Catalog | 'fail' = catalogFixture(), defaultCurrency: string | null = 'DKK') {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/subscriptions/new', SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush({ ...optionsFixture(), defaultCurrency });
    http.expectOne('/api/subscriptions').flush(overviewFixture());
    const req = http.expectOne('/api/subscriptions/catalog');
    if (catalog === 'fail') req.flush({}, { status: 500, statusText: 'Server Error' });
    else req.flush(catalog);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    await settle(harness);
    return { harness, root: harness.routeNativeElement as HTMLElement };
  }

  async function openEdit(detail: SubscriptionDetail, catalog: Catalog = catalogFixture()) {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/subscriptions/${detail.id}/edit`, SubscriptionFormPage);
    http.expectOne('/api/subscriptions/options').flush(optionsFixture());
    http.expectOne('/api/subscriptions').flush(overviewFixture());
    http.expectOne('/api/subscriptions/catalog').flush(catalog);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne(`/api/subscriptions/${detail.id}`).flush(detail);
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    await settle(harness);
    return { harness, root: harness.routeNativeElement as HTMLElement };
  }

  const field = (root: HTMLElement, id: string) => root.querySelector(`#${id}`) as HTMLInputElement & HTMLSelectElement;
  const results = (root: HTMLElement) => queryAll<HTMLButtonElement>(root, '[data-picker-result]', { min: 0 });

  function type(root: HTMLElement, id: string, value: string) {
    const element = field(root, id);
    element.value = value;
    element.dispatchEvent(new Event('input'));
  }

  async function pick(harness: RouterTestingHarness, root: HTMLElement, name: string) {
    const button = results(root).find((b) => b.textContent?.includes(name));
    expect(button, `result ${name}`).toBeDefined();
    button?.click();
    await settle(harness);
  }

  async function submit(harness: RouterTestingHarness) {
    (harness.routeNativeElement?.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(harness);
  }

  it('picking prefills the fields, which stay editable, and the add posts the catalog id', async () => {
    const { harness, root } = await openAdd();
    await pick(harness, root, 'StreamCo');
    expect(field(root, 'name').value).toBe('StreamCo');
    expect(field(root, 'category').value).toBe('Streaming');
    expect(field(root, 'currency').value).toBe('DKK');
    expect(field(root, 'amount').value).toBe('129.00');
    expect((field(root, 'cadence-month') as HTMLInputElement).checked).toBe(true);

    type(root, 'name', 'Our StreamCo');
    type(root, 'amount', '139.00');
    type(root, 'startDate', '2024-03-01');
    await submit(harness);
    const req = http.expectOne('/api/subscriptions');
    expect(req.request.body).toEqual({
      name: 'Our StreamCo', category: 'Streaming', notes: null, currency: 'DKK', startDate: '2024-03-01', endDate: null,
      amount: '139.00', cadenceUnit: 'month', cadenceCount: 1, catalogId: 'streamco', paymentOptionId: null, unsubscribeUrl: null, cancelBy: null,
    });
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('editing a prefilled field does not change the catalog id', async () => {
    const { harness, root } = await openAdd();
    await pick(harness, root, 'Music Box');
    type(root, 'name', 'Renamed');
    type(root, 'startDate', '2024-03-01');
    await submit(harness);
    const req = http.expectOne('/api/subscriptions');
    expect((req.request.body as { catalogId: string }).catalogId).toBe('music-box');
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('the catalog is not applied again after the pick: later changes are never overwritten', async () => {
    const { harness, root } = await openAdd();
    await pick(harness, root, 'StreamCo');
    type(root, 'amount', '5.00');
    type(root, 'picker-search', 'zzz');
    await settle(harness);
    expect(field(root, 'amount').value).toBe('5.00');
  });

  it('filters case- and diacritic-insensitively, shows at most eight, and announces the count politely', async () => {
    const many = Array.from({ length: 12 }, (_, i) => service(`svc-${i}`, `Café ${String.fromCharCode(65 + i)}`, [plan()]));
    const { harness, root } = await openAdd(catalogOf(...many, service('other', 'Other', [plan()])));
    expect(results(root)).toHaveLength(8);
    type(root, 'picker-search', 'CAFE');
    await settle(harness);
    expect(results(root)).toHaveLength(8);
    type(root, 'picker-search', 'cafe l');
    await settle(harness);
    expect(results(root).map((b) => b.textContent)).toEqual([expect.stringContaining('Café L')]);
    const live = root.querySelector('#picker-count') as HTMLElement;
    expect(live.getAttribute('aria-live')).toBe('polite');
    expect(live.textContent).toContain('1 services found');
    type(root, 'picker-search', 'nothing like it');
    await settle(harness);
    expect(live.textContent).toContain(en['subscriptions.picker.none']);
  });

  it('is keyboard operable: native buttons and a search field that does not submit on Enter', async () => {
    const { harness, root } = await openAdd();
    const search = field(root, 'picker-search');
    expect(search.type).toBe('search');
    const enter = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true, bubbles: true });
    search.dispatchEvent(enter);
    expect(enter.defaultPrevented).toBe(true);
    await settle(harness);
    for (const button of results(root)) {
      expect(button.tagName).toBe('BUTTON');
      expect(button.type).toBe('button');
      expect(button.tabIndex).toBeGreaterThanOrEqual(0);
    }
    expect(root.querySelector('[role="search"]')).not.toBeNull();
  });

  it('a catalog load failure still allows a free-form add and hides the picker', async () => {
    const { harness, root } = await openAdd('fail');
    expect(root.querySelector('[role="search"]')).toBeNull();
    expect(root.querySelector('#picker-failed')?.textContent).toContain(en['subscriptions.picker.loadFailed']);
    type(root, 'name', 'Free');
    type(root, 'startDate', '2024-03-01');
    type(root, 'amount', '10.00');
    await submit(harness);
    const req = http.expectOne('/api/subscriptions');
    expect((req.request.body as { catalogId: unknown }).catalogId).toBeNull();
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('"Don\'t link" clears the id and the hints but keeps typed values', async () => {
    const { harness, root } = await openAdd();
    await pick(harness, root, 'StreamCo');
    expect(root.querySelector('#catalog-verified')).not.toBeNull();
    (root.querySelector('#picker-unlink') as HTMLButtonElement).click();
    await settle(harness);
    expect(root.querySelector('#catalog-verified')).toBeNull();
    expect(root.querySelector('#picker-unlink')).toBeNull();
    expect(field(root, 'name').value).toBe('StreamCo');
    expect(field(root, 'amount').value).toBe('129.00');
    type(root, 'startDate', '2024-03-01');
    await submit(harness);
    const req = http.expectOne('/api/subscriptions');
    expect((req.request.body as { catalogId: unknown }).catalogId).toBeNull();
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('prefers the plan in the household default currency', async () => {
    const svc = service('multi', 'Multi', [plan({ name: 'Euro', currency: 'EUR', amount: '9.99' }), plan({ name: 'Krone', amount: '75.00' })]);
    const { harness, root } = await openAdd(catalogOf(svc), 'EUR');
    await pick(harness, root, 'Multi');
    expect(field(root, 'currency').value).toBe('EUR');
    expect(field(root, 'amount').value).toBe('9.99');
  });

  it('falls back to the first DK plan, then to the first plan', async () => {
    const dk = service('dk', 'Dk Service', [plan({ country: 'SE', currency: 'SEK', amount: '99.00', name: 'Swedish' }), plan({ name: 'Danish', amount: '79.00' })]);
    const other = service('other', 'Other Service', [plan({ country: 'SE', currency: 'SEK', amount: '99.00', name: 'Swedish' }), plan({ country: 'NO', currency: 'NOK', amount: '89.00', name: 'Norwegian' })]);
    const { harness, root } = await openAdd(catalogOf(dk, other), 'USD');
    await pick(harness, root, 'Dk Service');
    expect(field(root, 'currency').value).toBe('DKK');
    expect(field(root, 'amount').value).toBe('79.00');
    await pick(harness, root, 'Other Service');
    expect(field(root, 'currency').value).toBe('SEK');
    expect(field(root, 'amount').value).toBe('99.00');
  });

  it('a service with no plans prefills only name and category and leaves the price empty', async () => {
    const { harness, root } = await openAdd();
    await pick(harness, root, 'No Plans');
    expect(field(root, 'name').value).toBe('No Plans');
    expect(field(root, 'category').value).toBe('Other');
    expect(field(root, 'amount').value).toBe('');
    expect(root.querySelector('#picker-plan')).toBeNull();
  });

  it('several plans in one market use the default plan and offer a select', async () => {
    const { harness, root } = await openAdd();
    await pick(harness, root, 'StreamCo');
    const select = field(root, 'picker-plan');
    expect(select.options).toHaveLength(2);
    expect(field(root, 'amount').value).toBe('129.00');
    select.value = '1';
    select.dispatchEvent(new Event('change'));
    await settle(harness);
    expect(field(root, 'amount').value).toBe('89.00');
  });

  it('shows the verified date and an upcoming change, and hides a past one', async () => {
    const future = service('f', 'Future', [plan({ upcomingChange: { amount: '119.00', effectiveFrom: day(30) } })]);
    const past = service('p', 'Past', [plan({ upcomingChange: { amount: '119.00', effectiveFrom: day(-30) } })]);
    const { harness, root } = await openAdd(catalogOf(future, past));
    await pick(harness, root, 'Future');
    expect(root.querySelector('#catalog-verified')?.textContent).toContain('Nov 2026');
    expect(root.querySelector('#catalog-upcoming')?.textContent).toContain('119.00');
    expect(root.querySelector('#catalog-upcoming button')).toBeNull();
    await pick(harness, root, 'Past');
    expect(root.querySelector('#catalog-verified')).not.toBeNull();
    expect(root.querySelector('#catalog-upcoming')).toBeNull();
  });

  it('a second pick replaces the first pick instead of merging', async () => {
    const { harness, root } = await openAdd();
    await pick(harness, root, 'StreamCo');
    await pick(harness, root, 'Music Box');
    expect(field(root, 'name').value).toBe('Music Box');
    expect(field(root, 'category').value).toBe('Music');
    expect(field(root, 'amount').value).toBe('119.00');
    expect(root.querySelector('#picker-plan')).toBeNull();
    await pick(harness, root, 'No Plans');
    expect(field(root, 'amount').value).toBe('');
  });

  it('prefills the amount with the decimal comma in Danish', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { harness, root } = await openAdd();
    await pick(harness, root, 'StreamCo');
    expect(field(root, 'amount').value).toBe('129,00');
    expect(englishLeftovers(root)).toEqual([]);
  });

  it('edit mode keeps the link on save', async () => {
    const detail = { ...detailFixture(), catalogId: 'streamco', logoUrl: '/logos/streamco.svg' };
    const { harness, root } = await openEdit(detail);
    expect(root.querySelector('#linked-service')?.textContent).toContain('StreamCo');
    expect(root.querySelector('#picker-search')).toBeNull();
    await submit(harness);
    const req = http.expectOne(`/api/subscriptions/${detail.id}`);
    expect((req.request.body as { catalogId: unknown }).catalogId).toBe('streamco');
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('edit mode removes the link on request', async () => {
    const detail = { ...detailFixture(), catalogId: 'streamco', logoUrl: null };
    const { harness, root } = await openEdit(detail);
    (root.querySelector('#picker-remove-link') as HTMLButtonElement).click();
    await settle(harness);
    expect(root.querySelector('#linked-service')).toBeNull();
    await submit(harness);
    const req = http.expectOne(`/api/subscriptions/${detail.id}`);
    expect((req.request.body as { catalogId: unknown }).catalogId).toBeNull();
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('edit mode with a dangling catalog id keeps the linked line and submits the stored id unchanged', async () => {
    const detail = { ...detailFixture(), catalogId: 'gone-service', logoUrl: null };
    const { harness, root } = await openEdit(detail);
    expect(root.querySelector('#linked-service')?.textContent).toContain('gone-service');
    await submit(harness);
    const req = http.expectOne(`/api/subscriptions/${detail.id}`);
    expect((req.request.body as { catalogId: unknown }).catalogId).toBe('gone-service');
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('maps a catalogId field error to a localized message', async () => {
    const { harness, root } = await openAdd();
    await pick(harness, root, 'StreamCo');
    type(root, 'startDate', '2024-03-01');
    await submit(harness);
    http.expectOne('/api/subscriptions').flush({ traceId: 't', errors: { catalogId: ['x'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(root.textContent).toContain(en['subscriptions.error.catalogId']);
  });

});
