import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService } from '../i18n/i18n';
import { en } from '../i18n/catalog.en';
import { query, queryAll } from '../testing/query';
import { da } from '../i18n/catalog.da';
import { csrfInterceptor } from '../session';
import { SubscriptionOverviewComponent } from './subscription-overview';
import { SubscriptionOverview, SubscriptionSummary } from './subscription.models';
import { englishLeftovers } from './testing/english-leftovers';
import { overviewFixture, overviewWith } from './testing/fixtures';
import { PERIOD_STORAGE_KEY, flushPeriods, memoryStorage, periodOptionsFixture, periodTestProviders, throwingStorage } from './testing/period-providers';

const OVERVIEW_URL = '/api/subscriptions?period=year-2026';

const plain = (value: string | null | undefined) => (value ?? '').replace(/[\u00a0\u202f]/g, ' ').replace(/\s+/g, ' ').trim();

describe('SubscriptionOverviewComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  async function render(data: SubscriptionOverview = overviewFixture()) {
    const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
    fixture.detectChanges();
    http.expectOne(OVERVIEW_URL).flush(data);
    flushPeriods(http);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  const element = (fixture: { nativeElement: unknown }) => fixture.nativeElement as HTMLElement;
  const rowFor = (root: HTMLElement, name: string) =>
    queryAll(root, 'tbody tr').find((row) => query(row, 'th a').textContent?.trim() === name) as HTMLElement;
  /** The status column's badge; the "price change scheduled" badge is a separate `data-status="upcoming"` element. */
  const statusBadge = (row: HTMLElement) => query(row, '[data-status]:not([data-status="upcoming"])');

  it('links to the payment options page whatever the state, in the active language', async () => {
    const root = element(await render());
    expect(root.querySelector('a[href="/payment-options"]')?.textContent?.trim()).toBe(en['paymentOptions.link']);
    TestBed.inject(LanguageService).setLanguage('da');
    TestBed.createComponent(SubscriptionOverviewComponent).detectChanges();
    http.expectOne(OVERVIEW_URL).flush(overviewFixture());
    flushPeriods(http);
  });

  it('shows a loading status first', () => {
    const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
    fixture.detectChanges();
    expect(element(fixture).querySelector('output')?.textContent).toContain(en['subscriptions.loading']);
    http.expectOne(OVERVIEW_URL).flush(overviewFixture());
    flushPeriods(http);
  });

  it('renders the empty state with the entry point to add a subscription', async () => {
    const fixture = await render({ ...overviewFixture(), subscriptions: [], totals: [] });
    const root = element(fixture);
    expect(root.textContent).toContain(en['subscriptions.emptyHeading']);
    expect(root.querySelector('a[href="/subscriptions/new"]')).toBeTruthy();
    expect(root.querySelector('table')).toBeNull();
  });

  it('labels statuses and shows "price change scheduled" only where one is scheduled', async () => {
    const root = element(await render());
    const badge = (name: string) => statusBadge(rowFor(root, name)).textContent?.trim();
    expect(badge('StreamCo')).toBe(en['subscriptions.status.active']);
    expect(badge('KidsClub')).toBe(en['subscriptions.status.not-started']);
    expect(badge('SportsLive')).toBe(en['subscriptions.status.ended']);
    // EX-009: the StreamCo row has a scheduled change. EX-011: a not-started row with only its initial entry has none.
    expect(rowFor(root, 'StreamCo').textContent).toContain(en['subscriptions.priceChangeScheduled']);
    expect(rowFor(root, 'KidsClub').textContent).not.toContain(en['subscriptions.priceChangeScheduled']);
    expect(rowFor(root, 'SportsLive').textContent).not.toContain(en['subscriptions.priceChangeScheduled']);
  });

  it('shows one totals line per currency and never a combined figure (AC-003)', async () => {
    const data = overviewFixture();
    const root = element(await render(data));
    const lines = Array.from(queryAll(root, '[data-totals] li')).map((li) => plain(li.textContent));
    expect(lines.length).toBe(data.totals.length);
    expect(lines.length).toBeGreaterThan(1);
    expect(lines[0]).toContain('DKK');
    expect(lines[0]).toBe('DKK 3,862.32 · DKK 321.86 per month on average (4 active in the period)');
    expect(lines[1]).toBe('EUR 96.58 · EUR 8.05 per month on average (1 active in the period)');
    expect(lines[0]).not.toContain('per year');
    expect(lines.some((line) => line.includes('DKK') && line.includes('EUR'))).toBe(false);
  });

  it('says so when there are no active subscriptions to total', async () => {
    const data = overviewFixture();
    data.totals = [];
    expect(element(await render(data)).textContent).toContain(en['subscriptions.totalsNone']);
  });

  it('has a caption, column headers, row headers that link to the detail page, and a focusable scroll region', async () => {
    const root = element(await render());
    expect(root.querySelector('caption')?.textContent).toContain(en['subscriptions.tableCaption']);
    expect(root.querySelectorAll('thead th[scope=col]')).toHaveLength(7);
    const link = rowFor(root, 'StreamCo').querySelector('th[scope=row] a');
    expect(link?.getAttribute('href')).toMatch(/^\/subscriptions\/[0-9a-f]{32}$/);
    const region = query(root, '[data-table-scroll]');
    // A named <section> is a region landmark (implicit role), so no explicit role attribute.
    expect(region.tagName).toBe('SECTION');
    expect(region.getAttribute('tabindex')).toBe('0');
    expect(region.getAttribute('aria-label')).toBe(en['subscriptions.scrollRegion']);
    expect(region.classList.contains('overflow-x-auto')).toBe(true);
    expect(query(region, 'table').classList.contains('table')).toBe(true);
  });

  it('shows an error with the reference and retries', async () => {
    const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
    fixture.detectChanges();
    http.expectOne(OVERVIEW_URL).flush({ traceId: 'trace-123' }, { status: 503, statusText: 'Unavailable' });
    flushPeriods(http);
    await fixture.whenStable();
    fixture.detectChanges();
    const root = element(fixture);
    expect(root.querySelector('[role=alert]')?.textContent).toContain(en['subscriptions.loadFailed']);
    expect(root.textContent).toContain('trace-123');
    (Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim() === en['common.retry']) as HTMLButtonElement).click();
    http.expectOne(OVERVIEW_URL).flush(overviewFixture());
    await fixture.whenStable();
    fixture.detectChanges();
    expect(root.querySelector('table')).toBeTruthy();
  });

  it('keeps duplicate names in a stable order: name, then start date, then id (BR-002)', async () => {
    const data = overviewFixture();
    const template = data.subscriptions[0];
    const twin = (id: string, startDate: string) => ({ ...structuredClone(template), id, name: 'Twin', startDate });
    data.subscriptions = [twin('0000000000000000000000000000000b', '2025-01-01'), twin('0000000000000000000000000000000a', '2025-01-01'), twin('0000000000000000000000000000000c', '2024-01-01')];
    const links = Array.from(element(await render(data)).querySelectorAll('tbody th a')).map((a) => a.getAttribute('href'));
    expect(links).toEqual([
      '/subscriptions/0000000000000000000000000000000c',
      '/subscriptions/0000000000000000000000000000000a',
      '/subscriptions/0000000000000000000000000000000b',
    ]);
  });

  it('renders in Danish with no English application copy', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const root = element(await render());
    expect(root.textContent).toContain(da['subscriptions.tableCaption']);
    expect(query(root, '#subscriptions-heading').textContent?.trim()).toBe(da['subscriptions.heading']);
    expect(query(root, 'label[for=category-filter]').textContent?.trim()).toBe(da['subscriptions.filter.label']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  it('re-renders amounts, cadence labels and status badges on a language switch without a request (EX-018)', async () => {
    const data = overviewFixture();
    const fixture = await render(data);
    const root = element(fixture);
    const streamCo = data.subscriptions.find((s) => s.name === 'StreamCo')!;
    expect(plain(rowFor(root, 'StreamCo').textContent)).toContain('DKK 1,463.18');
    expect(plain(rowFor(root, 'StreamCo').textContent)).toContain(en['cadence.monthly']);
    expect(streamCo.periodTotal).toBe('1463.18');

    TestBed.inject(LanguageService).setLanguage('da');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(plain(rowFor(root, 'StreamCo').textContent)).toContain('1.463,18 DKK');
    expect(plain(rowFor(root, 'StreamCo').textContent)).toContain(da['cadence.monthly']);
    expect(statusBadge(rowFor(root, 'StreamCo')).textContent?.trim()).toBe(da['subscriptions.status.active']);
    expect(englishLeftovers(root)).toEqual([]);
    http.expectNone(() => true);
  });

  // ---- pause and resume ------------------------------------------------------------------------------

  describe('pause and resume', () => {
    /** StreamCo is active, KidsClub not-started, SportsLive ended, VideoTube paused; versions are distinct so the header is checkable. */
    function data(): SubscriptionOverview {
      const overview = overviewFixture();
      const by = (name: string) => overview.subscriptions.find((s) => s.name === name)!;
      by('StreamCo').version = 7;
      by('VideoTube').status = 'paused';
      by('VideoTube').paused = true;
      by('VideoTube').version = 5;
      return overview;
    }

    const actionFor = (root: HTMLElement, name: string) => rowFor(root, name).querySelector('td button') as HTMLButtonElement | null;
    const idOf = (overview: SubscriptionOverview, name: string) => overview.subscriptions.find((s) => s.name === name)!.id;

    it('offers Pause on active rows and Resume on paused rows only, with names that identify the row', async () => {
      const root = element(await render(data()));
      expect(actionFor(root, 'StreamCo')?.textContent?.trim()).toBe(en['subscriptions.pause']);
      expect(actionFor(root, 'StreamCo')?.getAttribute('aria-label')).toBe('Pause StreamCo');
      expect(actionFor(root, 'VideoTube')?.textContent?.trim()).toBe(en['subscriptions.resume']);
      expect(actionFor(root, 'VideoTube')?.getAttribute('aria-label')).toBe('Resume VideoTube');
      expect(statusBadge(rowFor(root, 'VideoTube')).getAttribute('data-status')).toBe('paused');
      expect(statusBadge(rowFor(root, 'VideoTube')).textContent?.trim()).toBe(en['subscriptions.status.paused']);
      expect(actionFor(root, 'KidsClub')).toBeNull();
      expect(actionFor(root, 'SportsLive')).toBeNull();
      expect(root.textContent).toContain(en['subscriptions.col.actions']);
    });

    it('keeps Resume reachable when the flag is set but the dates show ended or not-started', async () => {
      const overview = data();
      for (const [name, status] of [['KidsClub', 'not-started'], ['SportsLive', 'ended']] as const) {
        const row = overview.subscriptions.find((s) => s.name === name)!;
        row.paused = true;
        expect(row.status).toBe(status);
      }
      const root = element(await render(overview));
      expect(actionFor(root, 'KidsClub')?.textContent?.trim()).toBe(en['subscriptions.resume']);
      expect(actionFor(root, 'SportsLive')?.textContent?.trim()).toBe(en['subscriptions.resume']);
      expect(statusBadge(rowFor(root, 'SportsLive')).getAttribute('data-status')).toBe('ended');
    });

    it('posts the pause with the row version in If-Match and no body, reloads and says so politely', async () => {
      const overview = data();
      const fixture = await render(overview);
      const root = element(fixture);
      actionFor(root, 'StreamCo')!.click();
      const post = http.expectOne({ method: 'POST', url: `/api/subscriptions/${idOf(overview, 'StreamCo')}/pause?period=year-2026` });
      expect(post.request.headers.get('If-Match')).toBe('"7"');
      expect(post.request.body).toBeNull();

      const after = structuredClone(overview);
      const row = after.subscriptions.find((s) => s.name === 'StreamCo')!;
      row.status = 'paused';
      row.paused = true;
      row.version = 8;
      post.flush({});
      http.expectOne({ method: 'GET', url: OVERVIEW_URL }).flush(after);
      await fixture.whenStable();
      fixture.detectChanges();

      expect(statusBadge(rowFor(root, 'StreamCo')).getAttribute('data-status')).toBe('paused');
      expect(actionFor(root, 'StreamCo')?.textContent?.trim()).toBe(en['subscriptions.resume']);
      expect(query(root, '[data-action-notice] [role=status]').textContent).toContain('StreamCo is paused.');
      expect(root.querySelector('[role=alert]')).toBeNull();
    });

    it('posts the resume for a paused row and then offers Pause again', async () => {
      const overview = data();
      const fixture = await render(overview);
      const root = element(fixture);
      actionFor(root, 'VideoTube')!.click();
      const post = http.expectOne({ method: 'POST', url: `/api/subscriptions/${idOf(overview, 'VideoTube')}/resume?period=year-2026` });
      expect(post.request.headers.get('If-Match')).toBe('"5"');
      expect(post.request.body).toBeNull();

      const after = structuredClone(overview);
      const row = after.subscriptions.find((s) => s.name === 'VideoTube')!;
      row.status = 'active';
      row.paused = false;
      row.version = 6;
      post.flush({});
      http.expectOne({ method: 'GET', url: OVERVIEW_URL }).flush(after);
      await fixture.whenStable();
      fixture.detectChanges();

      expect(actionFor(root, 'VideoTube')?.textContent?.trim()).toBe(en['subscriptions.pause']);
      expect(query(root, '[data-action-notice] [role=status]').textContent).toContain('VideoTube was resumed.');
    });

    it('sends exactly one request for a double click and disables every action button while it is in flight', async () => {
      const overview = data();
      const fixture = await render(overview);
      const root = element(fixture);
      const button = actionFor(root, 'StreamCo')!;
      button.click();
      button.click();
      actionFor(root, 'VideoTube')!.click();
      fixture.detectChanges();
      const post = http.expectOne((r) => r.method === 'POST');
      expect(Array.from(root.querySelectorAll<HTMLButtonElement>('td button')).every((b) => b.disabled)).toBe(true);
      post.flush({});
      http.expectOne({ method: 'GET', url: OVERVIEW_URL }).flush(overview);
      await fixture.whenStable();
      fixture.detectChanges();
      expect(Array.from(root.querySelectorAll<HTMLButtonElement>('td button')).every((b) => !b.disabled)).toBe(true);
    });

    it('shows the conflict message on a 412 and reloads the overview for the new version', async () => {
      const overview = data();
      const fixture = await render(overview);
      const root = element(fixture);
      actionFor(root, 'StreamCo')!.click();
      http.expectOne((r) => r.method === 'POST').flush({ traceId: 'trace-412', currentVersion: 9 }, { status: 412, statusText: 'Precondition Failed' });
      const reloaded = structuredClone(overview);
      reloaded.subscriptions.find((s) => s.name === 'StreamCo')!.version = 9;
      http.expectOne({ method: 'GET', url: OVERVIEW_URL }).flush(reloaded);
      await fixture.whenStable();
      fixture.detectChanges();

      expect(root.querySelector('[role=alert]')?.textContent).toContain(en['subscriptions.form.conflict']);
      expect(root.querySelector('[data-action-notice]')).toBeNull();

      actionFor(root, 'StreamCo')!.click();
      const retry = http.expectOne((r) => r.method === 'POST');
      expect(retry.request.headers.get('If-Match')).toBe('"9"');
      retry.flush({});
      http.expectOne({ method: 'GET', url: OVERVIEW_URL }).flush(reloaded);
      await fixture.whenStable();
      fixture.detectChanges();
      expect(root.querySelector('[role=alert]')).toBeNull();
    });

    it('shows the invalid-state message on a 409 and reloads the overview', async () => {
      const overview = data();
      const fixture = await render(overview);
      const root = element(fixture);
      actionFor(root, 'VideoTube')!.click();
      http.expectOne((r) => r.method === 'POST').flush({ traceId: 't', currentVersion: 5 }, { status: 409, statusText: 'Conflict' });
      http.expectOne({ method: 'GET', url: OVERVIEW_URL }).flush(overview);
      await fixture.whenStable();
      fixture.detectChanges();
      expect(root.querySelector('[role=alert]')?.textContent).toContain(en['subscriptions.pauseInvalid']);
    });

    it('shows the generic failure with the trace id for any other error and never backend English', async () => {
      const overview = data();
      const fixture = await render(overview);
      const root = element(fixture);
      actionFor(root, 'StreamCo')!.click();
      http.expectOne((r) => r.method === 'POST').flush({ traceId: 'trace-500', title: 'Backend English' }, { status: 500, statusText: 'Server Error' });
      http.expectOne({ method: 'GET', url: OVERVIEW_URL }).flush(overview);
      await fixture.whenStable();
      fixture.detectChanges();
      const alert = root.querySelector('[role=alert]')?.textContent ?? '';
      expect(alert).toContain(en['subscriptions.form.saveFailed']);
      expect(alert).toContain('trace-500');
      expect(alert).not.toContain('Backend English');
    });

    it('renders the paused row and the action labels in Danish with no English application copy', async () => {
      TestBed.inject(LanguageService).setLanguage('da');
      const root = element(await render(data()));
      expect(statusBadge(rowFor(root, 'VideoTube')).textContent?.trim()).toBe(da['subscriptions.status.paused']);
      expect(actionFor(root, 'VideoTube')?.textContent?.trim()).toBe(da['subscriptions.resume']);
      expect(actionFor(root, 'VideoTube')?.getAttribute('aria-label')).toBe('Genoptag VideoTube');
      expect(actionFor(root, 'StreamCo')?.textContent?.trim()).toBe(da['subscriptions.pause']);
      expect(englishLeftovers(root)).toEqual([]);
    });
  });

  it('shows a logo or the initial letter beside each name without changing the row name', async () => {
    const data = overviewFixture();
    data.subscriptions[0] = { ...data.subscriptions[0], catalogId: 'streamco', logoUrl: '/logos/streamco.svg' };
    const fixture = await render(data);
    const root = element(fixture);
    const first = rowFor(root, data.subscriptions[0].name);
    const img = first.querySelector('th img');
    expect(img?.getAttribute('src')).toBe('/logos/streamco.svg');
    expect(img?.getAttribute('alt')).toBe('');
    const other = rowFor(root, data.subscriptions[1].name);
    expect(other.querySelector('th img')).toBeNull();
    expect(other.querySelector('th span[aria-hidden="true"]')?.textContent?.trim()).toBe(Array.from(data.subscriptions[1].name)[0].toUpperCase());
    // The link is still the accessible name: the logo adds no text of its own.
    expect(other.querySelector('th a')?.textContent?.trim()).toBe(data.subscriptions[1].name);
  });

  it('falls back to the letter when a logo fails to load', async () => {
    const data = overviewFixture();
    data.subscriptions[0] = { ...data.subscriptions[0], catalogId: 'streamco', logoUrl: '/logos/broken.svg' };
    const fixture = await render(data);
    const root = element(fixture);
    const first = rowFor(root, data.subscriptions[0].name);
    first.querySelector('th img')?.dispatchEvent(new Event('error'));
    fixture.detectChanges();
    expect(first.querySelector('th img')).toBeNull();
    expect(first.querySelector('th span[aria-hidden="true"]')).not.toBeNull();
  });
});

describe('SubscriptionOverviewComponent categories', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  const rows = (...patches: Partial<SubscriptionSummary>[]) => overviewWith(patches);

  async function render(data: SubscriptionOverview) {
    const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
    fixture.detectChanges();
    http.expectOne(OVERVIEW_URL).flush(data);
    flushPeriods(http);
    await settle(fixture);
    return fixture;
  }
  async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const root = (fixture: { nativeElement: unknown }) => fixture.nativeElement as HTMLElement;
  const select = (el: HTMLElement) => el.querySelector('#category-filter') as HTMLSelectElement;
  const toggle = (el: HTMLElement) => el.querySelector('#group-toggle') as HTMLInputElement;
  const names = (el: HTMLElement) => Array.from(el.querySelectorAll('tbody th a')).map((a) => a.textContent?.trim());
  const status = (el: HTMLElement) => plain(el.querySelector('output')?.textContent);
  const optionLabels = (el: HTMLElement) => Array.from(select(el).options).map((o) => o.textContent?.trim());
  const choose = async (fixture: Parameters<typeof settle>[0] & { nativeElement: unknown }, value: string) => {
    const control = select(root(fixture));
    control.value = value;
    control.dispatchEvent(new Event('change'));
    await settle(fixture);
  };
  const group = async (fixture: Parameters<typeof settle>[0] & { nativeElement: unknown }, on = true) => {
    const box = toggle(root(fixture));
    box.checked = on;
    box.dispatchEvent(new Event('change'));
    await settle(fixture);
  };
  const headings = (el: HTMLElement) => Array.from(el.querySelectorAll('[data-category-group] h3')).map((h) => h.textContent?.trim());

  const data = () =>
    rows(
      { name: 'A', category: 'Food' },
      { name: 'B', category: ' food ' },
      { name: 'C', category: 'Internet' },
      { name: 'D', category: null },
      { name: 'E', category: '   ' },
    );

  it('filters by category, treats case and whitespace variants as one, and All restores everything (AC1, AC3)', async () => {
    const fixture = await render(data());
    const el = root(fixture);
    expect(names(el)).toEqual(['A', 'B', 'C', 'D', 'E']);
    expect(optionLabels(el)).toEqual([en['subscriptions.filter.all'], 'Food', 'Internet', en['subscriptions.filter.uncategorized']]);
    await choose(fixture, 'cat:food');
    expect(names(el)).toEqual(['A', 'B']);
    expect(status(el)).toBe('2 shown');
    await choose(fixture, 'all');
    expect(names(el)).toEqual(['A', 'B', 'C', 'D', 'E']);
    expect(status(el)).toBe('5 shown');
  });

  it('offers Uncategorized only when an uncategorized subscription exists (AC2)', async () => {
    const withNone = await render(rows({ name: 'A', category: 'Food' }));
    expect(optionLabels(root(withNone))).toEqual([en['subscriptions.filter.all'], 'Food']);
    const fixture = await render(data());
    await choose(fixture, 'uncategorized');
    expect(names(root(fixture))).toEqual(['D', 'E']);
  });

  it('keeps categories literally named All and Uncategorized apart from the built-in choices, in Danish too', async () => {
    TestBed.inject(LanguageService).setLanguage('da');
    const fixture = await render(rows({ name: 'A', category: 'All' }, { name: 'B', category: 'Uncategorized' }, { name: 'C', category: null }));
    const el = root(fixture);
    expect(optionLabels(el)).toEqual([da['subscriptions.filter.all'], 'All', 'Uncategorized', da['subscriptions.filter.uncategorized']]);
    await choose(fixture, 'cat:all');
    expect(names(el)).toEqual(['A']);
    await choose(fixture, 'cat:uncategorized');
    expect(names(el)).toEqual(['B']);
    await choose(fixture, 'uncategorized');
    expect(names(el)).toEqual(['C']);
  });

  it('shows All when the selected category disappears and resumes it if it comes back (R3.3)', async () => {
    const fixture = await render(data());
    const el = root(fixture);
    await choose(fixture, 'cat:internet');
    expect(names(el)).toEqual(['C']);

    fixture.componentInstance.overview.set(rows({ name: 'A', category: 'Food' }, { name: 'D', category: null }));
    await settle(fixture);
    expect(names(el)).toEqual(['A', 'D']);
    expect(select(el).value).toBe('all');
    expect(status(el)).toBe('2 shown');

    fixture.componentInstance.overview.set(data());
    await settle(fixture);
    expect(select(el).value).toBe('cat:internet');
    expect(names(el)).toEqual(['C']);
  });

  it('falls back to All for a stored selection that names nothing (R3.4 guard is unreachable through the UI)', async () => {
    const fixture = await render(data());
    const el = root(fixture);
    fixture.componentInstance.categoryFilter.set({ kind: 'category', key: 'nope' });
    await settle(fixture);
    expect(names(el)).toHaveLength(5);
    expect(select(el).value).toBe('all');
    expect(status(el)).toBe('5 shown');
    expect(el.textContent).not.toContain(en['subscriptions.filter.emptyBody']);
    fixture.componentInstance.categoryFilter.set({ kind: 'uncategorized' });
    fixture.componentInstance.overview.set(rows({ name: 'A', category: 'Food' }));
    await settle(fixture);
    expect(select(el).value).toBe('all');
  });

  it('shows no toolbar and keeps the empty state when there are no subscriptions (AC8)', async () => {
    const el = root(await render({ ...overviewFixture(), subscriptions: [], totals: [] }));
    expect(el.textContent).toContain(en['subscriptions.emptyHeading']);
    expect(select(el)).toBeNull();
    expect(el.textContent).not.toContain(en['subscriptions.filter.emptyBody']);
  });

  it('groups alphabetically with Uncategorized last, counts every status and totals the rows active in the period per currency (AC4, AC5)', async () => {
    const fixture = await render(
      rows(
        { name: 'A', category: 'Media', currency: 'DKK', periodTotal: '948.00' },
        { name: 'B', category: 'media', currency: 'DKK', periodTotal: '126.00' },
        { name: 'C', category: 'Media', currency: 'EUR', periodTotal: '60.00' },
        { name: 'D', category: 'Media', currency: 'DKK', status: 'ended', periodTotal: '0.00', activeInPeriod: false },
        { name: 'E', category: 'Bills', status: 'not-started', activeInPeriod: false },
        { name: 'F', category: null },
      ),
    );
    const el = root(fixture);
    await group(fixture);
    expect(headings(el)).toEqual(['Bills', 'Media', en['subscriptions.filter.uncategorized']]);
    const sections = Array.from(el.querySelectorAll('[data-category-group]'));
    expect(plain(sections[1].querySelector('p')?.textContent)).toBe('Subscriptions: 4');
    const lines = Array.from(sections[1].querySelectorAll('[data-totals] li')).map((li) => plain(li.textContent));
    expect(lines).toHaveLength(2);
    expect(lines[0]).toContain('DKK 1,074.00 · DKK 89.50 per month on average');
    expect(lines[0]).toContain('2 active in the period');
    expect(lines[1]).toContain('EUR 60.00 · EUR 5.00 per month on average');
    expect(lines.some((line) => line.includes('DKK') && line.includes('EUR'))).toBe(false);
    // No row of Bills is active in the period: rows and count, but no totals line.
    expect(sections[0].querySelector('[data-totals]')).toBeNull();
    expect(sections[0].querySelectorAll('tbody tr')).toHaveLength(1);
    await group(fixture, false);
    expect(el.querySelector('[data-category-group]')).toBeNull();
    expect(el.querySelectorAll('tbody tr')).toHaveLength(6);
  });

  it('combines filter and grouping and reorders groups in Danish (AC5)', async () => {
    const fixture = await render(
      rows({ name: 'A', category: 'Zebra' }, { name: 'B', category: 'Åben' }, { name: 'C', category: 'Æble' }, { name: 'D', category: 'Øl' }, { name: 'E', category: null }),
    );
    const el = root(fixture);
    await group(fixture);
    expect(headings(el).slice(0, 4)).toEqual(['Åben', 'Æble', 'Øl', 'Zebra']);
    TestBed.inject(LanguageService).setLanguage('da');
    await settle(fixture);
    expect(headings(el)).toEqual(['Zebra', 'Æble', 'Øl', 'Åben', da['subscriptions.filter.uncategorized']]);
    await choose(fixture, 'cat:zebra');
    expect(headings(el)).toEqual(['Zebra']);
    expect(status(el)).toBe('1 vist');
    expect(englishLeftovers(el)).toEqual([]);
  });

  it('omits a group totals line for malformed amounts but still renders the rows', async () => {
    const fixture = await render(rows({ name: 'A', category: 'Bad', periodTotal: '1,50' }, { name: 'B', category: 'Good' }));
    const el = root(fixture);
    await group(fixture);
    const [bad, good] = Array.from(el.querySelectorAll('[data-category-group]'));
    expect(bad.querySelector('[data-totals]')).toBeNull();
    expect(bad.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(good.querySelector('[data-totals]')).toBeTruthy();
  });

  it('labels the controls and exposes group headings semantically (R5.1)', async () => {
    const fixture = await render(data());
    const el = root(fixture);
    expect(el.querySelector('label[for=category-filter]')?.textContent?.trim()).toBe(en['subscriptions.filter.label']);
    expect(el.querySelector('label[for=group-toggle]')?.textContent?.trim()).toBe(en['subscriptions.group.toggle']);
    expect(toggle(el).type).toBe('checkbox');
    expect(el.querySelectorAll('[data-table-scroll]')).toHaveLength(1);
    await group(fixture);
    const regions = el.querySelectorAll('[data-table-scroll]');
    expect(regions).toHaveLength(1);
    expect(regions[0].getAttribute('tabindex')).toBe('0');
    expect(regions[0].getAttribute('aria-label')).toBe(en['subscriptions.scrollRegion']);
    const sections = Array.from(el.querySelectorAll('[data-category-group]'));
    expect(sections.length).toBeGreaterThan(1);
    const ids = new Set<string>();
    for (const section of sections) {
      const id = section.getAttribute('aria-labelledby') as string;
      ids.add(id);
      expect(section.querySelector(`h3[id=${id}]`)).toBeTruthy();
      expect(section.querySelector('table')?.getAttribute('aria-labelledby')).toBe(id);
      expect(section.querySelector('caption')).toBeNull();
    }
    expect(ids.size).toBe(sections.length);
  });

  it('keeps focus on the filter and the toggle when they change (R5.1)', async () => {
    const fixture = await render(data());
    document.body.appendChild(root(fixture));
    try {
      const el = root(fixture);
      select(el).focus();
      await choose(fixture, 'cat:food');
      expect(document.activeElement).toBe(select(el));
      toggle(el).focus();
      await group(fixture);
      expect(document.activeElement).toBe(toggle(el));
    } finally {
      root(fixture).remove();
    }
  });

  it('has the same columns and row markup flat and grouped', async () => {
    const fixture = await render(data());
    const el = root(fixture);
    const flat = Array.from(el.querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    await group(fixture);
    for (const head of Array.from(el.querySelectorAll('thead'))) {
      expect(Array.from(head.querySelectorAll('th')).map((th) => th.textContent?.trim())).toEqual(flat);
    }
  });
});

describe('SubscriptionOverviewComponent period', () => {
  let http: HttpTestingController;

  function setup(storage: Pick<Storage, 'getItem' | 'setItem'> | null = memoryStorage()) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
        ...periodTestProviders(storage),
        { provide: BROWSER_LANGUAGES, useValue: ['en'] },
        { provide: LANGUAGE_STORAGE, useValue: { getItem: () => null, setItem: vi.fn() } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  }
  afterEach(() => http.verify());

  const url = (key: string) => `/api/subscriptions?period=${key}`;
  const periodData = (key: string, factor: string): SubscriptionOverview => {
    const data = overviewFixture();
    data.period = { ...data.period, key };
    for (const row of data.subscriptions) row.periodTotal = factor === '' ? row.periodTotal : `${factor}${row.periodTotal}`;
    return data;
  };

  async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function render(requested = 'year-2026', data: SubscriptionOverview = overviewFixture()) {
    const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
    fixture.detectChanges();
    http.expectOne(url(requested)).flush(data);
    flushPeriods(http);
    await settle(fixture);
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  const select = (root: HTMLElement) => root.querySelector<HTMLSelectElement>('#period-select')!;
  const headers = (root: HTMLElement) => Array.from(root.querySelectorAll('thead th')).map((th) => plain(th.textContent));
  const stream = (root: HTMLElement) => plain(Array.from(root.querySelectorAll('tbody tr')).find((r) => r.querySelector('th a')?.textContent?.trim() === 'StreamCo')?.textContent);

  async function choose(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }, root: HTMLElement, key: string) {
    select(root).value = key;
    select(root).dispatchEvent(new Event('change'));
    await settle(fixture);
  }

  it('puts a labelled selector above the table, with the years from the periods response, then Last and Next 12 months', async () => {
    setup();
    const { root } = await render();
    expect(root.querySelector('label[for=period-select]')?.textContent?.trim()).toBe(en['subscriptions.period.label']);
    const table = root.querySelector('table')!;
    expect(select(root).compareDocumentPosition(table) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(Array.from(select(root).options).map((option) => [option.value, plain(option.textContent)])).toEqual([
      ['year-2024', '2024'],
      ['year-2025', '2025'],
      ['year-2026', '2026'],
      ['year-2027', '2027'],
      ['last-12-months', en['subscriptions.period.last12']],
      ['next-12-months', en['subscriptions.period.next12']],
    ]);
    expect(select(root).value).toBe('year-2026');
  });

  it('loads the periods once, not again when the selection changes', async () => {
    setup();
    const { fixture, root } = await render();
    await choose(fixture, root, 'next-12-months');
    http.expectOne(url('next-12-months')).flush(periodData('next-12-months', ''));
    await settle(fixture);
    expect(http.match('/api/subscriptions/periods')).toHaveLength(0);
  });

  it('names the period in the column header, for each kind', async () => {
    setup();
    const { fixture, root } = await render();
    expect(headers(root)).toContain('Total 2026');
    expect(headers(root)).not.toContain('Per year');
    expect(headers(root)).toContain(en['subscriptions.col.perMonth']);
    await choose(fixture, root, 'last-12-months');
    http.expectOne(url('last-12-months')).flush(periodData('last-12-months', ''));
    await settle(fixture);
    expect(headers(root)).toContain(en['subscriptions.col.periodTotal.last12']);
    await choose(fixture, root, 'next-12-months');
    http.expectOne(url('next-12-months')).flush(periodData('next-12-months', ''));
    await settle(fixture);
    expect(headers(root)).toContain(en['subscriptions.col.periodTotal.next12']);
    await choose(fixture, root, 'year-2025');
    http.expectOne(url('year-2025')).flush(periodData('year-2025', ''));
    await settle(fixture);
    expect(headers(root)).toContain('Total 2025');
  });

  it('shows the period total in the total cell and keeps the per-month cell on the normalized monthly price', async () => {
    setup();
    const { root } = await render();
    // StreamCo: periodTotal 1463.18, normalizedMonthly 129.00 (the current price per month, unrelated to the period).
    const cells = Array.from(root.querySelectorAll('tbody tr')).find((r) => r.querySelector('th a')?.textContent?.trim() === 'StreamCo')!.querySelectorAll('td');
    expect(plain(cells[3].textContent)).toBe('DKK 129.00');
    expect(plain(cells[4].textContent)).toBe('DKK 1,463.18');
    expect(root.textContent).not.toContain('1,548.00');
  });

  it('reloads in place when the selection changes: a new ?period= request, the table never replaced by a skeleton, the choice remembered', async () => {
    const storage = memoryStorage();
    setup(storage);
    const { fixture, root } = await render();
    const table = root.querySelector('table');
    await choose(fixture, root, 'last-12-months');
    expect(root.querySelector('app-skeleton-block')).toBeNull();
    expect(root.querySelector('table')).toBe(table);
    const data = periodData('last-12-months', '1');
    http.expectOne(url('last-12-months')).flush(data);
    await settle(fixture);
    expect(root.querySelector('table')).toBe(table);
    expect(stream(root)).toContain('DKK 11,463.18');
    expect(select(root).value).toBe('last-12-months');
    expect(storage.values.get(PERIOD_STORAGE_KEY)).toBe('last-12-months');
  });

  it('shows the totals panel as the period total and the average per month, per currency', async () => {
    setup();
    const { fixture, root } = await render();
    const lines = () => Array.from(root.querySelectorAll('[data-totals-panel] [data-totals] li')).map((li) => plain(li.textContent));
    expect(lines()[0]).toBe('DKK 3,862.32 · DKK 321.86 per month on average (4 active in the period)');
    const next = overviewFixture();
    next.period.key = 'next-12-months';
    next.totals = [{ currency: 'DKK', minorUnit: 2, total: '1000.00', monthlyAverage: '83.33', activeCount: 2 }];
    await choose(fixture, root, 'next-12-months');
    http.expectOne(url('next-12-months')).flush(next);
    await settle(fixture);
    expect(lines()).toEqual(['DKK 1,000.00 · DKK 83.33 per month on average (2 active in the period)']);
  });

  it('does not let an out-of-order response overwrite the data of the current selection', async () => {
    setup();
    const { fixture, root } = await render();
    await choose(fixture, root, 'last-12-months');
    const slow = http.expectOne(url('last-12-months'));
    await choose(fixture, root, 'next-12-months');
    const fast = http.expectOne(url('next-12-months'));
    fast.flush(periodData('next-12-months', '2'));
    await settle(fixture);
    slow.flush(periodData('last-12-months', '3'));
    await settle(fixture);
    expect(select(root).value).toBe('next-12-months');
    expect(headers(root)).toContain(en['subscriptions.col.periodTotal.next12']);
    expect(stream(root)).toContain('DKK 21,463.18');
    expect(stream(root)).not.toContain('31,463.18');
  });

  it('shows a failed reload with its reference and retries for the current selection', async () => {
    setup();
    const { fixture, root } = await render();
    await choose(fixture, root, 'next-12-months');
    http.expectOne(url('next-12-months')).flush({ traceId: 'trace-period' }, { status: 503, statusText: 'Unavailable' });
    await settle(fixture);
    expect(root.querySelector('[role=alert]')?.textContent).toContain(en['subscriptions.loadFailed']);
    expect(root.textContent).toContain('trace-period');
    // The selector stays, so the member can pick another period instead of retrying.
    expect(select(root).value).toBe('next-12-months');
    (Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim() === en['common.retry']) as HTMLButtonElement).click();
    http.expectOne(url('next-12-months')).flush(periodData('next-12-months', ''));
    await settle(fixture);
    expect(root.querySelector('table')).toBeTruthy();
    expect(root.querySelector('[role=alert]')).toBeNull();
  });

  it('restores a stored selection and requests it first', async () => {
    setup(memoryStorage({ [PERIOD_STORAGE_KEY]: 'last-12-months' }));
    const { root } = await render('last-12-months', periodData('last-12-months', ''));
    expect(select(root).value).toBe('last-12-months');
    expect(headers(root)).toContain(en['subscriptions.col.periodTotal.last12']);
  });

  it('falls back to the current year when the stored year is not offered, and keeps the stored choice', async () => {
    const storage = memoryStorage({ [PERIOD_STORAGE_KEY]: 'year-2019' });
    setup(storage);
    const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
    fixture.detectChanges();
    http.expectOne(url('year-2019')).flush(periodData('year-2019', ''));
    flushPeriods(http);
    await settle(fixture);
    http.expectOne(url('year-2026')).flush(overviewFixture());
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(select(root).value).toBe('year-2026');
    expect(headers(root)).toContain('Total 2026');
    expect(storage.values.get(PERIOD_STORAGE_KEY)).toBe('year-2019');
  });

  it('keeps working, and keeps offering the fallback years, when the periods request fails', async () => {
    setup();
    const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
    fixture.detectChanges();
    http.expectOne(url('year-2026')).flush(overviewFixture());
    http.expectOne('/api/subscriptions/periods').flush({ traceId: 't' }, { status: 500, statusText: 'Server Error' });
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(Array.from(select(root).options).map((option) => option.value)).toEqual(['year-2026', 'year-2027', 'last-12-months', 'next-12-months']);
    expect(root.querySelector('table')).toBeTruthy();
  });

  it('keeps working when the storage throws on every access', async () => {
    setup(throwingStorage);
    const { fixture, root } = await render();
    await choose(fixture, root, 'next-12-months');
    http.expectOne(url('next-12-months')).flush(periodData('next-12-months', ''));
    await settle(fixture);
    expect(select(root).value).toBe('next-12-months');
    expect(root.querySelector('table')).toBeTruthy();
  });

  it('sends the selected period with pause and reloads the overview for it', async () => {
    setup(memoryStorage({ [PERIOD_STORAGE_KEY]: 'next-12-months' }));
    const data = periodData('next-12-months', '');
    const { fixture, root } = await render('next-12-months', data);
    const row = data.subscriptions.find((s) => s.name === 'StreamCo')!;
    root.querySelector<HTMLButtonElement>(`#action-${row.id}`)!.click();
    http.expectOne({ method: 'POST', url: `/api/subscriptions/${row.id}/pause?period=next-12-months` }).flush({});
    http.expectOne({ method: 'GET', url: url('next-12-months') }).flush(data);
    await settle(fixture);
    expect(root.querySelector('table')).toBeTruthy();
  });

  it('renders the selector, the headings and the totals line in Danish with no English application copy', async () => {
    setup();
    TestBed.inject(LanguageService).setLanguage('da');
    const { root } = await render();
    expect(root.querySelector('label[for=period-select]')?.textContent?.trim()).toBe(da['subscriptions.period.label']);
    expect(headers(root)).toContain('I alt 2026');
    expect(plain(root.querySelector('[data-totals-panel] [data-totals] li')?.textContent)).toBe('3.862,32 DKK · 321,86 DKK pr. måned i gennemsnit (4 aktive i perioden)');
    expect(Array.from(select(root).options).map((o) => plain(o.textContent))).toContain(da['subscriptions.period.next12']);
    expect(englishLeftovers(root)).toEqual([]);
  });

  it('shows the empty state with the selector still reachable', async () => {
    setup();
    const { root } = await render('year-2026', { ...overviewFixture(), subscriptions: [], totals: [] });
    expect(root.textContent).toContain(en['subscriptions.emptyHeading']);
    expect(select(root)).toBeTruthy();
  });

  it('offers a year option for every fixture year', () => {
    expect(periodOptionsFixture().years).toEqual([2024, 2025, 2026, 2027]);
  });
});
