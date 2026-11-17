import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Period } from './period';
import { catalogFixture, detailFixture, overviewFixture } from './testing/fixtures';
import { periodOptionsFixture } from './testing/period-providers';
import { SubscriptionsService } from './subscriptions.service';

describe('SubscriptionsService catalog', () => {
  it('loads the catalog from /api/subscriptions/catalog', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    let received: unknown;
    TestBed.inject(SubscriptionsService).catalog().subscribe((c) => (received = c));
    const request = http.expectOne('/api/subscriptions/catalog');
    expect(request.request.method).toBe('GET');
    request.flush(catalogFixture());
    expect(received).toEqual(catalogFixture());
    http.verify();
  });
});

describe('SubscriptionsService household zone', () => {
  it('reads the zone from GET /api/household', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    let zone: unknown;
    TestBed.inject(SubscriptionsService).householdZone().subscribe((z) => (zone = z));
    const request = http.expectOne('/api/household');
    expect(request.request.method).toBe('GET');
    request.flush({ id: 'h', name: 'Home', version: 1, timeZoneId: 'Europe/Copenhagen', calendarFeed: { enabled: false, rotatedAt: null }, members: [] });
    expect(zone).toBe('Europe/Copenhagen');
    http.verify();
  });
});

describe('SubscriptionsService period requests', () => {
  function setup() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    return { http: TestBed.inject(HttpTestingController), service: TestBed.inject(SubscriptionsService) };
  }
  const kinds: [Period, string][] = [
    [{ kind: 'year', year: 2026 }, 'year-2026'],
    [{ kind: 'last-12-months' }, 'last-12-months'],
    [{ kind: 'next-12-months' }, 'next-12-months'],
  ];

  for (const [period, key] of kinds) {
    it(`sends ?period=${key} on the overview and the detail`, () => {
      const { http, service } = setup();
      service.overview(period).subscribe();
      const overview = http.expectOne(`/api/subscriptions?period=${key}`);
      expect(overview.request.method).toBe('GET');
      expect(overview.request.params.get('period')).toBe(key);
      overview.flush(overviewFixture());
      service.detail('abc', period).subscribe();
      http.expectOne(`/api/subscriptions/abc?period=${key}`).flush(detailFixture());
      http.verify();
    });
  }

  it('sends no period when the caller shows no period figures (the form)', () => {
    const { http, service } = setup();
    service.overview().subscribe();
    const overview = http.expectOne('/api/subscriptions');
    expect(overview.request.params.keys()).toEqual([]);
    overview.flush(overviewFixture());
    service.detail('abc').subscribe();
    const detail = http.expectOne('/api/subscriptions/abc');
    expect(detail.request.params.keys()).toEqual([]);
    detail.flush(detailFixture());
    http.verify();
  });

  it('loads the selector options from GET /api/subscriptions/periods', () => {
    const { http, service } = setup();
    let received: unknown;
    service.periods().subscribe((options) => (received = options));
    const request = http.expectOne('/api/subscriptions/periods');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.keys()).toEqual([]);
    request.flush(periodOptionsFixture());
    expect(received).toEqual(periodOptionsFixture());
    http.verify();
  });

  it('carries the period and the If-Match header on pause, resume and the price mutations, and only those', () => {
    const { http, service } = setup();
    const period: Period = { kind: 'next-12-months' };
    const body = { effectiveFrom: '2026-12-01', amount: '10.00', cadenceUnit: 'month' as const, cadenceCount: 1, paymentOptionId: null };
    const calls: [string, string, () => void][] = [
      ['POST', '/api/subscriptions/x/pause', () => service.pause('x', 3, period).subscribe()],
      ['POST', '/api/subscriptions/x/resume', () => service.resume('x', 3, period).subscribe()],
      ['POST', '/api/subscriptions/x/prices', () => service.addPrice('x', 3, body, period).subscribe()],
      ['PUT', '/api/subscriptions/x/prices/e1', () => service.correctPrice('x', 3, 'e1', body, period).subscribe()],
      ['DELETE', '/api/subscriptions/x/prices/e1', () => service.removePrice('x', 3, 'e1', period).subscribe()],
    ];
    for (const [method, url, call] of calls) {
      call();
      const request = http.expectOne({ method, url: `${url}?period=next-12-months` });
      expect(request.request.headers.get('If-Match'), url).toBe('"3"');
      request.flush(detailFixture());
    }
    http.verify();
  });

  it('sends the pause-period calls to /pauses with the bodies, the period and the If-Match header', () => {
    const { http, service } = setup();
    const period: Period = { kind: 'year', year: 2026 };
    const closed = { pauseDate: '2026-03-01', resumeDate: '2026-03-10' };
    const open = { pauseDate: '2026-04-01', resumeDate: null };

    service.addPause('x', 5, closed, period).subscribe();
    const add = http.expectOne({ method: 'POST', url: '/api/subscriptions/x/pauses?period=year-2026' });
    expect(add.request.headers.get('If-Match')).toBe('"5"');
    expect(add.request.body).toEqual(closed);
    add.flush(detailFixture());

    service.changePause('x', 6, 'p1', open, period).subscribe();
    const change = http.expectOne({ method: 'PUT', url: '/api/subscriptions/x/pauses/p1?period=year-2026' });
    expect(change.request.headers.get('If-Match')).toBe('"6"');
    expect(change.request.body).toEqual({ pauseDate: '2026-04-01', resumeDate: null });
    change.flush(detailFixture());

    service.removePause('x', 7, 'p1', { kind: 'last-12-months' }).subscribe();
    const remove = http.expectOne({ method: 'DELETE', url: '/api/subscriptions/x/pauses/p1?period=last-12-months' });
    expect(remove.request.headers.get('If-Match')).toBe('"7"');
    expect(remove.request.body).toBeNull();
    remove.flush(detailFixture());
    http.verify();
  });

  it('puts the period on an update only when asked, and keeps the If-Match header', () => {
    const { http, service } = setup();
    const body = { name: 'n', category: null, notes: null, currency: 'DKK', startDate: '2026-01-01', endDate: null, catalogId: null, unsubscribeUrl: null, cancelBy: null };
    service.update('x', 4, body, { kind: 'last-12-months' }).subscribe();
    const withPeriod = http.expectOne({ method: 'PUT', url: '/api/subscriptions/x?period=last-12-months' });
    expect(withPeriod.request.headers.get('If-Match')).toBe('"4"');
    withPeriod.flush(detailFixture());
    service.update('x', 4, body).subscribe();
    const without = http.expectOne((r) => r.method === 'PUT' && r.url === '/api/subscriptions/x' && r.params.keys().length === 0);
    expect(without.request.headers.get('If-Match')).toBe('"4"');
    without.flush(detailFixture());
    http.verify();
  });

  it('removes a subscription with no period', () => {
    const { http, service } = setup();
    service.remove('x', 2).subscribe();
    const request = http.expectOne({ method: 'DELETE', url: '/api/subscriptions/x' });
    expect(request.request.headers.get('If-Match')).toBe('"2"');
    request.flush(null, { status: 204, statusText: 'No Content' });
    http.verify();
  });
});
