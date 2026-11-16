import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { routes } from '../app.routes';
import { da } from '../i18n/catalog.da';
import { en } from '../i18n/catalog.en';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService } from '../i18n/i18n';
import { SessionService, csrfInterceptor } from '../session';
import { PaymentOptionsPage } from './payment-options-page';
import { PaymentOption, PaymentOptionList } from './subscription.models';
import { englishLeftovers } from './testing/english-leftovers';
import { paymentOptionListFixture, suggestionListFixture } from './testing/fixtures';
import '../app.config';

const VISA = '0192f3c1a1b27c4e8d2e5f60718293b5';
const PAYPAL = '0192f3c1a1b27c4e8d2e5f60718293d7';

describe('PaymentOptionsPage', () => {
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

  const settle = async (harness: RouterTestingHarness) => {
    harness.detectChanges();
    await harness.fixture.whenStable();
    harness.detectChanges();
  };

  async function open(list: PaymentOptionList = paymentOptionListFixture(), suggestions: 'fail' | 'ok' = 'ok') {
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/payment-options', PaymentOptionsPage);
    http.expectOne('/api/payment-options').flush(list);
    const request = http.expectOne('/api/payment-options/suggestions');
    if (suggestions === 'fail') request.flush({}, { status: 500, statusText: 'Server Error' });
    else request.flush(suggestionListFixture());
    await settle(harness);
    return { harness, component, root: harness.routeNativeElement as HTMLElement };
  }

  const el = <T extends HTMLElement = HTMLElement>(root: HTMLElement, selector: string) => root.querySelector(selector) as T;
  const plain = (value: string | null | undefined) => (value ?? '').replace(/\s+/g, ' ').trim();
  const items = (root: HTMLElement) => Array.from(root.querySelectorAll('.option-list li'));
  const buttonByText = (root: HTMLElement, text: string) =>
    Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim() === text) as HTMLButtonElement;

  function type(root: HTMLElement, id: string, value: string) {
    const field = el<HTMLInputElement>(root, `#${id}`);
    field.value = value;
    field.dispatchEvent(new Event('input'));
  }

  async function submitForm(harness: RouterTestingHarness, selector: string) {
    (harness.routeNativeElement?.querySelector(selector) as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(harness);
  }

  /** After a write the page reloads the list. */
  async function reload(harness: RouterTestingHarness, options: PaymentOption[]) {
    http.expectOne('/api/payment-options').flush({ options });
    await settle(harness);
  }

  const created = (name: string, label: string | null = null): PaymentOption => ({ id: PAYPAL, version: 1, name, label, removed: false });

  it('lists the options that are not removed, with their label, and the suggestions as chips', async () => {
    const { root } = await open();
    expect(el(root, 'h1').textContent).toBe(en['paymentOptions.heading']);
    expect(items(root).map((li) => plain(li.querySelector('.option-text')?.textContent))).toEqual(['Visa · Joint account']);
    expect(root.textContent).not.toContain('Old card');
    expect(Array.from(root.querySelectorAll('.suggestion')).map((b) => b.textContent?.trim())).toEqual(['PayPal', 'Visa', 'Mastercard', 'Dankort', 'MobilePay / Vipps']);
  });

  it('says so when there are no options, and links back home', async () => {
    const { root } = await open({ options: [] });
    expect(root.textContent).toContain(en['paymentOptions.empty']);
    expect(el(root, 'a[href="/"]')).toBeTruthy();
  });

  it('a suggestion prefills the name only and creates an ordinary option: no suggestion id is sent', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, '#suggest-paypal').click();
    await settle(harness);
    expect(el<HTMLInputElement>(root, '#option-name').value).toBe('PayPal');
    await submitForm(harness, '.add-form');
    const req = http.expectOne('/api/payment-options');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'PayPal', label: null });
    req.flush(created('PayPal'), { status: 201, statusText: 'Created' });
    await settle(harness);
    await reload(harness, [...paymentOptionListFixture().options, created('PayPal')]);
    expect(root.querySelector('output.status')?.textContent).toContain('PayPal was added.');
    expect(items(root).length).toBe(2);
    expect(el<HTMLInputElement>(root, '#option-name').value).toBe('');
  });

  it('adds a custom option with a trimmed name and a label, sending an explicit null for an empty label', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', '  Company card ');
    type(root, 'option-label', ' Finance ');
    await submitForm(harness, '.add-form');
    const req = http.expectOne('/api/payment-options');
    expect(req.request.body).toEqual({ name: 'Company card', label: 'Finance' });
    req.flush(created('Company card', 'Finance'), { status: 201, statusText: 'Created' });
    await settle(harness);
    await reload(harness, [created('Company card', 'Finance')]);
    expect(items(root).length).toBe(1);
  });

  it('rejects a card-number-like name or label before any request, with the localized card message', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', 'Visa');
    type(root, 'option-label', '4111 1111 1111 1111');
    await submitForm(harness, '.add-form');
    http.expectNone('/api/payment-options');
    expect(el(root, '#option-label-error').textContent).toContain(en['paymentOptions.error.cardNumber']);
    expect(el(root, '#option-label').getAttribute('aria-invalid')).toBe('true');
    expect(el(root, '#option-name').getAttribute('aria-invalid')).toBeNull();
    expect(document.activeElement?.id).toBe('option-label');

    type(root, 'option-name', '4111111111111111');
    type(root, 'option-label', '');
    await submitForm(harness, '.add-form');
    http.expectNone('/api/payment-options');
    expect(el(root, '#option-name-error').textContent).toContain(en['paymentOptions.error.cardNumber']);
  });

  it('rejects an empty name before any request', async () => {
    const { harness, root } = await open();
    await submitForm(harness, '.add-form');
    http.expectNone('/api/payment-options');
    expect(el(root, '#option-name-error').textContent).toContain(en['paymentOptions.error.name']);
  });

  it('shows a localized message for a server 400, never its English text', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', 'Visa');
    await submitForm(harness, '.add-form');
    http.expectOne('/api/payment-options').flush({ traceId: 't', errors: { name: ['English from the server'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(el(root, '#option-name-error').textContent).toContain(en['paymentOptions.error.name']);
    expect(root.textContent).not.toContain('English from the server');
  });

  it('edits the name and the label with the version in If-Match, then reloads', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#edit-${VISA}`).click();
    await settle(harness);
    expect(el<HTMLInputElement>(root, '#edit-name').value).toBe('Visa');
    expect(el<HTMLInputElement>(root, '#edit-label').value).toBe('Joint account');
    type(root, 'edit-name', 'Visa Gold');
    type(root, 'edit-label', '');
    await submitForm(harness, '.edit-form');
    const req = http.expectOne(`/api/payment-options/${VISA}`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-Match')).toBe('"3"');
    expect(req.request.headers.get('X-CSRF')).toBe('1');
    expect(req.request.body).toEqual({ name: 'Visa Gold', label: null });
    req.flush({ id: VISA, version: 4, name: 'Visa Gold', label: null, removed: false });
    await settle(harness);
    await reload(harness, [{ id: VISA, version: 4, name: 'Visa Gold', label: null, removed: false }]);
    expect(root.querySelector('.edit-form')).toBeNull();
    expect(plain(items(root)[0].querySelector('.option-text')?.textContent)).toBe('Visa Gold');
    expect(root.querySelector('output.status')?.textContent).toContain('Visa Gold was saved.');
  });

  it('cancels an edit without a request', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#edit-${VISA}`).click();
    await settle(harness);
    buttonByText(root, en['subscriptions.form.cancel']).click();
    await settle(harness);
    expect(root.querySelector('.edit-form')).toBeNull();
    expect(document.activeElement?.id).toBe(`edit-${VISA}`);
  });

  it('removes only after confirmation, with the version in If-Match, and the option leaves the list', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#remove-${VISA}`).click();
    await settle(harness);
    expect(document.activeElement?.id).toBe('confirm-option');
    expect(el(root, '#confirm-option').textContent).toContain('Visa');
    http.expectNone(`/api/payment-options/${VISA}`);
    buttonByText(root, en['paymentOptions.removeYes']).click();
    const req = http.expectOne(`/api/payment-options/${VISA}`);
    expect(req.request.method).toBe('DELETE');
    expect(req.request.headers.get('If-Match')).toBe('"3"');
    req.flush(null, { status: 204, statusText: 'No Content' });
    await settle(harness);
    await reload(harness, [{ ...paymentOptionListFixture().options[0], removed: true }]);
    expect(items(root).length).toBe(0);
    expect(root.textContent).toContain(en['paymentOptions.empty']);
    expect(root.querySelector('output.status')?.textContent).toContain('Visa was removed.');
  });

  it('keeps the option when the removal is declined', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#remove-${VISA}`).click();
    await settle(harness);
    buttonByText(root, en['detail.keep']).click();
    await settle(harness);
    expect(root.querySelector('#confirm-option')).toBeNull();
    expect(items(root).length).toBe(1);
  });

  it('shows the conflict notice on a 412 and reloads on request', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#edit-${VISA}`).click();
    await settle(harness);
    type(root, 'edit-name', 'Visa Gold');
    await submitForm(harness, '.edit-form');
    http.expectOne(`/api/payment-options/${VISA}`).flush({ traceId: 'trace-412', currentVersion: 9 }, { status: 412, statusText: 'Precondition Failed' });
    await settle(harness);
    expect(el(root, '.notice[role=alert]').textContent).toContain(en['paymentOptions.conflict']);
    expect(root.textContent).toContain('trace-412');
    buttonByText(root, en['subscriptions.form.reloadLatest']).click();
    await settle(harness);
    http.expectOne('/api/payment-options').flush({ options: [{ id: VISA, version: 9, name: 'Visa X', label: null, removed: false }] });
    await settle(harness);
    expect(root.textContent).not.toContain(en['paymentOptions.conflict']);
    expect(plain(items(root)[0].querySelector('.option-text')?.textContent)).toBe('Visa X');
  });

  it('tells the member when the option is gone (404) and refreshes the list', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#remove-${VISA}`).click();
    await settle(harness);
    buttonByText(root, en['paymentOptions.removeYes']).click();
    http.expectOne(`/api/payment-options/${VISA}`).flush({ traceId: 't' }, { status: 404, statusText: 'Not Found' });
    await settle(harness);
    await reload(harness, []);
    expect(el(root, 'p.error[role=alert]').textContent).toContain(en['paymentOptions.gone']);
    expect(items(root).length).toBe(0);
  });

  it('shows the generic failure with the trace id for any other error, never backend English', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', 'Visa');
    await submitForm(harness, '.add-form');
    http.expectOne('/api/payment-options').flush({ traceId: 'trace-500', title: 'English' }, { status: 500, statusText: 'Server Error' });
    await settle(harness);
    expect(el(root, 'p.error[role=alert]').textContent).toContain(en['paymentOptions.saveFailed']);
    expect(root.textContent).toContain('trace-500');
    expect(root.textContent).not.toContain('English from');
  });

  it('shows a retry when the options cannot be loaded, and loads again', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/payment-options', PaymentOptionsPage);
    http.expectOne('/api/payment-options').flush({ traceId: 'trace-load' }, { status: 503, statusText: 'Unavailable' });
    // The failed list cancels the parallel suggestions request.
    expect(http.expectOne('/api/payment-options/suggestions').cancelled).toBe(true);
    await settle(harness);
    const root = harness.routeNativeElement as HTMLElement;
    expect(el(root, '.notice[role=alert]').textContent).toContain(en['paymentOptions.loadFailed']);
    expect(root.textContent).toContain('trace-load');
    buttonByText(root, en['common.retry']).click();
    await settle(harness);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/payment-options/suggestions').flush(suggestionListFixture());
    await settle(harness);
    expect(items(root).length).toBe(1);
  });

  it('still works without suggestions when that request fails', async () => {
    const { root } = await open(paymentOptionListFixture(), 'fail');
    expect(root.querySelector('.suggestion')).toBeNull();
    expect(root.querySelector('#option-name')).toBeTruthy();
  });

  it('renders in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { harness, root } = await open();
    expect(el(root, 'h1').textContent).toBe(da['paymentOptions.heading']);
    el<HTMLButtonElement>(root, `#edit-${VISA}`).click();
    await settle(harness);
    expect(englishLeftovers(root)).toEqual([]);
    buttonByText(root, da['subscriptions.form.cancel']).click();
    await settle(harness);
    el<HTMLButtonElement>(root, `#remove-${VISA}`).click();
    await settle(harness);
    expect(englishLeftovers(root)).toEqual([]);
  });

  it('is reached through the household guard, so an anonymous member is sent to sign in', async () => {
    TestBed.inject(SessionService).current.set({ state: 'anonymous' });
    await RouterTestingHarness.create('/payment-options');
    http.expectNone((request: { url: string }) => request.url.startsWith('/api/payment-options'));
  });
});
