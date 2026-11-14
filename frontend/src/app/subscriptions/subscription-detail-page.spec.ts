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
import { SubscriptionDetail } from './subscription.models';
import { englishLeftovers } from './testing/english-leftovers';
import { detailFixture } from './testing/fixtures';
import '../app.config';

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

  async function open(detail: SubscriptionDetail = detailFixture()) {
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl(`/subscriptions/${detail.id}`, SubscriptionDetailPage);
    http.expectOne(`/api/subscriptions/${detail.id}`).flush(detail);
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
  const request = (detail: SubscriptionDetail, suffix = '') => http.expectOne(`/api/subscriptions/${detail.id}${suffix}`);

  async function respond(harness: RouterTestingHarness, req: TestRequest, body: SubscriptionDetail) {
    req.flush(body);
    await settle(harness);
  }

  const bump = (detail: SubscriptionDetail, changes: Partial<SubscriptionDetail> = {}): SubscriptionDetail =>
    ({ ...structuredClone(detail), ...changes, version: detail.version + 1 });

  // ---- display ---------------------------------------------------------------------------------------

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
    expect(first.request.body).toEqual({ effectiveFrom: plusDays(detail.startDate, 5), amount: '69.50', cadenceUnit: 'month', cadenceCount: 1 });
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
    expect(second.request.body).toEqual({ effectiveFrom: plusDays(detail.startDate, 6), amount: '999.00', cadenceUnit: 'year', cadenceCount: 1 });
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
    expect(req.request.body).toEqual({ effectiveFrom: detail.startDate, amount: '49.00', cadenceUnit: 'month', cadenceCount: 1 });
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
    expect(req.request.body).toEqual({ effectiveFrom: entry.effectiveFrom, amount: '65.00', cadenceUnit: 'month', cadenceCount: 1 });
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
    http.expectNone(`/api/subscriptions/${detail.id}/prices/${second.entryId}`);
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
    http.expectNone(`/api/subscriptions/${detail.id}/prices`);
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
    http.expectNone(`/api/subscriptions/${detail.id}`);
    expect(el(root, '#end-date-error').textContent).toContain(en['detail.end.error']);
  });

  // ---- end, reinstate, remove ------------------------------------------------------------------------

  it('ends with the exact six-key PUT body copied from the loaded detail (D-10)', async () => {
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
      name: detail.name, category: 'Streaming', notes: 'Family plan', currency: detail.currency, startDate: detail.startDate, endDate,
    });
    await respond(harness, req, bump(detail, { endDate, status: 'ended' }));
    expect(root.textContent).toContain(en['subscriptions.status.ended']);
  });

  it('reinstates with the same six keys and endDate null', async () => {
    const detail = { ...detailFixture(), endDate: '2099-01-01', status: 'active' as const };
    const { harness, root } = await open(detail);
    buttonByText(root, en['detail.reinstate']).click();
    await settle(harness);
    const req = request(detail);
    expect(req.request.body).toEqual({
      name: detail.name, category: 'Streaming', notes: 'Family plan', currency: detail.currency, startDate: detail.startDate, endDate: null,
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
    http.expectNone(`/api/subscriptions/${detail.id}`);
    buttonByText(root, en['detail.remove.yes']).click();
    const req = request(detail);
    expect(req.request.method).toBe('DELETE');
    expect(req.request.headers.get('If-Match')).toBe(`"${detail.version}"`);
    req.flush(null, { status: 204, statusText: 'No Content' });
    await settle(harness);
    expect(TestBed.inject(Router).url).toBe('/');
    http.expectOne('/api/subscriptions').flush({ asOf: '2026-11-13', subscriptions: [], totals: [] });
    await settle(harness);
    expect(el(harness.routeNativeElement as HTMLElement, '[role=status]').textContent).toContain(`${detail.name} was removed.`);
    expect(TestBed.inject(SubscriptionsService).notice()).toBeNull();
  });

  // ---- failures --------------------------------------------------------------------------------------

  it('shows "not found" with a link home on a 404', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/subscriptions/${'b'.repeat(32)}`, SubscriptionDetailPage);
    http.expectOne(`/api/subscriptions/${'b'.repeat(32)}`).flush({ traceId: 't' }, { status: 404, statusText: 'Not Found' });
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
    request(detail).flush({ traceId: 't' }, { status: 403, statusText: 'Forbidden' });
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
    expect(plain(root.textContent)).toContain('DKK 948.00');
    TestBed.inject(LanguageService).setLanguage('da');
    await settle(harness);
    expect(plain(root.textContent)).toContain('948,00 DKK');
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
