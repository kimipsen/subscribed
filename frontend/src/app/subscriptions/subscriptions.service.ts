import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Message } from '../i18n/i18n';
import { Observable } from 'rxjs';
import {
  AddSubscriptionBody,
  PriceEntryBody,
  SubscriptionDetail,
  SubscriptionOptions,
  SubscriptionOverview,
  UpdateSubscriptionBody,
} from './subscription.models';

const BASE = '/api/subscriptions';

function ifMatch(version: number): { headers: { 'If-Match': string } } {
  return { headers: { 'If-Match': `"${version}"` } };
}

@Injectable({ providedIn: 'root' })
export class SubscriptionsService {
  private readonly http = inject(HttpClient);

  /** A one-time polite status message for the next overview, e.g. after a removal. */
  readonly notice = signal<Message | null>(null);

  overview(): Observable<SubscriptionOverview> {
    return this.http.get<SubscriptionOverview>(BASE);
  }

  options(): Observable<SubscriptionOptions> {
    return this.http.get<SubscriptionOptions>(`${BASE}/options`);
  }

  detail(id: string): Observable<SubscriptionDetail> {
    return this.http.get<SubscriptionDetail>(`${BASE}/${id}`);
  }

  add(body: AddSubscriptionBody): Observable<SubscriptionDetail> {
    return this.http.post<SubscriptionDetail>(BASE, body);
  }

  update(id: string, version: number, body: UpdateSubscriptionBody): Observable<SubscriptionDetail> {
    return this.http.put<SubscriptionDetail>(`${BASE}/${id}`, body, ifMatch(version));
  }

  remove(id: string, version: number): Observable<void> {
    return this.http.delete<void>(`${BASE}/${id}`, ifMatch(version));
  }

  addPrice(id: string, version: number, body: PriceEntryBody): Observable<SubscriptionDetail> {
    return this.http.post<SubscriptionDetail>(`${BASE}/${id}/prices`, body, ifMatch(version));
  }

  correctPrice(id: string, version: number, entryId: string, body: PriceEntryBody): Observable<SubscriptionDetail> {
    return this.http.put<SubscriptionDetail>(`${BASE}/${id}/prices/${entryId}`, body, ifMatch(version));
  }

  removePrice(id: string, version: number, entryId: string): Observable<SubscriptionDetail> {
    return this.http.delete<SubscriptionDetail>(`${BASE}/${id}/prices/${entryId}`, ifMatch(version));
  }
}
