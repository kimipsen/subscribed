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
import { SubscriptionDetailPage } from './subscription-detail-page';
import { SubscriptionsService } from './subscriptions.service';
import { PaymentOptionList, SubscriptionDetail } from './subscription.models';
import { englishLeftovers } from './testing/english-leftovers';
import { detailFixture, overviewFixture, paymentOptionListFixture } from './testing/fixtures';
import { PERIOD_STORAGE_KEY, flushPeriods, memoryStorage, periodOptionsFixture, periodTestProviders, throwingStorage } from './testing/period-providers';
import '../app.config';

const Q = '?period=year-2026';

const plain = (value: string | null | undefined) => (value ?? '').replace(/[\u00a0\u202f]/g, ' ').replace(/\s+/g, ' ').trim();

function plusDays(iso: string, days: number): string {
  const date = new Date(`${iso}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

describe('SubscriptionDetailPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(SessionService).current.set({ state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' });
  });
  afterEach(() => http.verify());

  async function settle(harness: RouterTestingHarness) {
    harness.detectChanges();
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  async function open(detail: SubscriptionDetail = detailFixture(), options: PaymentOptionList = paymentOptionListFixture()) {
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl(`/subscriptions/${detail.id}`, SubscriptionDetailPage);
    http.expectOne('/api/payment-options').flush(options);
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    http.expectOne(`/api/subscriptions/${detail.id}${Q}`).flush(detail);
    await settle(harness);
    flushPeriods(http);
    await settle(harness);
    return { harness, component, root: harness.routeNativeElement as HTMLElement, detail };
  }

  const el = <T extends HTMLElement = HTMLElement>(root: HTMLElement, selector: string) => root.querySelector(selector) as T;
  const buttonByText = (root: HTMLElement, text: string) =>
    Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim() === text) as HTMLButtonElement;
  const rows = (root: HTMLElement) => Array.from(root.querySelectorAll('tbody tr')) as HTMLElement[];

  function type(root: HTMLElement, id: string, value: string, event = 'input') {
    const field = el<HTMLInputElement>(root, `#${id}`);
    field.value = value;
    field.dispatchEvent(new Event(event));
  }

  async function submitForm(harness: RouterTestingHarness, selector: string) {
    (harness.routeNativeElement?.querySelector(selector) as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(harness);
  }

  const entrySubmit = (harness: RouterTestingHarness) => submitForm(harness, '.entry-form');
  /** Removing the subscription answers 204 with no body, so its DELETE carries no period. */
  const removeRequest = (detail: SubscriptionDetail) => http.expectOne(`/api/subscriptions/${detail.id}`);
  const request = (detail: SubscriptionDetail, suffix = '') => http.expectOne(`/api/subscriptions/${detail.id}${suffix}${Q}`);

  async function respond(harness: RouterTestingHarness, req: TestRequest, body: SubscriptionDetail) {
    req.flush(body);
    await settle(harness);
  }

  const bump = (detail: SubscriptionDetail, changes: Partial<SubscriptionDetail> = {}): SubscriptionDetail =>
    ({ ...structuredClone(detail), ...changes, version: detail.version + 1 });

  // ---- display ---------------------------------------------------------------------------------------

  it('shows the logo beside the heading and falls back to the letter when it is broken', async () => {
    const { harness, root, detail } = await open({ ...detailFixture(), catalogId: 'streamco', logoUrl: '/logos/streamco.svg' });
    expect(root.querySelector('.title-row img')?.getAttribute('src')).toBe('/logos/streamco.svg');
    expect(root.querySelector('.title-row img')?.getAttribute('alt')).toBe('');
    expect(el(root, 'h1').textContent).toBe(detail.name);
    root.querySelector('.title-row img')?.dispatchEvent(new Event('error'));
    await settle(harness);
    expect(root.querySelector('.title-row img')).toBeNull();
    expect(root.querySelector('.title-row span[aria-hidden="true"]')?.textContent?.trim()).toBe(Array.from(detail.name)[0].toUpperCase());
  });

  it('shows the initial letter when the subscription has no logo', async () => {
    const { root, detail } = await open();
    expect(root.querySelector('.title-row img')).toBeNull();
    expect(root.querySelector('.title-row span[aria-hidden="true"]')?.textContent?.trim()).toBe(Array.from(detail.name)[0].toUpperCase());
  });

  it('shows the history chronologically with changes and the Upcoming and Current badges', async () => {
    const { root, detail } = await open();
    const body = rows(root);
    expect(body.length).toBe(detail.priceHistory.length);
    expect(body[0].textContent).toContain(en['detail.noPreviousPrice']);
    expect(body[0].textContent).toContain(en['detail.initialPrice']);
    for (const [index, entry] of detail.priceHistory.entries()) {
      expect(plain(body[index].textContent), entry.entryId).toContain(plain(entry.amount.replace('.00', '')));
      if (entry.change) {
        expect(plain(body[index].textContent)).toContain(`+DKK ${entry.change.amount}`);
        expect(plain(body[index].textContent)).toContain(`+${entry.change.percent} %`);
      }
      expect(body[index].textContent?.includes(en['detail.badge.current']), `current ${index}`).toBe(entry.current);
      expect(body[index].textContent?.includes(en['detail.badge.upcoming']), `upcoming ${index}`).toBe(entry.upcoming);
    }
    expect(el(root, 'h1').textContent).toBe(detail.name);
    expect(root.textContent).toContain(detail.notes as string);
  });

  it('shows the amount change and no percentage, no NaN and no empty brackets, after a 0.00 entry (BR-018)', async () => {
    const detail = detailFixture();
    detail.priceHistory[0].amount = '0.00';
    detail.priceHistory[0].normalizedMonthly = '0.00';
    detail.priceHistory[1].change = { amount: '69.00', percent: null };
    const { root } = await open(detail);
    const text = plain(rows(root)[1].textContent);
    expect(text).toContain('+DKK 69.00');
    expect(text).not.toContain('NaN');
    expect(text).not.toContain('()');
    expect(text).not.toContain('%');
  });

  // ---- price entries ---------------------------------------------------------------------------------

  it('adds entries with the exact four-key body, and a second add uses the version from the first response (EX-007, EX-008)', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', plusDays(detail.startDate, 5));
    type(root, 'entry-amount', '69.50');
    await entrySubmit(harness);
    const first = request(detail, '/prices');
    expect(first.request.method).toBe('POST');
    expect(first.request.headers.get('If-Match')).toBe(`"${detail.version}"`);
    expect(first.request.headers.get('X-CSRF')).toBe('1');
    expect(first.request.body).toEqual({ effectiveFrom: plusDays(detail.startDate, 5), amount: '69.50', cadenceUnit: 'month', cadenceCount: 1, paymentOptionId: null });
    const afterFirst = bump(detail);
    await respond(harness, first, afterFirst);

    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', plusDays(detail.startDate, 6));
    type(root, 'entry-amount', '999.00');
    el<HTMLInputElement>(root, '#entry-cadence-year').click();
    await settle(harness);
    await entrySubmit(harness);
    const second = request(detail, '/prices');
    expect(second.request.headers.get('If-Match')).toBe(`"${afterFirst.version}"`);
    expect(second.request.body).toEqual({ effectiveFrom: plusDays(detail.startDate, 6), amount: '999.00', cadenceUnit: 'year', cadenceCount: 1, paymentOptionId: null });
    second.flush(bump(afterFirst));
  });

  it('sends the canonical amount when a Danish member types a decimal comma (D-7)', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', plusDays(detail.startDate, 5));
    type(root, 'entry-amount', '69,50');
    await entrySubmit(harness);
    const req = request(detail, '/prices');
    expect((req.request.body as { amount: string }).amount).toBe('69.50');
    req.flush(bump(detail));
  });

  it('corrects the initial entry amount with its date fixed to the start date, never blocked (BR-014)', async () => {
    const { harness, root, detail } = await open();
    const initial = detail.priceHistory.find((e) => e.initial)!;
    el<HTMLButtonElement>(root, `#correct-${initial.entryId}`).click();
    await settle(harness);
    expect(el<HTMLInputElement>(root, '#entry-effectiveFrom').disabled).toBe(true);
    expect(root.textContent).toContain(en['detail.entry.initialDateHint']);
    type(root, 'entry-amount', '49.00');
    await entrySubmit(harness);
    const req = request(detail, `/prices/${initial.entryId}`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-Match')).toBe(`"${detail.version}"`);
    expect(req.request.body).toEqual({ effectiveFrom: detail.startDate, amount: '49.00', cadenceUnit: 'month', cadenceCount: 1, paymentOptionId: null });
    req.flush(bump(detail));
  });

  it('corrects a non-initial entry keeping its date with that entry\'s own date and cadence', async () => {
    const { harness, root, detail } = await open();
    const entry = detail.priceHistory.find((e) => !e.initial && !e.upcoming)!;
    el<HTMLButtonElement>(root, `#correct-${entry.entryId}`).click();
    await settle(harness);
    type(root, 'entry-amount', '65.00');
    await entrySubmit(harness);
    const req = request(detail, `/prices/${entry.entryId}`);
    expect(req.request.body).toEqual({ effectiveFrom: entry.effectiveFrom, amount: '65.00', cadenceUnit: 'month', cadenceCount: 1, paymentOptionId: null });
    req.flush(bump(detail));
  });

  it('pre-fills a Danish correction as 69,00 and sends the canonical 69.00 when only the date changes (D-7)', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { harness, root, detail } = await open();
    const entry = detail.priceHistory.find((e) => !e.initial && !e.upcoming)!;
    el<HTMLButtonElement>(root, `#correct-${entry.entryId}`).click();
    await settle(harness);
    expect(el<HTMLInputElement>(root, '#entry-amount').value).toBe(entry.amount.replace('.', ','));
    type(root, 'entry-effectiveFrom', plusDays(entry.effectiveFrom, 1));
    await entrySubmit(harness);
    const req = request(detail, `/prices/${entry.entryId}`);
    expect((req.request.body as { amount: string }).amount).toBe(entry.amount);
    req.flush(bump(detail));
  });

  it('pre-fills a 1490.00 entry as 1490,00 in Danish, valid and never grouped', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const detail = detailFixture();
    const entry = detail.priceHistory.find((e) => !e.initial && !e.upcoming)!;
    entry.amount = '1490.00';
    const { harness, root } = await open(detail);
    el<HTMLButtonElement>(root, `#correct-${entry.entryId}`).click();
    await settle(harness);
    expect(el<HTMLInputElement>(root, '#entry-amount').value).toBe('1490,00');
    type(root, 'entry-effectiveFrom', plusDays(entry.effectiveFrom, 1));
    await entrySubmit(harness);
    expect(el(root, '#entry-amount').getAttribute('aria-invalid')).toBeNull();
    const req = request(detail, `/prices/${entry.entryId}`);
    expect((req.request.body as { amount: string }).amount).toBe('1490.00');
    req.flush(bump(detail));
  });

  it('cannot remove the initial entry, and removes another after confirmation with If-Match', async () => {
    const { harness, root, detail } = await open();
    const initial = detail.priceHistory.find((e) => e.initial)!;
    const second = detail.priceHistory.find((e) => !e.initial && !e.upcoming)!;
    expect(root.querySelector(`#remove-${initial.entryId}`)).toBeNull();
    el<HTMLButtonElement>(root, `#remove-${second.entryId}`).click();
    await settle(harness);
    http.expectNone(`/api/subscriptions/${detail.id}/prices/${second.entryId}${Q}`);
    expect(document.activeElement?.id).toBe('confirm-entry');
    buttonByText(root, en['detail.entry.removeYes']).click();
    const req = request(detail, `/prices/${second.entryId}`);
    expect(req.request.method).toBe('DELETE');
    expect(req.request.headers.get('If-Match')).toBe(`"${detail.version}"`);
    await respond(harness, req, bump(detail, { priceHistory: detail.priceHistory.filter((e) => e.entryId !== second.entryId) }));
    expect(rows(root).length).toBe(detail.priceHistory.length - 1);
  });

  it('returns focus to the trigger when a confirmation is cancelled', async () => {
    const { harness, root, detail } = await open();
    const second = detail.priceHistory.find((e) => !e.initial && !e.upcoming)!;
    el<HTMLButtonElement>(root, `#remove-${second.entryId}`).click();
    await settle(harness);
    buttonByText(root, en['detail.keep']).click();
    await settle(harness);
    expect(document.activeElement?.id).toBe(`remove-${second.entryId}`);
  });

  // ---- field errors (BR-023, AC-002) -----------------------------------------------------------------

  it('flags a date another entry already has before sending, and sends nothing', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', detail.priceHistory[1].effectiveFrom);
    type(root, 'entry-amount', '70.00');
    await entrySubmit(harness);
    http.expectNone(`/api/subscriptions/${detail.id}/prices${Q}`);
    expect(el(root, '#entry-effectiveFrom').getAttribute('aria-invalid')).toBe('true');
    expect(el(root, '#entry-effectiveFrom').getAttribute('aria-describedby')).toContain('entry-effectiveFrom-error');
    expect(el(root, '#entry-effectiveFrom-error').textContent).toContain(en['detail.error.effectiveFrom']);
  });

  it('shows a server effectiveFrom error next to the date, keeping the input, history and version', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', plusDays(detail.startDate, 5));
    type(root, 'entry-amount', '70.00');
    await entrySubmit(harness);
    request(detail, '/prices').flush({ traceId: 't', errors: { effectiveFrom: ['Backend English'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(el(root, '#entry-effectiveFrom').getAttribute('aria-invalid')).toBe('true');
    expect(el(root, '#entry-effectiveFrom-error').textContent).toContain(en['detail.error.effectiveFrom']);
    expect(root.textContent).not.toContain('Backend English');
    expect(el<HTMLInputElement>(root, '#entry-amount').value).toBe('70.00');
    expect(rows(root).length).toBe(detail.priceHistory.length);
  });

  it('shows a server amount error on a correction next to the amount field', async () => {
    const { harness, root, detail } = await open();
    const entry = detail.priceHistory.find((e) => !e.initial && !e.upcoming)!;
    el<HTMLButtonElement>(root, `#correct-${entry.entryId}`).click();
    await settle(harness);
    await entrySubmit(harness);
    request(detail, `/prices/${entry.entryId}`).flush({ traceId: 't', errors: { amount: ['x'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(el(root, '#entry-amount').getAttribute('aria-invalid')).toBe('true');
    expect(el(root, '#entry-amount').getAttribute('aria-describedby')).toContain('entry-amount-error');
    expect(el(root, '#entry-amount-error').textContent).toContain('Enter a valid amount');
  });

  it('shows a server endDate error next to the end-date field', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#end-open').click();
    await settle(harness);
    expect(document.activeElement?.id).toBe('end-date');
    type(root, 'end-date', plusDays(detail.startDate, 2000));
    await submitForm(harness, 'form:not(.entry-form)');
    request(detail).flush({ traceId: 't', errors: { endDate: ['x'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(el(root, '#end-date').getAttribute('aria-invalid')).toBe('true');
    expect(el(root, '#end-date-error').textContent).toContain(en['detail.end.error']);
  });

  it('shows the generic failure for an unknown server key, never backend English, and keeps the input', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', plusDays(detail.startDate, 5));
    type(root, 'entry-amount', '70.00');
    await entrySubmit(harness);
    request(detail, '/prices').flush({ traceId: 't', errors: { brandNew: ['Backend English'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(root.textContent).toContain(en['subscriptions.form.saveFailed']);
    expect(root.textContent).not.toContain('Backend English');
    expect(el<HTMLInputElement>(root, '#entry-amount').value).toBe('70.00');
  });

  it('rejects an end date before the last price before sending', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#end-open').click();
    await settle(harness);
    type(root, 'end-date', detail.startDate);
    await submitForm(harness, 'form:not(.entry-form)');
    http.expectNone(`/api/subscriptions/${detail.id}${Q}`);
    expect(el(root, '#end-date-error').textContent).toContain(en['detail.end.error']);
  });

  // ---- payment options -------------------------------------------------------------------------------

  const VISA = '0192f3c1a1b27c4e8d2e5f60718293b5';
  const OLD = '0192f3c1a1b27c4e8d2e5f60718293c6';

  /** The first entry uses the removed option, the current one uses Visa, and the rest have none. */
  function withOptions(): SubscriptionDetail {
    const detail = detailFixture();
    detail.priceHistory[0].paymentOptionId = OLD;
    const current = detail.priceHistory.find((e) => e.current)!;
    current.paymentOptionId = VISA;
    detail.currentPrice.paymentOptionId = VISA;
    return detail;
  }

  const optionChoices = (root: HTMLElement) =>
    Array.from(root.querySelectorAll<HTMLOptionElement>('#entry-paymentOption option')).map((o) => ({ value: o.value, text: plain(o.textContent), disabled: o.disabled }));

  async function pickOption(harness: RouterTestingHarness, root: HTMLElement, value: string) {
    const select = el<HTMLSelectElement>(root, '#entry-paymentOption');
    select.value = value;
    select.dispatchEvent(new Event('change'));
    await settle(harness);
  }

  it('shows a Payment option column: the name and label, "(removed)" for a removed option, and a dash for none', async () => {
    const { root, detail } = await open(withOptions());
    expect(Array.from(root.querySelectorAll('thead th')).map((th) => plain(th.textContent))).toEqual([
      en['detail.col.period'], en['detail.col.price'], en['detail.col.perMonth'], en['detail.col.change'], en['detail.col.paymentOption'], en['detail.col.actions'],
    ]);
    const cells = rows(root).map((row) => plain(row.querySelectorAll('td')[3].textContent));
    expect(cells[0]).toBe(`Old card ${en['detail.paymentOption.removed']}`);
    const current = detail.priceHistory.findIndex((e) => e.current);
    expect(cells[current]).toBe('Visa · Joint account');
    expect(cells.filter((c) => c === '—')).toHaveLength(detail.priceHistory.length - 2);
  });

  it('shows a decorative logo before the option text: the brand logo or the generic icon, also for a removed option', async () => {
    const options = paymentOptionListFixture();
    options.options.push({ id: '0192f3c1a1b27c4e8d2e5f60718293e8', version: 1, name: 'Cash', label: null, brand: null, removed: false });
    const detail = withOptions();
    const unlinked = detail.priceHistory.findIndex((e) => !e.current && e.paymentOptionId === null);
    detail.priceHistory[unlinked].paymentOptionId = '0192f3c1a1b27c4e8d2e5f60718293e8';
    const { root } = await open(detail, options);
    const cell = (index: number) => rows(root)[index].querySelectorAll('td')[3];
    const current = detail.priceHistory.findIndex((e) => e.current);
    for (const index of [0, current, unlinked]) {
      expect(cell(index).querySelectorAll('img[alt=""]')).toHaveLength(1);
      expect(cell(index).querySelector('img')?.getAttribute('src')).toBe('/logos/generic-card.svg');
    }
    // The removed option keeps its logo and its removed marker.
    expect(plain(cell(0).textContent)).toBe(`Old card ${en['detail.paymentOption.removed']}`);
    // Rows without an option show a dash and no logo.
    const none = detail.priceHistory.findIndex((e, i) => i !== 0 && i !== current && i !== unlinked);
    expect(cell(none).querySelector('img')).toBeNull();
  });

  it('resolves a history row by id, so a renamed option shows its new name in old rows', async () => {
    const renamed = paymentOptionListFixture();
    renamed.options[0] = { ...renamed.options[0], name: 'Visa Platinum', label: null };
    const { root, detail } = await open(withOptions(), renamed);
    const current = detail.priceHistory.findIndex((e) => e.current);
    expect(plain(rows(root)[current].querySelectorAll('td')[3].textContent)).toBe('Visa Platinum');
    expect(root.textContent).not.toContain('Joint account');
  });

  it('shows a dash for an option id it cannot resolve and still renders when the options fail to load', async () => {
    const detail = withOptions();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/subscriptions/${detail.id}`, SubscriptionDetailPage);
    http.expectOne('/api/payment-options').flush({ traceId: 't' }, { status: 500, statusText: 'Server Error' });
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    http.expectOne(`/api/subscriptions/${detail.id}${Q}`).flush(detail);
    await settle(harness);
    flushPeriods(http);
    await settle(harness);
    const root = harness.routeNativeElement as HTMLElement;
    expect(el(root, 'h1').textContent).toBe(detail.name);
    expect(plain(rows(root)[0].querySelectorAll('td')[3].textContent)).toBe('—');
  });

  it('offers None and the non-removed options when adding a price, starting from the current price\'s option', async () => {
    const { harness, root } = await open(withOptions());
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    expect(optionChoices(root)).toEqual([
      { value: '', text: en['paymentOptions.select.none'], disabled: false },
      { value: VISA, text: 'Visa · Joint account', disabled: false },
    ]);
    expect(el<HTMLSelectElement>(root, '#entry-paymentOption').value).toBe(VISA);
  });

  it('sends the picked option when adding a price, and an explicit null for None', async () => {
    const { harness, root, detail } = await open(withOptions());
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', plusDays(detail.startDate, 5));
    type(root, 'entry-amount', '69.50');
    await entrySubmit(harness);
    const withVisa = request(detail, '/prices');
    expect((withVisa.request.body as { paymentOptionId: string | null }).paymentOptionId).toBe(VISA);
    const afterFirst = bump(detail);
    await respond(harness, withVisa, afterFirst);

    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', plusDays(detail.startDate, 6));
    type(root, 'entry-amount', '70.00');
    await pickOption(harness, root, '');
    await entrySubmit(harness);
    const none = request(detail, '/prices');
    expect(Object.hasOwn(none.request.body as object, 'paymentOptionId')).toBe(true);
    expect((none.request.body as { paymentOptionId: string | null }).paymentOptionId).toBeNull();
    none.flush(bump(afterFirst));
  });

  it('a price-only correction keeps the entry\'s existing option: entryBody always sends the key', async () => {
    const { harness, root, detail } = await open(withOptions());
    const entry = detail.priceHistory.find((e) => e.current)!;
    el<HTMLButtonElement>(root, `#correct-${entry.entryId}`).click();
    await settle(harness);
    expect(el<HTMLSelectElement>(root, '#entry-paymentOption').value).toBe(VISA);
    type(root, 'entry-amount', '65.00');
    await entrySubmit(harness);
    const req = request(detail, `/prices/${entry.entryId}`);
    expect(req.request.body).toEqual({ effectiveFrom: entry.effectiveFrom, amount: '65.00', cadenceUnit: 'month', cadenceCount: 1, paymentOptionId: VISA });
    req.flush(bump(detail));
  });

  it('changes and clears the option of an entry when asked', async () => {
    const { harness, root, detail } = await open(withOptions());
    const entry = detail.priceHistory.find((e) => e.current)!;
    el<HTMLButtonElement>(root, `#correct-${entry.entryId}`).click();
    await settle(harness);
    await pickOption(harness, root, '');
    await entrySubmit(harness);
    const req = request(detail, `/prices/${entry.entryId}`);
    expect((req.request.body as { paymentOptionId: string | null }).paymentOptionId).toBeNull();
    req.flush(bump(detail));
  });

  it('keeps a removed option as a disabled "(removed)" entry in the correct form, and saving does not clear it', async () => {
    const { harness, root, detail } = await open(withOptions());
    const entry = detail.priceHistory[0];
    el<HTMLButtonElement>(root, `#correct-${entry.entryId}`).click();
    await settle(harness);
    expect(optionChoices(root)).toEqual([
      { value: '', text: en['paymentOptions.select.none'], disabled: false },
      { value: VISA, text: 'Visa · Joint account', disabled: false },
      { value: OLD, text: `Old card (removed)`, disabled: true },
    ]);
    expect(el<HTMLSelectElement>(root, '#entry-paymentOption').value).toBe(OLD);
    type(root, 'entry-amount', '49.00');
    await entrySubmit(harness);
    const req = request(detail, `/prices/${entry.entryId}`);
    expect((req.request.body as { paymentOptionId: string | null }).paymentOptionId).toBe(OLD);
    req.flush(bump(detail));

    // Another entry does not offer the removed option at all.
    const other = detail.priceHistory.find((e) => e.current)!;
    el<HTMLButtonElement>(root, `#correct-${other.entryId}`).click();
    await settle(harness);
    expect(optionChoices(root).map((c) => c.value)).toEqual(['', VISA]);
  });

  it('maps a paymentOptionId error from the server to a localized message beside the select', async () => {
    const { harness, root, detail } = await open(withOptions());
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', plusDays(detail.startDate, 5));
    type(root, 'entry-amount', '69.50');
    await entrySubmit(harness);
    request(detail, '/prices').flush({ traceId: 't', errors: { paymentOptionId: ['English from the server'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
    expect(el(root, '#entry-paymentOptionId-error').textContent).toContain(en['subscriptions.error.paymentOptionId']);
    expect(root.textContent).not.toContain('English from the server');
    expect(el(root, '#entry-paymentOption').getAttribute('aria-invalid')).toBe('true');
  });

  it('the details PUT carries no paymentOptionId: it neither sets nor clears an option (D9)', async () => {
    const detail = { ...withOptions(), endDate: '2099-01-01', status: 'active' as const };
    const { harness, root } = await open(detail);
    buttonByText(root, en['detail.reinstate']).click();
    await settle(harness);
    const req = request(detail);
    expect(Object.keys(req.request.body as object)).toEqual(['name', 'category', 'notes', 'currency', 'startDate', 'endDate', 'catalogId', 'unsubscribeUrl', 'cancelBy']);
    req.flush(bump(detail, { endDate: null }));
  });

  it('renders the option column and the select in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { harness, root, detail } = await open(withOptions());
    el<HTMLButtonElement>(root, `#correct-${detail.priceHistory[0].entryId}`).click();
    await settle(harness);
    expect(root.textContent).toContain(da['detail.col.paymentOption']);
    expect(root.textContent).toContain(da['detail.paymentOption.removed']);
    expect(root.textContent).toContain(da['paymentOptions.select.label']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  // ---- cancel by -------------------------------------------------------------------------------------

  const cancelByRow = (root: HTMLElement) =>
    Array.from(root.querySelectorAll('dl.summary dt')).find((dt) => dt.textContent?.trim() === en['detail.cancelBy']) as HTMLElement | undefined;

  it('shows the deadline as household wall time with the zone, next to the unsubscribe link', async () => {
    const { root } = await open({ ...detailFixture(), cancelBy: '2026-12-01T09:30', cancelByInstant: '2026-12-01T08:30:00Z' });
    const label = cancelByRow(root);
    expect(label).toBeDefined();
    const value = plain(label?.nextElementSibling?.textContent);
    expect(value).toContain('Europe/Copenhagen');
    expect(value).toMatch(/1 Dec 2026/);
    expect(value).toMatch(/9:30/);
    // Not shifted into the browser's own zone: 08:30Z would read 8:30 in UTC.
    expect(value).not.toMatch(/8:30/);
    const terms = Array.from(root.querySelectorAll('dl.summary dt')).map((dt) => dt.textContent?.trim());
    expect(terms.indexOf(en['detail.cancelBy'])).toBe(terms.indexOf(en['detail.unsubscribe']) + 1);
  });

  it('shows the deadline without a zone when the household cannot be loaded', async () => {
    const detail = { ...detailFixture(), cancelBy: '2026-12-01T09:30', cancelByInstant: '2026-12-01T08:30:00Z' };
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/subscriptions/${detail.id}`, SubscriptionDetailPage);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/household').flush({ traceId: 't' }, { status: 500, statusText: 'Server Error' });
    http.expectOne(`/api/subscriptions/${detail.id}${Q}`).flush(detail);
    await settle(harness);
    flushPeriods(http);
    await settle(harness);
    const value = plain(cancelByRow(harness.routeNativeElement as HTMLElement)?.nextElementSibling?.textContent);
    expect(value).toMatch(/9:30/);
    expect(value).not.toContain('Europe');
  });

  it('shows no deadline row when none is set', async () => {
    const { root } = await open();
    expect(cancelByRow(root)).toBeUndefined();
  });

  it('keeps the deadline when ending the subscription: the PUT echoes detail.cancelBy', async () => {
    const detail = { ...detailFixture(), cancelBy: '2026-12-01T09:30', cancelByInstant: '2026-12-01T08:30:00Z' };
    const { harness, root } = await open(detail);
    el<HTMLButtonElement>(root, '#end-open').click();
    await settle(harness);
    type(root, 'end-date', plusDays(detail.priceHistory[detail.priceHistory.length - 1].effectiveFrom, 10));
    await submitForm(harness, 'form:not(.entry-form)');
    const req = request(detail);
    expect((req.request.body as { cancelBy: unknown }).cancelBy).toBe('2026-12-01T09:30');
    req.flush({}, { status: 400, statusText: 'Bad Request' });
    await settle(harness);
  });

  it('shows the deadline row in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { root } = await open({ ...detailFixture(), cancelBy: '2026-12-01T09:30', cancelByInstant: '2026-12-01T08:30:00Z' });
    expect(root.textContent).toContain(da['detail.cancelBy']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  // ---- unsubscribe link ------------------------------------------------------------------------------

  const unsubscribeLink = (root: HTMLElement) => root.querySelector('a[target="_blank"]') as HTMLAnchorElement | null;

  it('shows the Unsubscribe link with safe attributes when a URL resolves', async () => {
    const { root, detail } = await open();
    const link = unsubscribeLink(root);
    expect(link?.getAttribute('href')).toBe(detail.effectiveUnsubscribeUrl);
    expect(link?.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link?.textContent?.trim()).toBe(en['detail.unsubscribeLink']);
    expect(plain(root.querySelector('.summary')?.textContent)).toContain(en['detail.unsubscribe']);
  });

  it('shows no link when no URL resolves', async () => {
    const { root } = await open({ ...detailFixture(), unsubscribeUrl: null, effectiveUnsubscribeUrl: null });
    expect(unsubscribeLink(root)).toBeNull();
    expect(plain(root.querySelector('.summary')?.textContent)).not.toContain(en['detail.unsubscribe']);
  });

  it('links the resolved catalog URL even when the member set none', async () => {
    const { root } = await open({ ...detailFixture(), unsubscribeUrl: null, effectiveUnsubscribeUrl: 'https://catalog.example/cancel' });
    expect(unsubscribeLink(root)?.getAttribute('href')).toBe('https://catalog.example/cancel');
  });

  it.each(['paused', 'ended'] as const)('shows the link for a %s subscription too', async (status) => {
    const { root } = await open({ ...detailFixture(), status, paused: status === 'paused' });
    expect(unsubscribeLink(root)).not.toBeNull();
  });

  it.each(['HTTPS://Example.com/cancel', 'Https://example.com/cancel'])('renders a link for the stored-as-typed value %s', async (url) => {
    const { root } = await open({ ...detailFixture(), effectiveUnsubscribeUrl: url });
    expect(unsubscribeLink(root)?.getAttribute('href')).toBe(url);
  });

  it.each(['http://example.com/cancel', 'javascript:alert(1)', 'data:text/html,x', '//example.com'])('renders no anchor for the non-https value %s', async (url) => {
    const { root } = await open({ ...detailFixture(), effectiveUnsubscribeUrl: url });
    expect(unsubscribeLink(root)).toBeNull();
    expect(root.innerHTML).not.toContain(url);
  });

  // ---- end, reinstate, remove ------------------------------------------------------------------------

  it('ends with the exact nine-key PUT body copied from the loaded detail (D-10)', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#end-open').click();
    await settle(harness);
    const endDate = plusDays(detail.priceHistory[detail.priceHistory.length - 1].effectiveFrom, 10);
    type(root, 'end-date', endDate);
    await submitForm(harness, 'form:not(.entry-form)');
    const req = request(detail);
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-Match')).toBe(`"${detail.version}"`);
    expect(req.request.body).toEqual({
      name: detail.name, category: 'Streaming', notes: 'Family plan', currency: detail.currency, startDate: detail.startDate, endDate, catalogId: null,
      unsubscribeUrl: 'https://example.com/cancel', cancelBy: null,
    });
    await respond(harness, req, bump(detail, { endDate, status: 'ended' }));
    expect(root.textContent).toContain(en['subscriptions.status.ended']);
  });

  it('reinstates with the same nine keys and endDate null', async () => {
    const detail = { ...detailFixture(), endDate: '2099-01-01', status: 'active' as const };
    const { harness, root } = await open(detail);
    buttonByText(root, en['detail.reinstate']).click();
    await settle(harness);
    const req = request(detail);
    expect(req.request.body).toEqual({
      name: detail.name, category: 'Streaming', notes: 'Family plan', currency: detail.currency, startDate: detail.startDate, endDate: null, catalogId: null,
      unsubscribeUrl: 'https://example.com/cancel', cancelBy: null,
    });
    await respond(harness, req, bump(detail, { endDate: null }));
    expect(buttonByText(root, en['detail.reinstate'])).toBeUndefined();
  });

  it('removes the subscription after confirmation, then goes home with a polite status message', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#remove-open').click();
    await settle(harness);
    expect(document.activeElement?.id).toBe('confirm-remove');
    expect(el(root, '#confirm-remove').textContent).toContain(detail.name);
    http.expectNone(`/api/subscriptions/${detail.id}${Q}`);
    buttonByText(root, en['detail.remove.yes']).click();
    const req = removeRequest(detail);
    expect(req.request.method).toBe('DELETE');
    expect(req.request.headers.get('If-Match')).toBe(`"${detail.version}"`);
    req.flush(null, { status: 204, statusText: 'No Content' });
    await settle(harness);
    expect(TestBed.inject(Router).url).toBe('/');
    http.expectOne(`/api/subscriptions${Q}`).flush({ ...overviewFixture(), subscriptions: [], totals: [] });
    flushPeriods(http);
    await settle(harness);
    expect(el(harness.routeNativeElement as HTMLElement, '[role=status]').textContent).toContain(`${detail.name} was removed.`);
    expect(TestBed.inject(SubscriptionsService).notice()).toBeNull();
  });

  // ---- failures --------------------------------------------------------------------------------------

  it('shows "not found" with a link home on a 404', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/subscriptions/${'b'.repeat(32)}`, SubscriptionDetailPage);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    http.expectOne(`/api/subscriptions/${'b'.repeat(32)}${Q}`).flush({ traceId: 't' }, { status: 404, statusText: 'Not Found' });
    await settle(harness);
    const root = harness.routeNativeElement as HTMLElement;
    expect(root.textContent).toContain(en['subscriptions.notFound']);
    expect(el(root, 'a[href="/"]')).toBeTruthy();
  });

  it('shows the localized message when access is revoked (403)', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#remove-open').click();
    await settle(harness);
    buttonByText(root, en['detail.remove.yes']).click();
    removeRequest(detail).flush({ traceId: 't' }, { status: 403, statusText: 'Forbidden' });
    await settle(harness);
    expect(root.textContent).toContain(en['subscriptions.form.forbidden']);
  });

  it('shows the conflict notice on a 412, keeps the input and reloads on request (BR-022)', async () => {
    const { harness, root, detail } = await open();
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-effectiveFrom', plusDays(detail.startDate, 5));
    type(root, 'entry-amount', '70.00');
    await entrySubmit(harness);
    request(detail, '/prices').flush({ traceId: 't', currentVersion: detail.version + 1 }, { status: 412, statusText: 'Precondition Failed' });
    await settle(harness);
    expect(root.textContent).toContain(en['subscriptions.form.conflict']);
    expect(el<HTMLInputElement>(root, '#entry-amount').value).toBe('70.00');
    buttonByText(root, en['subscriptions.form.reloadLatest']).click();
    request(detail).flush(bump(detail));
    await settle(harness);
    expect(root.textContent).not.toContain(en['subscriptions.form.conflict']);
  });

  // ---- pause and resume ------------------------------------------------------------------------------

  const pauseRequest = (detail: SubscriptionDetail, action: 'pause' | 'resume') =>
    http.expectOne({ method: 'POST', url: `/api/subscriptions/${detail.id}/${action}${Q}` });
  const toggle = (root: HTMLElement) => root.querySelector<HTMLButtonElement>('#pause-toggle');

  it('offers Pause on an active subscription and sends a body-less POST with the version in If-Match', async () => {
    const { harness, root, detail } = await open();
    expect(detail.status).toBe('active');
    expect(toggle(root)?.textContent?.trim()).toBe(en['subscriptions.pause']);
    toggle(root)!.click();
    const req = pauseRequest(detail, 'pause');
    expect(req.request.headers.get('If-Match')).toBe(`"${detail.version}"`);
    expect(req.request.body).toBeNull();
    await respond(harness, req, bump(detail, { status: 'paused', paused: true }));

    expect(el(root, '.summary .badge').textContent?.trim()).toBe(en['subscriptions.status.paused']);
    expect(el(root, '.summary .badge').classList.contains('status-paused')).toBe(true);
    expect(toggle(root)?.textContent?.trim()).toBe(en['subscriptions.resume']);
    expect(el(root, 'output.status').textContent).toContain(`${detail.name} is paused.`);
    expect(document.activeElement).toBe(toggle(root));
    http.expectNone(() => true);
  });

  it('resumes a paused subscription with the new version and offers Pause again', async () => {
    const paused = { ...detailFixture(), status: 'paused' as const, paused: true };
    const { harness, root, detail } = await open(paused);
    expect(el(root, '.summary .badge').textContent?.trim()).toBe(en['subscriptions.status.paused']);
    toggle(root)!.click();
    const req = pauseRequest(detail, 'resume');
    expect(req.request.headers.get('If-Match')).toBe(`"${detail.version}"`);
    await respond(harness, req, bump(detail, { status: 'active', paused: false }));
    expect(el(root, '.summary .badge').textContent?.trim()).toBe(en['subscriptions.status.active']);
    expect(toggle(root)?.textContent?.trim()).toBe(en['subscriptions.pause']);
    expect(el(root, 'output.status').textContent).toContain(`${detail.name} was resumed.`);
  });

  for (const status of ['ended', 'not-started'] as const) {
    it(`offers no Pause for a ${status} subscription`, async () => {
      const { root } = await open({ ...detailFixture(), status, paused: false });
      expect(toggle(root)).toBeNull();
    });

    it(`offers Resume for a ${status} subscription whose pause flag is set, so the flag cannot get stuck`, async () => {
      const { root } = await open({ ...detailFixture(), status, paused: true });
      expect(toggle(root)?.textContent?.trim()).toBe(en['subscriptions.resume']);
      expect(el(root, '.summary .badge').textContent?.trim()).toBe(en[`subscriptions.status.${status}`]);
    });
  }

  it('sends exactly one request for a double click and keeps the button disabled while it is in flight', async () => {
    const { harness, root, detail } = await open();
    const button = toggle(root)!;
    button.click();
    button.click();
    harness.detectChanges();
    expect(toggle(root)?.disabled).toBe(true);
    const req = pauseRequest(detail, 'pause');
    await respond(harness, req, bump(detail, { status: 'paused', paused: true }));
    expect(toggle(root)?.disabled).toBe(false);
  });

  it('shows the conflict banner on a 412 and reloads on request', async () => {
    const { harness, root, detail } = await open();
    toggle(root)!.click();
    pauseRequest(detail, 'pause').flush({ traceId: 't', currentVersion: detail.version + 1 }, { status: 412, statusText: 'Precondition Failed' });
    await settle(harness);
    expect(el(root, '.notice[role=alert]').textContent).toContain(en['subscriptions.form.conflict']);
    expect(root.querySelector('output.status')).toBeNull();
    buttonByText(root, en['subscriptions.form.reloadLatest']).click();
    request(detail).flush(bump(detail, { status: 'paused', paused: true }));
    await settle(harness);
    expect(root.textContent).not.toContain(en['subscriptions.form.conflict']);
    expect(toggle(root)?.textContent?.trim()).toBe(en['subscriptions.resume']);
  });

  it('shows the invalid-state message on a 409 and reloads the subscription without clearing the message', async () => {
    const { harness, root, detail } = await open();
    toggle(root)!.click();
    pauseRequest(detail, 'pause').flush({ traceId: 'trace-409', currentVersion: detail.version + 1 }, { status: 409, statusText: 'Conflict' });
    request(detail).flush(bump(detail, { status: 'paused', paused: true }));
    await settle(harness);
    expect(el(root, 'p.error[role=alert]').textContent).toContain(en['subscriptions.pauseInvalid']);
    expect(root.textContent).toContain('trace-409');
    expect(el(root, '.summary .badge').textContent?.trim()).toBe(en['subscriptions.status.paused']);
    expect(toggle(root)?.textContent?.trim()).toBe(en['subscriptions.resume']);
    expect(el(root, 'p.error[role=alert]').textContent).not.toContain(en['subscriptions.form.saveFailed']);
  });

  it('shows the generic failure with the trace id for any other error, never backend English', async () => {
    const { harness, root, detail } = await open();
    toggle(root)!.click();
    pauseRequest(detail, 'pause').flush({ traceId: 'trace-500', title: 'Backend English' }, { status: 500, statusText: 'Server Error' });
    await settle(harness);
    expect(el(root, 'p.error[role=alert]').textContent).toContain(en['subscriptions.form.saveFailed']);
    expect(root.textContent).toContain('trace-500');
    expect(root.textContent).not.toContain('Backend English');
    expect(toggle(root)?.textContent?.trim()).toBe(en['subscriptions.pause']);
    expect(el(root, '.summary .badge').textContent?.trim()).toBe(en['subscriptions.status.active']);
  });

  it('shows "not found" when the subscription was removed meanwhile (404)', async () => {
    const { harness, root, detail } = await open();
    toggle(root)!.click();
    pauseRequest(detail, 'pause').flush({ traceId: 't' }, { status: 404, statusText: 'Not Found' });
    await settle(harness);
    expect(root.textContent).toContain(en['subscriptions.notFound']);
  });

  it('renders a paused subscription in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { root } = await open({ ...detailFixture(), status: 'paused', paused: true });
    expect(el(root, '.summary .badge').textContent?.trim()).toBe(da['subscriptions.status.paused']);
    expect(toggle(root)?.textContent?.trim()).toBe(da['subscriptions.resume']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  // ---- language --------------------------------------------------------------------------------------

  it('renders in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const { root } = await open();
    expect(root.textContent).toContain(da['detail.historyHeading']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  it('re-renders amounts, dates, cadence labels and badges on a language switch with no request (EX-018)', async () => {
    const { harness, root, detail } = await open();
    const current = detail.priceHistory.find((e) => e.current)!;
    expect(plain(root.textContent)).toContain('DKK 920.38');
    TestBed.inject(LanguageService).setLanguage('da');
    await settle(harness);
    expect(plain(root.textContent)).toContain('920,38 DKK');
    expect(plain(root.textContent)).toContain(da['cadence.monthly']);
    expect(root.textContent).toContain(da['detail.badge.current']);
    expect(plain(rows(root)[detail.priceHistory.indexOf(current)].textContent)).toContain(da['detail.badge.current']);
    expect(englishLeftovers(root)).toEqual([]);
    http.expectNone(() => true);
  });

  it('keeps the amount valid and ungrouped across a language switch while the entry form is open (D-7)', async () => {
    const { harness, root, detail } = await open();
    expect(detail.priceHistory.length).toBeGreaterThan(1);
    el<HTMLButtonElement>(root, '#add-entry').click();
    await settle(harness);
    type(root, 'entry-amount', '1490.00');
    TestBed.inject(LanguageService).setLanguage('da');
    await settle(harness);
    expect(el<HTMLInputElement>(root, '#entry-amount').value).toBe('1490,00');
    expect(el(root, '#entry-amount').getAttribute('aria-invalid')).toBeNull();
  });
});


describe('SubscriptionDetailPage period', () => {
  let http: HttpTestingController;

  function setup(storage: Pick<Storage, 'getItem' | 'setItem'> | null = memoryStorage()) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(storage),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(SessionService).current.set({ state: 'member', user: { displayName: 'Synthetic', memberId: '1' }, expiresAt: '' });
  }
  afterEach(() => http.verify());

  const forPeriod = (key: string, total: string): SubscriptionDetail => {
    const detail = detailFixture();
    const dates: Record<string, [string, string]> = {
      'last-12-months': ['2025-11-18', '2026-11-17'],
      'next-12-months': ['2026-11-17', '2027-11-16'],
      'year-2025': ['2025-01-01', '2025-12-31'],
      'year-2026': ['2026-01-01', '2026-12-31'],
    };
    return { ...detail, periodTotal: total, period: { key, from: dates[key][0], to: dates[key][1], months: 12 } };
  };
  const url = (key: string) => `/api/subscriptions/${detailFixture().id}?period=${key}`;

  async function settle(harness: RouterTestingHarness) {
    harness.detectChanges();
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  /** Opens the page and answers the detail request for `requested` with `first`; the periods answer follows once the selector shows. */
  async function open(requested: string, first: SubscriptionDetail, years = periodOptionsFixture()) {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/subscriptions/${first.id}`, SubscriptionDetailPage);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    http.expectOne(url(requested)).flush(first);
    await settle(harness);
    flushPeriods(http, years);
    await settle(harness);
    return { harness, root: harness.routeNativeElement as HTMLElement };
  }

  const summaryTerms = (root: HTMLElement) => Array.from(root.querySelectorAll('.summary dt')).map((dt) => plain(dt.textContent));
  const summaryValueOf = (root: HTMLElement, term: string) => {
    const dt = Array.from(root.querySelectorAll('.summary dt')).find((candidate) => plain(candidate.textContent) === term);
    return plain(dt?.nextElementSibling?.textContent);
  };
  const periodSelect = (root: HTMLElement) => root.querySelector<HTMLSelectElement>('#period-select')!;

  async function choose(harness: RouterTestingHarness, root: HTMLElement, key: string) {
    const select = periodSelect(root);
    select.value = key;
    select.dispatchEvent(new Event('change'));
    await settle(harness);
  }

  it('shows the period total under the period heading and keeps the per-month figure', async () => {
    setup();
    const { root } = await open('year-2026', forPeriod('year-2026', '920.38'));
    expect(summaryTerms(root)).toContain('Total 2026');
    expect(summaryValueOf(root, 'Total 2026')).toBe('DKK 920.38');
    expect(summaryValueOf(root, en['subscriptions.col.perMonth'])).toBe('DKK 79.00');
    expect(summaryTerms(root)).not.toContain('Per year');
  });

  it('puts a labelled selector at the top of the summary, offering the years, then Last and Next 12 months', async () => {
    setup();
    const { root } = await open('year-2026', forPeriod('year-2026', '920.38'));
    const summary = root.querySelector('#summary-heading')!.parentElement!;
    expect(summary.querySelector('#period-select')).toBe(periodSelect(root));
    expect(summary.querySelector('h2')!.nextElementSibling!.contains(periodSelect(root))).toBe(true);
    expect(root.querySelector('label[for=period-select]')?.textContent?.trim()).toBe(en['subscriptions.period.label']);
    const options = Array.from(periodSelect(root).options).map((option) => [option.value, plain(option.textContent)]);
    expect(options).toEqual([
      ['year-2024', '2024'],
      ['year-2025', '2025'],
      ['year-2026', '2026'],
      ['year-2027', '2027'],
      ['last-12-months', en['subscriptions.period.last12']],
      ['next-12-months', en['subscriptions.period.next12']],
    ]);
    expect(periodSelect(root).value).toBe('year-2026');
  });

  it('reloads in place with ?period= when the selection changes, relabels the total and remembers the choice', async () => {
    const storage = memoryStorage();
    setup(storage);
    const { harness, root } = await open('year-2026', forPeriod('year-2026', '920.38'));
    const heading = root.querySelector('h1');
    await choose(harness, root, 'last-12-months');
    http.expectOne(url('last-12-months')).flush(forPeriod('last-12-months', '987.65'));
    await settle(harness);
    // In place: the page never went back to its skeleton, so the same heading element is still there.
    expect(root.querySelector('h1')).toBe(heading);
    expect(summaryValueOf(root, en['subscriptions.col.periodTotal.last12'])).toBe('DKK 987.65');
    expect(periodSelect(root).value).toBe('last-12-months');
    expect(storage.values.get(PERIOD_STORAGE_KEY)).toBe('last-12-months');
  });

  it('renders the selector and the period total in Danish with no English application copy', async () => {
    setup();
    TestBed.inject(LanguageService).setLanguage('da');
    const { root } = await open('year-2026', forPeriod('year-2026', '920.38'));
    expect(root.querySelector('label[for=period-select]')?.textContent?.trim()).toBe(da['subscriptions.period.label']);
    expect(summaryTerms(root)).toContain('I alt 2026');
    expect(Array.from(periodSelect(root).options).map((option) => plain(option.textContent))).toContain(da['subscriptions.period.last12']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  it('reads the selection shared with the overview and restores a stored one', async () => {
    setup(memoryStorage({ [PERIOD_STORAGE_KEY]: 'next-12-months' }));
    const { root } = await open('next-12-months', forPeriod('next-12-months', '1000.00'));
    expect(periodSelect(root).value).toBe('next-12-months');
    expect(summaryValueOf(root, en['subscriptions.col.periodTotal.next12'])).toBe('DKK 1,000.00');
  });

  it('falls back to the current year for a stored year that is not offered, without clearing the stored choice', async () => {
    const storage = memoryStorage({ [PERIOD_STORAGE_KEY]: 'year-2019' });
    setup(storage);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/subscriptions/${detailFixture().id}`, SubscriptionDetailPage);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    http.expectOne(url('year-2019')).flush({ ...forPeriod('year-2026', '0.00'), period: { key: 'year-2019', from: '2019-01-01', to: '2019-12-31', months: 12 } });
    await settle(harness);
    flushPeriods(http);
    await settle(harness);
    // The list does not contain 2019, so the page reloads for the current year.
    http.expectOne(url('year-2026')).flush(forPeriod('year-2026', '920.38'));
    await settle(harness);
    const root = harness.routeNativeElement as HTMLElement;
    expect(periodSelect(root).value).toBe('year-2026');
    expect(summaryValueOf(root, 'Total 2026')).toBe('DKK 920.38');
    expect(storage.values.get(PERIOD_STORAGE_KEY)).toBe('year-2019');
  });

  it('keeps working when the storage throws on every access', async () => {
    setup(throwingStorage);
    const { harness, root } = await open('year-2026', forPeriod('year-2026', '920.38'));
    await choose(harness, root, 'year-2025');
    http.expectOne(url('year-2025')).flush(forPeriod('year-2025', '700.00'));
    await settle(harness);
    expect(periodSelect(root).value).toBe('year-2025');
    expect(summaryValueOf(root, 'Total 2025')).toBe('DKK 700.00');
  });

  it('keeps offering the fallback years when the periods request fails', async () => {
    setup();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/subscriptions/${detailFixture().id}`, SubscriptionDetailPage);
    http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
    http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
    http.expectOne(url('year-2026')).flush(forPeriod('year-2026', '920.38'));
    await settle(harness);
    http.expectOne('/api/subscriptions/periods').flush({ traceId: 't' }, { status: 500, statusText: 'Server Error' });
    await settle(harness);
    const root = harness.routeNativeElement as HTMLElement;
    expect(Array.from(periodSelect(root).options).map((option) => option.value)).toEqual([
      'year-2026', 'year-2027', 'last-12-months', 'next-12-months',
    ]);
    expect(summaryValueOf(root, 'Total 2026')).toBe('DKK 920.38');
  });

  it('does not let an out-of-order response overwrite the data of the current selection', async () => {
    setup();
    const { harness, root } = await open('year-2026', forPeriod('year-2026', '920.38'));
    await choose(harness, root, 'last-12-months');
    const slow = http.expectOne(url('last-12-months'));
    await choose(harness, root, 'next-12-months');
    const fast = http.expectOne(url('next-12-months'));
    fast.flush(forPeriod('next-12-months', '1111.11'));
    await settle(harness);
    slow.flush(forPeriod('last-12-months', '2222.22'));
    await settle(harness);
    expect(periodSelect(root).value).toBe('next-12-months');
    expect(summaryValueOf(root, en['subscriptions.col.periodTotal.next12'])).toBe('DKK 1,111.11');
    expect(root.textContent).not.toContain('2,222.22');
  });

  it('sends the selected period with pause and shows the answer for it', async () => {
    setup(memoryStorage({ [PERIOD_STORAGE_KEY]: 'last-12-months' }));
    const first = forPeriod('last-12-months', '987.65');
    const { harness, root } = await open('last-12-months', first);
    root.querySelector<HTMLButtonElement>('#pause-toggle')!.click();
    const post = http.expectOne({ method: 'POST', url: `/api/subscriptions/${first.id}/pause?period=last-12-months` });
    post.flush({ ...first, version: first.version + 1, status: 'paused', paused: true, periodTotal: '900.00' });
    await settle(harness);
    expect(summaryValueOf(root, en['subscriptions.col.periodTotal.last12'])).toBe('DKK 900.00');
  });

  it('reloads the detail when a mutation answers for a period that is no longer selected', async () => {
    setup();
    const first = forPeriod('year-2026', '920.38');
    const { harness, root } = await open('year-2026', first);
    root.querySelector<HTMLButtonElement>('#pause-toggle')!.click();
    const post = http.expectOne({ method: 'POST', url: `/api/subscriptions/${first.id}/pause?period=year-2026` });
    await choose(harness, root, 'next-12-months');
    // The selection change already asked for the next 12 months.
    const reload = http.expectOne(url('next-12-months'));
    post.flush({ ...first, version: first.version + 1, status: 'paused', paused: true });
    await settle(harness);
    // The answer was for 2026, so it must not stand: the page asks again for the selection (a fresh version too) and ignores the stale request.
    const pending = http.match(url('next-12-months'));
    expect(pending).toHaveLength(1);
    pending[0].flush(forPeriod('next-12-months', '1111.11'));
    // The first reload answers last: its response belongs to a superseded request and is ignored.
    reload.flush(forPeriod('next-12-months', '9999.99'));
    await settle(harness);
    expect(summaryValueOf(root, en['subscriptions.col.periodTotal.next12'])).toBe('DKK 1,111.11');
    expect(root.textContent).not.toContain('9,999.99');
  });
});
