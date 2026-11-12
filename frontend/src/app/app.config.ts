import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { csrfInterceptor } from './session';

import { routes } from './app.routes';
import { registerLocaleData } from '@angular/common';
import localeEnGb from '@angular/common/locales/en-GB';
import localeDa from '@angular/common/locales/da';

registerLocaleData(localeEnGb, 'en-GB');
registerLocaleData(localeDa, 'da-DK');

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([csrfInterceptor]))
  ]
};
