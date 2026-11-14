import { Component, inject } from '@angular/core';
import { TranslatePipe } from '../i18n/translate.pipe';
import { SessionService } from '../session';

/** The panel shown when the session could not be checked. This does not mean the member is signed out. */
@Component({
  selector: 'app-session-unavailable',
  imports: [TranslatePipe],
  template: `
    <section class="panel">
      <h1 tabindex="-1">{{ 'unavailable.heading' | t }}</h1>
      <p role="alert">{{ 'unavailable.message' | t }}</p>
      @if (session.unavailableReference()) {
        <p>{{ 'common.referenceLabel' | t }} <code>{{ session.unavailableReference() }}</code></p>
      }
      <button type="button" (click)="session.load()">{{ 'common.retry' | t }}</button>
    </section>
  `,
})
export class SessionUnavailable {
  readonly session = inject(SessionService);
}
