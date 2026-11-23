import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { routes } from '../app.routes';
import { BROWSER_LANGUAGES, LANGUAGE_STORAGE } from '../i18n/i18n';
import { SessionService, csrfInterceptor } from '../session';
import { SubscriptionDetailPage } from '../subscriptions/subscription-detail-page';
import { SubscriptionOverviewComponent } from '../subscriptions/subscription-overview';
import { SubscriptionDetail, SubscriptionOverview } from '../subscriptions/subscription.models';
import { detailFixture, overviewFixture, paymentOptionListFixture } from '../subscriptions/testing/fixtures';
import { flushPeriods, periodTestProviders } from '../subscriptions/testing/period-providers';
import { defaultIsVisible, focusVisible } from './focus-visible-target';

describe('focusVisible', () => {
  let host: HTMLElement;

  beforeEach(() => {
    host = document.createElement('div');
    document.body.appendChild(host);
  });
  afterEach(() => host.remove());

  const render = (html: string) => {
    host.innerHTML = html;
    return host;
  };

  it('focuses a plainly rendered copy with the default predicate', () => {
    render('<button id="only" data-focus-key="action-1">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(true);
    expect(document.activeElement?.id).toBe('only');
  });

  it('focuses the first copy when both are present and the first is visible', () => {
    render('<button id="table" data-focus-key="action-1">Pause</button><button id="card" data-focus-key="action-1">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(true);
    expect(document.activeElement?.id).toBe('table');
  });

  it('focuses the second copy when the first has the hidden attribute', () => {
    render('<button id="table" hidden data-focus-key="action-1">Pause</button><button id="card" data-focus-key="action-1">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(true);
    expect(document.activeElement?.id).toBe('card');
  });

  it('focuses the second copy when the first sits inside a [hidden] ancestor', () => {
    render('<section hidden><button id="table" data-focus-key="action-1">Pause</button></section><button id="card" data-focus-key="action-1">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(true);
    expect(document.activeElement?.id).toBe('card');
  });

  it('returns false when every copy is hidden', () => {
    render('<button hidden data-focus-key="action-1">Pause</button><section hidden><button data-focus-key="action-1">Pause</button></section>');
    expect(focusVisible(document, 'action-1')).toBe(false);
    expect(document.activeElement).toBe(document.body);
  });

  it('returns false when nothing matches the key', () => {
    render('<button data-focus-key="action-2">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(false);
    expect(document.activeElement).toBe(document.body);
  });

  it('escapes keys that are not valid CSS identifiers', () => {
    render('<button id="odd" data-focus-key="a&quot;]b">Odd</button>');
    expect(focusVisible(document, 'a"]b')).toBe(true);
    expect(document.activeElement?.id).toBe('odd');
  });

  it('uses an injected predicate instead of the default', () => {
    render('<button id="first" data-focus-key="action-1">Pause</button><button id="second" data-focus-key="action-1">Pause</button>');
    const isVisible = vi.fn((element: HTMLElement) => element.id === 'second');
    expect(focusVisible(host, 'action-1', isVisible)).toBe(true);
    expect(isVisible).toHaveBeenCalledTimes(2);
    expect(document.activeElement?.id).toBe('second');
  });

  it('treats an element as hidden when checkVisibility reports false', () => {
    render('<button id="x" data-focus-key="k">x</button>');
    const element = host.querySelector<HTMLElement>('#x')!;
    element.checkVisibility = () => false;
    expect(defaultIsVisible(element)).toBe(false);
    element.checkVisibility = () => true;
    expect(defaultIsVisible(element)).toBe(true);
  });
});


/**
 * The migrated call sites, with the real helper and no stubbing: a plainly rendered row action is focused (positive test per
 * site), and when the table copy is hidden the card copy is the one that receives focus.
 */
describe('focusVisible at the migrated call sites', () => {
  let http: HttpTestingController;
  const Q = '?period=year-2026';

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

  const hideTables = (root: HTMLElement) => root.querySelectorAll('[data-table-scroll]').forEach((section) => section.setAttribute('hidden', ''));

  describe('overview action-<id>', () => {
    const OVERVIEW_URL = '/api/subscriptions?period=year-2026';

    async function openOverview(data: SubscriptionOverview) {
      const fixture = TestBed.createComponent(SubscriptionOverviewComponent);
      document.body.appendChild(fixture.nativeElement);
      fixture.detectChanges();
      http.expectOne(OVERVIEW_URL).flush(data);
      flushPeriods(http);
      await fixture.whenStable();
      fixture.detectChanges();
      return { fixture, root: fixture.nativeElement as HTMLElement };
    }

    async function pause(fixture: Awaited<ReturnType<typeof openOverview>>['fixture'], data: SubscriptionOverview, button: HTMLButtonElement, id: string) {
      button.click();
      http.expectOne({ method: 'POST', url: `/api/subscriptions/${id}/pause?period=year-2026` }).flush({});
      http.expectOne({ method: 'GET', url: OVERVIEW_URL }).flush(data);
      await fixture.whenStable();
      fixture.detectChanges();
    }

    const active = () => {
      const data = overviewFixture();
      const row = data.subscriptions.find((s) => s.status === 'active' && !s.paused)!;
      return { data, id: row.id };
    };

    it('focuses the plainly rendered table button after a pause', async () => {
      const { data, id } = active();
      const { fixture, root } = await openOverview(data);
      try {
        await pause(fixture, data, root.querySelector<HTMLButtonElement>(`#action-${id}`)!, id);
        expect(document.activeElement?.id).toBe(`action-${id}`);
      } finally {
        root.remove();
      }
    });

    it('focuses the card button when the table copy is hidden', async () => {
      const { data, id } = active();
      const { fixture, root } = await openOverview(data);
      try {
        hideTables(root);
        await pause(fixture, data, root.querySelector<HTMLButtonElement>(`#card-action-${id}`)!, id);
        expect(document.activeElement?.id).toBe(`card-action-${id}`);
      } finally {
        root.remove();
      }
    });
  });

  describe('detail page row actions', () => {
    const withPauses = (): SubscriptionDetail => ({
      ...detailFixture(),
      pauseHistory: [
        { id: 'p2', pauseDate: '2026-11-01', resumeDate: null },
        { id: 'p1', pauseDate: '2026-03-01', resumeDate: '2026-03-10' },
      ],
    });

    async function openDetail(detail: SubscriptionDetail) {
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl(`/subscriptions/${detail.id}`, SubscriptionDetailPage);
      http.expectOne('/api/payment-options').flush(paymentOptionListFixture());
      http.expectOne('/api/household').flush({ timeZoneId: 'Europe/Copenhagen' });
      http.expectOne(`/api/subscriptions/${detail.id}${Q}`).flush(detail);
      harness.detectChanges();
      await harness.fixture.whenStable();
      harness.detectChanges();
      flushPeriods(http);
      harness.detectChanges();
      await harness.fixture.whenStable();
      harness.detectChanges();
      return { harness, root: harness.routeNativeElement as HTMLElement, detail };
    }

    const press = async (harness: RouterTestingHarness, root: HTMLElement, selector: string) => {
      root.querySelector<HTMLElement>(selector)!.click();
      harness.detectChanges();
      await harness.fixture.whenStable();
      harness.detectChanges();
    };

    /** Roots moved into the document so focus works. Removed after each test: a leftover copy would win `#id` lookups of the next one. */
    const attached: HTMLElement[] = [];
    afterEach(() => attached.splice(0).forEach((root) => root.remove()));

    type Site = { name: string; key: string; open: string; leave: string };
    const sites = (detail: SubscriptionDetail): Site[] => {
      const later = detail.priceHistory.find((h) => !h.initial)!.entryId;
      return [
        { name: 'closeEntry', key: `correct-${later}`, open: `correct-${later}`, leave: '[data-entry-form] button.btn-outline' },
        { name: 'cancelRemoveEntry', key: `remove-${later}`, open: `remove-${later}`, leave: '[data-confirm-entry] button.btn-outline' },
        { name: 'closePause', key: 'edit-pause-p1', open: 'edit-pause-p1', leave: '[data-pause-form] button.btn-outline' },
        { name: 'cancelRemovePause', key: 'delete-pause-p1', open: 'delete-pause-p1', leave: '[data-confirm-pause] button.btn-outline' },
      ];
    };

    for (const index of [0, 1, 2, 3]) {
      it(`focuses the plainly rendered table button at site ${['closeEntry', 'cancelRemoveEntry', 'closePause', 'cancelRemovePause'][index]}`, async () => {
        const { harness, root, detail } = await openDetail(withPauses());
        attached.push(root);
        document.body.appendChild(root);
        const site = sites(detail)[index];
        await press(harness, root, `#${site.open}`);
        await press(harness, root, site.leave);
        expect(document.activeElement?.id).toBe(site.key);
      });

      it(`focuses the card button at site ${['closeEntry', 'cancelRemoveEntry', 'closePause', 'cancelRemovePause'][index]} when the table copy is hidden`, async () => {
        const { harness, root, detail } = await openDetail(withPauses());
        attached.push(root);
        document.body.appendChild(root);
        hideTables(root);
        const site = sites(detail)[index];
        await press(harness, root, `#card-${site.open}`);
        await press(harness, root, site.leave);
        expect(document.activeElement?.id).toBe(`card-${site.key}`);
      });
    }
  });
});
