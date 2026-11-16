import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import {
  PaymentOption,
  PaymentOptionBody,
  PaymentOptionList,
  PaymentOptionSuggestion,
  PaymentOptionSuggestionList,
} from './subscription.models';

const BASE = '/api/payment-options';

function ifMatch(version: number): { headers: { 'If-Match': string } } {
  return { headers: { 'If-Match': `"${version}"` } };
}

@Injectable({ providedIn: 'root' })
export class PaymentOptionsService {
  private readonly http = inject(HttpClient);

  /** Every option of the household, removed ones included (`removed: true`), so the price history can resolve any id. */
  list(): Observable<PaymentOption[]> {
    return this.http.get<PaymentOptionList>(BASE).pipe(map((list) => list.options));
  }

  suggestions(): Observable<PaymentOptionSuggestion[]> {
    return this.http.get<PaymentOptionSuggestionList>(`${BASE}/suggestions`).pipe(map((list) => list.suggestions));
  }

  create(body: PaymentOptionBody): Observable<PaymentOption> {
    return this.http.post<PaymentOption>(BASE, body);
  }

  update(id: string, version: number, body: PaymentOptionBody): Observable<PaymentOption> {
    return this.http.put<PaymentOption>(`${BASE}/${id}`, body, ifMatch(version));
  }

  remove(id: string, version: number): Observable<void> {
    return this.http.delete<void>(`${BASE}/${id}`, ifMatch(version));
  }
}
