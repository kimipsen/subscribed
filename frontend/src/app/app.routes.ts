import { Routes } from '@angular/router';
import { FoundationPage } from './foundation-page';

export const routes: Routes = [
  { path: '', component: FoundationPage, data: { page: 'home' } },
  { path: 'household', component: FoundationPage, data: { page: 'household' } },
  { path: 'welcome', component: FoundationPage, data: { page: 'welcome' } },
  { path: 'access-denied', component: FoundationPage, data: { page: 'access-denied' } },
  { path: 'signed-out', component: FoundationPage, data: { page: 'signed-out' } },
  { path: '**', component: FoundationPage, data: { page: 'not-found' } },
];
