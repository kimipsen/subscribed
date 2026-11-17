import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Message } from '../i18n/i18n';
import { Household } from '../session';
import { Observable, map } from 'rxjs';
import {
  AddSubscriptionBody,
  Catalog,
  PeriodOptions,
  PriceEntryBody,
  SubscriptionDetail,
  SubscriptionOptions,
  SubscriptionOverview,
  UpdateSubscriptionBody,
} from './subscription.models';

import { Period, periodKey } from './period';

const BASE = '/api/subscriptions';

/** The `?period=` query of a request that totals over a period (a mutation answers with the detail for it). */
function periodParams(period: Period | undefined): { params?: HttpParams } {
  return period ? { params: new HttpParams().set('period', periodKey(period)) } : {};
}

function ifMatch(version: number): { headers: { 'If-Match': string } } {
  return { headers: { 'If-Match': `"${version}"` } };
}

@Injectable({ providedIn: 'root' })
export class SubscriptionsService {
  private readonly http = inject(HttpClient);

  /** A one-time polite status message for the next overview, e.g. after a removal. */
  readonly notice = signal<Message | null>(null);

  /** The household's IANA time zone: cancel-by deadlines are entered and shown in that zone. */
  householdZone(): Observable<string> {
    return this.http.get<Household>('/api/household').pipe(map((household) => household.timeZoneId));
  }

  /** `period` is omitted by a caller that does not show period figures (the form only needs the rows): the API then uses the current year. */
  overview(period?: Period): Observable<SubscriptionOverview> {
    return this.http.get<SubscriptionOverview>(BASE, periodParams(period));
  }

  /** The calendar years the period selector offers. */
  periods(): Observable<PeriodOptions> {
    return this.http.get<PeriodOptions>(`${BASE}/periods`);
  }

  options(): Observable<SubscriptionOptions> {
    return this.http.get<SubscriptionOptions>(`${BASE}/options`);
  }

  catalog(): Observable<Catalog> {
    return this.http.get<Catalog>(`${BASE}/catalog`);
  }

  detail(id: string, period?: Period): Observable<SubscriptionDetail> {
    return this.http.get<SubscriptionDetail>(`${BASE}/${id}`, periodParams(period));
  }

  add(body: AddSubscriptionBody): Observable<SubscriptionDetail> {
    return this.http.post<SubscriptionDetail>(BASE, body);
  }

  /** `period` is for a caller that shows the returned detail's period total; the edit form does not, and omits it. */
  update(id: string, version: number, body: UpdateSubscriptionBody, period?: Period): Observable<SubscriptionDetail> {
    return this.http.put<SubscriptionDetail>(`${BASE}/${id}`, body, { ...ifMatch(version), ...periodParams(period) });
  }

  remove(id: string, version: number): Observable<void> {
    return this.http.delete<void>(`${BASE}/${id}`, ifMatch(version));
  }

  pause(id: string, version: number, period: Period): Observable<SubscriptionDetail> {
    return this.http.post<SubscriptionDetail>(`${BASE}/${id}/pause`, null, { ...ifMatch(version), ...periodParams(period) });
  }

  resume(id: string, version: number, period: Period): Observable<SubscriptionDetail> {
    return this.http.post<SubscriptionDetail>(`${BASE}/${id}/resume`, null, { ...ifMatch(version), ...periodParams(period) });
  }

  addPrice(id: string, version: number, body: PriceEntryBody, period: Period): Observable<SubscriptionDetail> {
    return this.http.post<SubscriptionDetail>(`${BASE}/${id}/prices`, body, { ...ifMatch(version), ...periodParams(period) });
  }

  correctPrice(id: string, version: number, entryId: string, body: PriceEntryBody, period: Period): Observable<SubscriptionDetail> {
    return this.http.put<SubscriptionDetail>(`${BASE}/${id}/prices/${entryId}`, body, { ...ifMatch(version), ...periodParams(period) });
  }

  removePrice(id: string, version: number, entryId: string, period: Period): Observable<SubscriptionDetail> {
    return this.http.delete<SubscriptionDetail>(`${BASE}/${id}/prices/${entryId}`, { ...ifMatch(version), ...periodParams(period) });
  }
}
