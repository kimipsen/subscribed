import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { catalogFixture } from './testing/fixtures';
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
