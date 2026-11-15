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
