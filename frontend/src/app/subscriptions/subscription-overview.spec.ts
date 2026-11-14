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
    expect(root.querySelectorAll('thead th[scope=col]').length).toBe(6);
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
});
