import { inject } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { filter, map, of, take } from 'rxjs';
import { FoundationPage } from './foundation-page';
import { SessionService } from './session';

export const householdGuard: CanActivateFn = (_route, requested) => {
  const session = inject(SessionService);
  const router = inject(Router);
  const state = session.current();
  const ready = state.state === 'loading' ?
    toObservable(session.current).pipe(filter((value) => value.state !== 'loading'), take(1)) :
    of(state);
  return ready.pipe(map((value) => {
    if (value.state === 'anonymous') {
      return router.createUrlTree(['/welcome'], { queryParams: { returnUrl: requested.url } });
    }
    if (value.state === 'not-member') return router.createUrlTree(['/access-denied']);
    return true;
  }));
};

export const routes: Routes = [
  { path: '', component: FoundationPage, data: { page: 'home' } },
  { path: 'household', component: FoundationPage, canActivate: [householdGuard], data: { page: 'household' } },
  {
    path: 'subscriptions/new',
    canActivate: [householdGuard],
    loadComponent: () => import('./subscriptions/subscription-form-page').then((m) => m.SubscriptionFormPage),
  },
  {
    path: 'subscriptions/:id/edit',
    canActivate: [householdGuard],
    loadComponent: () => import('./subscriptions/subscription-form-page').then((m) => m.SubscriptionFormPage),
  },
  {
    path: 'subscriptions/:id',
    canActivate: [householdGuard],
    loadComponent: () => import('./subscriptions/subscription-detail-page').then((m) => m.SubscriptionDetailPage),
  },
  { path: 'welcome', component: FoundationPage, data: { page: 'welcome' } },
  { path: 'access-denied', component: FoundationPage, data: { page: 'access-denied' } },
  { path: 'signed-out', component: FoundationPage, data: { page: 'signed-out' } },
  { path: '**', component: FoundationPage, data: { page: 'not-found' } },
];
