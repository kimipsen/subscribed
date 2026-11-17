import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { routes } from '../app.routes';
import { da } from '../i18n/catalog.da';
import { en } from '../i18n/catalog.en';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService } from '../i18n/i18n';
import { SessionService, csrfInterceptor } from '../session';
import { query, queryAll } from '../testing/query';
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
  const items = (root: HTMLElement) => queryAll(root, '[data-option-list] li', { min: 0 });
  const buttonByText = (root: HTMLElement, text: string) =>
    Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim() === text) as HTMLButtonElement;

  function type(root: HTMLElement, id: string, value: string) {
    const field = el<HTMLInputElement>(root, `#${id}`);
    field.value = value;
    field.dispatchEvent(new Event('input'));
  }

  async function submitForm(harness: RouterTestingHarness, selector: string) {
    query<HTMLFormElement>(harness.routeNativeElement as HTMLElement, selector).dispatchEvent(new Event('submit'));
    await settle(harness);
  }

  /** After a write the page reloads the list. */
  async function reload(harness: RouterTestingHarness, options: PaymentOption[]) {
    http.expectOne('/api/payment-options').flush({ options });
    await settle(harness);
  }

  const created = (name: string, label: string | null = null, brand: PaymentOption['brand'] = null): PaymentOption => ({ id: PAYPAL, version: 1, name, label, brand, removed: false });

  /** Chooses a value in a native select the way a member does. */
  async function choose(harness: RouterTestingHarness, id: string, label: string) {
    const select = el<HTMLSelectElement>(harness.routeNativeElement as HTMLElement, `#${id}`);
    const index = Array.from(select.options).findIndex((o) => o.textContent?.trim() === label);
    select.selectedIndex = index;
    select.dispatchEvent(new Event('change'));
    await settle(harness);
  }

  const selected = (root: HTMLElement, id: string) => {
    const select = el<HTMLSelectElement>(root, `#${id}`);
    return select.options[select.selectedIndex]?.textContent?.trim();
  };

  it('lists the options that are not removed, with their label, and the suggestions as chips', async () => {
    const { root } = await open();
    expect(el(root, 'h1').textContent).toBe(en['paymentOptions.heading']);
    expect(items(root).map((li) => plain(query(li, '[data-option-text]').textContent))).toEqual(['Visa · Joint account']);
    expect(root.textContent).not.toContain('Old card');
    expect(queryAll(root, '[data-suggestion]').map((b) => b.textContent?.trim())).toEqual(['PayPal', 'Visa', 'Mastercard', 'Dankort', 'MobilePay / Vipps']);
  });

  it('says so when there are no options, and links back home', async () => {
    const { root } = await open({ options: [] });
    expect(root.textContent).toContain(en['paymentOptions.empty']);
    expect(el(root, 'a[href="/"]')).toBeTruthy();
  });

  it('a suggestion prefills the name and the brand, and the request carries the brand', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, '#suggest-paypal').click();
    await settle(harness);
    expect(el<HTMLInputElement>(root, '#option-name').value).toBe('PayPal');
    expect(selected(root, 'option-brand')).toBe('PayPal');
    await submitForm(harness, '[data-add-form]');
    const req = http.expectOne('/api/payment-options');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'PayPal', label: null, brand: 'paypal' });
    req.flush(created('PayPal', null, 'paypal'), { status: 201, statusText: 'Created' });
    await settle(harness);
    await reload(harness, [...paymentOptionListFixture().options, created('PayPal', null, 'paypal')]);
    expect(query(root, '[data-action-notice] [role=status]').textContent).toContain('PayPal was added.');
    expect(items(root)).toHaveLength(2);
    expect(el<HTMLInputElement>(root, '#option-name').value).toBe('');
    expect(selected(root, 'option-brand')).toBe(en['paymentOptions.brand.none']);
  });

  it('adds a custom option with a trimmed name and a label, sending an explicit null for an empty label', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', '  Company card ');
    type(root, 'option-label', ' Finance ');
    await submitForm(harness, '[data-add-form]');
    const req = http.expectOne('/api/payment-options');
    expect(req.request.body).toEqual({ name: 'Company card', label: 'Finance', brand: null });
    req.flush(created('Company card', 'Finance'), { status: 201, statusText: 'Created' });
    await settle(harness);
    await reload(harness, [created('Company card', 'Finance')]);
    expect(items(root)).toHaveLength(1);
  });

  it('a custom-named option can be linked to a brand', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', 'Company card');
    await choose(harness, 'option-brand', 'Mastercard');
    await submitForm(harness, '[data-add-form]');
    const req = http.expectOne('/api/payment-options');
    expect(req.request.body).toEqual({ name: 'Company card', label: null, brand: 'mastercard' });
    req.flush(created('Company card', null, 'mastercard'), { status: 201, statusText: 'Created' });
    await settle(harness);
    await reload(harness, [created('Company card', null, 'mastercard')]);
  });

  it('offers None plus the five brands, and never an id the server would reject', async () => {
    const { root } = await open();
    const options = Array.from(el<HTMLSelectElement>(root, '#option-brand').options).map((o) => o.textContent?.trim());
    expect(options).toEqual([en['paymentOptions.brand.none'], 'PayPal', 'Visa', 'Mastercard', 'Dankort', 'MobilePay / Vipps']);
    expect(selected(root, 'option-brand')).toBe(en['paymentOptions.brand.none']);
  });

  it('None sends an explicit null brand, not an empty string', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, '#suggest-visa').click();
    await settle(harness);
    await choose(harness, 'option-brand', en['paymentOptions.brand.none']);
    await submitForm(harness, '[data-add-form]');
    const req = http.expectOne('/api/payment-options');
    expect(req.request.body).toEqual({ name: 'Visa', label: null, brand: null });
    req.flush(created('Visa'), { status: 201, statusText: 'Created' });
    await settle(harness);
    await reload(harness, [created('Visa')]);
  });

  it('shows a localized message for a server brand error and focuses the brand select', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', 'Visa');
    await submitForm(harness, '[data-add-form]');
    http.expectOne('/api/payment-options').flush({ traceId: 't', errors: { brand: ['English from the server'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(el(root, '#option-brand-error').textContent).toContain(en['paymentOptions.error.brand']);
    expect(el(root, '#option-brand').getAttribute('aria-invalid')).toBe('true');
    expect(root.textContent).not.toContain('English from the server');
    expect(document.activeElement?.id).toBe('option-brand');
  });

  it('shows chips and saved rows with a decorative image: the brand logo or the generic card icon', async () => {
    const options = [...paymentOptionListFixture().options, created('Cash')];
    const { root } = await open({ options });
    const chips = queryAll(root, '[data-suggestion]');
    expect(chips).toHaveLength(5);
    for (const chip of chips) expect(chip.querySelectorAll('img[alt=""]')).toHaveLength(1);
    const rows = items(root);
    expect(rows).toHaveLength(2);
    for (const row of rows) {
      expect(row.querySelectorAll('img[alt=""]')).toHaveLength(1);
      expect(row.querySelector('img')?.getAttribute('src')).toBe('/logos/generic-card.svg');
    }
  });

  it('the chip button keeps the brand name as its accessible text', async () => {
    const { root } = await open();
    expect(plain(el(root, '#suggest-mobilepay-vipps').textContent)).toBe('MobilePay / Vipps');
  });

  it('rejects a card-number-like name or label before any request, with the localized card message', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', 'Visa');
    type(root, 'option-label', '4111 1111 1111 1111');
    await submitForm(harness, '[data-add-form]');
    http.expectNone('/api/payment-options');
    expect(el(root, '#option-label-error').textContent).toContain(en['paymentOptions.error.cardNumber']);
    expect(el(root, '#option-label').getAttribute('aria-invalid')).toBe('true');
    expect(el(root, '#option-name').getAttribute('aria-invalid')).toBeNull();
    expect(document.activeElement?.id).toBe('option-label');

    type(root, 'option-name', '4111111111111111');
    type(root, 'option-label', '');
    await submitForm(harness, '[data-add-form]');
    http.expectNone('/api/payment-options');
    expect(el(root, '#option-name-error').textContent).toContain(en['paymentOptions.error.cardNumber']);
  });

  it('rejects an empty name before any request', async () => {
    const { harness, root } = await open();
    await submitForm(harness, '[data-add-form]');
    http.expectNone('/api/payment-options');
    expect(el(root, '#option-name-error').textContent).toContain(en['paymentOptions.error.name']);
  });

  it('shows a localized message for a server 400, never its English text', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', 'Visa');
    await submitForm(harness, '[data-add-form]');
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
    expect(selected(root, 'edit-brand')).toBe('Visa');
    type(root, 'edit-name', 'Visa Gold');
    type(root, 'edit-label', '');
    await submitForm(harness, '[data-edit-form]');
    const req = http.expectOne(`/api/payment-options/${VISA}`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-Match')).toBe('"3"');
    expect(req.request.headers.get('X-CSRF')).toBe('1');
    // Renaming keeps the brand: the edit form always sends the current one.
    expect(req.request.body).toEqual({ name: 'Visa Gold', label: null, brand: 'visa' });
    req.flush({ id: VISA, version: 4, name: 'Visa Gold', label: null, brand: 'visa', removed: false });
    await settle(harness);
    await reload(harness, [{ id: VISA, version: 4, name: 'Visa Gold', label: null, brand: 'visa', removed: false }]);
    expect(root.querySelector('[data-edit-form]')).toBeNull();
    expect(plain(query(items(root)[0], '[data-option-text]').textContent)).toBe('Visa Gold');
    expect(query(root, '[data-action-notice] [role=status]').textContent).toContain('Visa Gold was saved.');
  });

  it('changes the brand to None in the edit form and sends null', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#edit-${VISA}`).click();
    await settle(harness);
    await choose(harness, 'edit-brand', en['paymentOptions.brand.none']);
    await submitForm(harness, '[data-edit-form]');
    const req = http.expectOne(`/api/payment-options/${VISA}`);
    expect(req.request.body).toEqual({ name: 'Visa', label: 'Joint account', brand: null });
    req.flush({ id: VISA, version: 4, name: 'Visa', label: 'Joint account', brand: null, removed: false });
    await settle(harness);
    await reload(harness, [{ id: VISA, version: 4, name: 'Visa', label: 'Joint account', brand: null, removed: false }]);
  });

  it('an option without a brand starts the edit form on None and can be linked', async () => {
    const { harness, root } = await open({ options: [{ id: VISA, version: 3, name: 'Cash', label: null, brand: null, removed: false }] });
    el<HTMLButtonElement>(root, `#edit-${VISA}`).click();
    await settle(harness);
    expect(selected(root, 'edit-brand')).toBe(en['paymentOptions.brand.none']);
    await choose(harness, 'edit-brand', 'Dankort');
    await submitForm(harness, '[data-edit-form]');
    const req = http.expectOne(`/api/payment-options/${VISA}`);
    expect(req.request.body).toEqual({ name: 'Cash', label: null, brand: 'dankort' });
    req.flush({ id: VISA, version: 4, name: 'Cash', label: null, brand: 'dankort', removed: false });
    await settle(harness);
    await reload(harness, [{ id: VISA, version: 4, name: 'Cash', label: null, brand: 'dankort', removed: false }]);
  });

  it('cancels an edit without a request', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#edit-${VISA}`).click();
    await settle(harness);
    buttonByText(root, en['subscriptions.form.cancel']).click();
    await settle(harness);
    expect(root.querySelector('[data-edit-form]')).toBeNull();
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
    expect(items(root)).toHaveLength(0);
    expect(root.textContent).toContain(en['paymentOptions.empty']);
    expect(query(root, '[data-action-notice] [role=status]').textContent).toContain('Visa was removed.');
  });

  it('keeps the option when the removal is declined', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#remove-${VISA}`).click();
    await settle(harness);
    buttonByText(root, en['detail.keep']).click();
    await settle(harness);
    expect(root.querySelector('#confirm-option')).toBeNull();
    expect(items(root)).toHaveLength(1);
  });

  it('shows the conflict notice on a 412 and reloads on request', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#edit-${VISA}`).click();
    await settle(harness);
    type(root, 'edit-name', 'Visa Gold');
    await submitForm(harness, '[data-edit-form]');
    http.expectOne(`/api/payment-options/${VISA}`).flush({ traceId: 'trace-412', currentVersion: 9 }, { status: 412, statusText: 'Precondition Failed' });
    await settle(harness);
    expect(query(root, 'app-notice [role=alert]').textContent).toContain(en['paymentOptions.conflict']);
    expect(root.textContent).toContain('trace-412');
    buttonByText(root, en['subscriptions.form.reloadLatest']).click();
    await settle(harness);
    http.expectOne('/api/payment-options').flush({ options: [{ id: VISA, version: 9, name: 'Visa X', label: null, brand: null, removed: false }] });
    await settle(harness);
    expect(root.textContent).not.toContain(en['paymentOptions.conflict']);
    expect(plain(query(items(root)[0], '[data-option-text]').textContent)).toBe('Visa X');
  });

  it('tells the member when the option is gone (404) and refreshes the list', async () => {
    const { harness, root } = await open();
    el<HTMLButtonElement>(root, `#remove-${VISA}`).click();
    await settle(harness);
    buttonByText(root, en['paymentOptions.removeYes']).click();
    http.expectOne(`/api/payment-options/${VISA}`).flush({ traceId: 't' }, { status: 404, statusText: 'Not Found' });
    await settle(harness);
    await reload(harness, []);
    expect(query(root, '[data-action-problem] [role=alert]').textContent).toContain(en['paymentOptions.gone']);
    expect(items(root)).toHaveLength(0);
  });

  it('shows the generic failure with the trace id for any other error, never backend English', async () => {
    const { harness, root } = await open();
    type(root, 'option-name', 'Visa');
    await submitForm(harness, '[data-add-form]');
    http.expectOne('/api/payment-options').flush({ traceId: 'trace-500', title: 'English' }, { status: 500, statusText: 'Server Error' });
    await settle(harness);
    expect(query(root, '[data-action-problem] [role=alert]').textContent).toContain(en['paymentOptions.saveFailed']);
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
    expect(query(root, 'app-notice [role=alert]').textContent).toContain(en['paymentOptions.loadFailed']);
    expect(root.textContent).toContain('trace-load');
    buttonByText(root, en['common.retry']).click();
    await settle(harness);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/payment-options/suggestions').flush(suggestionListFixture());
    await settle(harness);
    expect(items(root)).toHaveLength(1);
  });

  it('still works without suggestions when that request fails', async () => {
    const { root } = await open(paymentOptionListFixture(), 'fail');
    expect(root.querySelector('[data-suggestion]')).toBeNull();
    expect(root.querySelector('#option-name')).toBeTruthy();
  });

  it('renders in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { harness, root } = await open();
    expect(el(root, 'h1').textContent).toBe(da['paymentOptions.heading']);
    expect(query(root, 'label[for="option-name"]').textContent).toContain(da['paymentOptions.field.name']);
    expect(query(root, '#option-label-hint').textContent).toContain(da['paymentOptions.field.labelHint']);
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
    expect(TestBed.inject(Router).url).toBe('/welcome?returnUrl=%2Fpayment-options');
  });
});
