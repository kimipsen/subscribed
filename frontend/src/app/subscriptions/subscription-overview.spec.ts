import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE, LanguageService } from '../i18n/i18n';
import { en } from '../i18n/catalog.en';
import { da } from '../i18n/catalog.da';
import { csrfInterceptor } from '../session';
import { SubscriptionOverviewComponent } from './subscription-overview';
import { SubscriptionOverview, SubscriptionSummary } from './subscription.models';
import { englishLeftovers } from './testing/english-leftovers';
import { overviewFixture, overviewWith } from './testing/fixtures';

const plain = (value: string | null | undefined) => (value ?? '').replace(/[\u00a0\u202f]/g, ' ').replace(/\s+/g, ' ').trim();

describe('SubscriptionOverviewComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([csrfInterceptor])),
        provideHttpClientTesting(),
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
    http.expectOne('/api/subscriptions').flush(data);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  const element = (fixture: { nativeElement: unknown }) => fixture.nativeElement as HTMLElement;
  const rowFor = (root: HTMLElement, name: string) =>
    Array.from(root.querySelectorAll('tbody tr')).find((row) => row.querySelector('th a')?.textContent?.trim() === name) as HTMLElement;

  it('shows a loading status first', () => {
    const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
    fixture.detectChanges();
    expect(element(fixture).querySelector('[role=status]')?.textContent).toContain(en['subscriptions.loading']);
    http.expectOne('/api/subscriptions').flush(overviewFixture());
  });

  it('renders the empty state with the entry point to add a subscription', async () => {
    const fixture = await render({ asOf: '2026-11-13', subscriptions: [], totals: [] });
    const root = element(fixture);
    expect(root.textContent).toContain(en['subscriptions.emptyHeading']);
    expect(root.querySelector('a[href="/subscriptions/new"]')).toBeTruthy();
    expect(root.querySelector('table')).toBeNull();
  });

  it('labels statuses and shows "price change scheduled" only where one is scheduled', async () => {
    const root = element(await render());
    const badge = (name: string) => rowFor(root, name).querySelector('.status-active, .status-ended, .status-not-started')?.textContent?.trim();
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
    const lines = Array.from(root.querySelectorAll('.totals li')).map((li) => plain(li.textContent));
    expect(lines.length).toBe(data.totals.length);
    expect(lines.length).toBeGreaterThan(1);
    expect(lines[0]).toContain('DKK');
    expect(lines[0]).toContain('per month');
    expect(lines[0]).toContain('per year');
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
    expect(root.querySelectorAll('thead th[scope=col]').length).toBe(7);
    const link = rowFor(root, 'StreamCo').querySelector('th[scope=row] a');
    expect(link?.getAttribute('href')).toMatch(/^\/subscriptions\/[0-9a-f]{32}$/);
    const region = root.querySelector('.table-scroll');
    expect(region?.getAttribute('role')).toBe('region');
    expect(region?.getAttribute('tabindex')).toBe('0');
    expect(region?.getAttribute('aria-label')).toBe(en['subscriptions.scrollRegion']);
  });

  it('shows an error with the reference and retries', async () => {
    const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
    fixture.detectChanges();
    http.expectOne('/api/subscriptions').flush({ traceId: 'trace-123' }, { status: 503, statusText: 'Unavailable' });
    await fixture.whenStable();
    fixture.detectChanges();
    const root = element(fixture);
    expect(root.querySelector('[role=alert]')?.textContent).toContain(en['subscriptions.loadFailed']);
    expect(root.textContent).toContain('trace-123');
    (Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim() === en['common.retry']) as HTMLButtonElement).click();
    http.expectOne('/api/subscriptions').flush(overviewFixture());
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
    expect(englishLeftovers(root)).toEqual([]);
  });

  it('re-renders amounts, cadence labels and status badges on a language switch without a request (EX-018)', async () => {
    const data = overviewFixture();
    const fixture = await render(data);
    const root = element(fixture);
    const streamCo = data.subscriptions.find((s) => s.name === 'StreamCo')!;
    expect(plain(rowFor(root, 'StreamCo').textContent)).toContain('DKK 1,548.00');
    expect(plain(rowFor(root, 'StreamCo').textContent)).toContain(en['cadence.monthly']);
    expect(streamCo.normalizedYearly).toBe('1548.00');

    TestBed.inject(LanguageService).setLanguage('da');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(plain(rowFor(root, 'StreamCo').textContent)).toContain('1.548,00 DKK');
    expect(plain(rowFor(root, 'StreamCo').textContent)).toContain(da['cadence.monthly']);
    expect(rowFor(root, 'StreamCo').querySelector('.badge')?.textContent?.trim()).toBe(da['subscriptions.status.active']);
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
      expect(rowFor(root, 'VideoTube').querySelector('.status-paused')?.textContent?.trim()).toBe(en['subscriptions.status.paused']);
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
      expect(rowFor(root, 'SportsLive').querySelector('.status-ended')).toBeTruthy();
    });

    it('posts the pause with the row version in If-Match and no body, reloads and says so politely', async () => {
      const overview = data();
      const fixture = await render(overview);
      const root = element(fixture);
      actionFor(root, 'StreamCo')!.click();
      const post = http.expectOne({ method: 'POST', url: `/api/subscriptions/${idOf(overview, 'StreamCo')}/pause` });
      expect(post.request.headers.get('If-Match')).toBe('"7"');
      expect(post.request.body).toBeNull();

      const after = structuredClone(overview);
      const row = after.subscriptions.find((s) => s.name === 'StreamCo')!;
      row.status = 'paused';
      row.paused = true;
      row.version = 8;
      post.flush({});
      http.expectOne({ method: 'GET', url: '/api/subscriptions' }).flush(after);
      await fixture.whenStable();
      fixture.detectChanges();

      expect(rowFor(root, 'StreamCo').querySelector('.status-paused')).toBeTruthy();
      expect(actionFor(root, 'StreamCo')?.textContent?.trim()).toBe(en['subscriptions.resume']);
      expect(root.querySelector('[role=status].status')?.textContent).toContain('StreamCo is paused.');
      expect(root.querySelector('[role=alert]')).toBeNull();
    });

    it('posts the resume for a paused row and then offers Pause again', async () => {
      const overview = data();
      const fixture = await render(overview);
      const root = element(fixture);
      actionFor(root, 'VideoTube')!.click();
      const post = http.expectOne({ method: 'POST', url: `/api/subscriptions/${idOf(overview, 'VideoTube')}/resume` });
      expect(post.request.headers.get('If-Match')).toBe('"5"');
      expect(post.request.body).toBeNull();

      const after = structuredClone(overview);
      const row = after.subscriptions.find((s) => s.name === 'VideoTube')!;
      row.status = 'active';
      row.paused = false;
      row.version = 6;
      post.flush({});
      http.expectOne({ method: 'GET', url: '/api/subscriptions' }).flush(after);
      await fixture.whenStable();
      fixture.detectChanges();

      expect(actionFor(root, 'VideoTube')?.textContent?.trim()).toBe(en['subscriptions.pause']);
      expect(root.querySelector('[role=status].status')?.textContent).toContain('VideoTube was resumed.');
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
      http.expectOne({ method: 'GET', url: '/api/subscriptions' }).flush(overview);
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
      http.expectOne({ method: 'GET', url: '/api/subscriptions' }).flush(reloaded);
      await fixture.whenStable();
      fixture.detectChanges();

      expect(root.querySelector('[role=alert]')?.textContent).toContain(en['subscriptions.form.conflict']);
      expect(root.querySelector('[role=status].status')).toBeNull();

      actionFor(root, 'StreamCo')!.click();
      const retry = http.expectOne((r) => r.method === 'POST');
      expect(retry.request.headers.get('If-Match')).toBe('"9"');
      retry.flush({});
      http.expectOne({ method: 'GET', url: '/api/subscriptions' }).flush(reloaded);
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
      http.expectOne({ method: 'GET', url: '/api/subscriptions' }).flush(overview);
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
      http.expectOne({ method: 'GET', url: '/api/subscriptions' }).flush(overview);
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
      expect(rowFor(root, 'VideoTube').querySelector('.status-paused')?.textContent?.trim()).toBe(da['subscriptions.status.paused']);
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
    http.expectOne('/api/subscriptions').flush(data);
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
  const status = (el: HTMLElement) => plain(el.querySelector('p[role=status]')?.textContent);
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
  const headings = (el: HTMLElement) => Array.from(el.querySelectorAll('.category-group h3')).map((h) => h.textContent?.trim());

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
    const el = root(await render({ asOf: '2026-11-13', subscriptions: [], totals: [] }));
    expect(el.textContent).toContain(en['subscriptions.emptyHeading']);
    expect(select(el)).toBeNull();
    expect(el.textContent).not.toContain(en['subscriptions.filter.emptyBody']);
  });

  it('groups alphabetically with Uncategorized last, counts every status and totals active rows per currency (AC4, AC5)', async () => {
    const fixture = await render(
      rows(
        { name: 'A', category: 'Media', currency: 'DKK', normalizedMonthly: '79.00', normalizedYearly: '948.00' },
        { name: 'B', category: 'media', currency: 'DKK', normalizedMonthly: '10.50', normalizedYearly: '126.00' },
        { name: 'C', category: 'Media', currency: 'EUR', normalizedMonthly: '5.00', normalizedYearly: '60.00' },
        { name: 'D', category: 'Media', currency: 'DKK', status: 'ended', normalizedMonthly: '9.00', normalizedYearly: '108.00' },
        { name: 'E', category: 'Bills', status: 'not-started' },
        { name: 'F', category: null },
      ),
    );
    const el = root(fixture);
    await group(fixture);
    expect(headings(el)).toEqual(['Bills', 'Media', en['subscriptions.filter.uncategorized']]);
    const sections = Array.from(el.querySelectorAll('.category-group'));
    expect(plain(sections[1].querySelector('p')?.textContent)).toBe('Subscriptions: 4');
    const lines = Array.from(sections[1].querySelectorAll('.totals li')).map((li) => plain(li.textContent));
    expect(lines).toHaveLength(2);
    expect(lines[0]).toContain('DKK 89.50 per month · DKK 1,074.00 per year');
    expect(lines[0]).toContain('2 active');
    expect(lines[1]).toContain('EUR 5.00 per month');
    expect(lines.some((line) => line.includes('DKK') && line.includes('EUR'))).toBe(false);
    // No active subscription in Bills: rows and count, but no totals line.
    expect(sections[0].querySelector('.totals')).toBeNull();
    expect(sections[0].querySelectorAll('tbody tr')).toHaveLength(1);
    await group(fixture, false);
    expect(el.querySelector('.category-group')).toBeNull();
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
    const fixture = await render(rows({ name: 'A', category: 'Bad', normalizedMonthly: '1,50' }, { name: 'B', category: 'Good' }));
    const el = root(fixture);
    await group(fixture);
    const [bad, good] = Array.from(el.querySelectorAll('.category-group'));
    expect(bad.querySelector('.totals')).toBeNull();
    expect(bad.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(good.querySelector('.totals')).toBeTruthy();
  });

  it('labels the controls and exposes group headings semantically (R5.1)', async () => {
    const fixture = await render(data());
    const el = root(fixture);
    expect(el.querySelector('label[for=category-filter]')?.textContent?.trim()).toBe(en['subscriptions.filter.label']);
    expect(el.querySelector('label[for=group-toggle]')?.textContent?.trim()).toBe(en['subscriptions.group.toggle']);
    expect(toggle(el).type).toBe('checkbox');
    expect(el.querySelectorAll('[role=region]')).toHaveLength(1);
    await group(fixture);
    const regions = el.querySelectorAll('[role=region]');
    expect(regions).toHaveLength(1);
    expect(regions[0].getAttribute('tabindex')).toBe('0');
    expect(regions[0].getAttribute('aria-label')).toBe(en['subscriptions.scrollRegion']);
    const sections = Array.from(el.querySelectorAll('.category-group'));
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
