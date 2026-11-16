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
import { SubscriptionOverview } from './subscription.models';
import { englishLeftovers } from './testing/english-leftovers';
import { overviewFixture } from './testing/fixtures';

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
    Array.from(root.querySelectorAll('tbody tr')).find((row) => row.querySelector('th')?.textContent?.trim() === name) as HTMLElement;

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
});
