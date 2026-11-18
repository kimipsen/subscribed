import { Component, inject } from '@angular/core';
import { TranslatePipe } from '../i18n/translate.pipe';
import { SessionService } from '../session';
import { Panel } from '../ui/panel';

/** The panel shown when the session could not be checked. This does not mean the member is signed out. */
@Component({
  selector: 'app-session-unavailable',
  imports: [Panel, TranslatePipe],
  template: `
    <app-panel [narrow]="true">
      <h1 class="mt-0 mb-2 text-2xl font-semibold wrap-anywhere" tabindex="-1">{{ 'unavailable.heading' | t }}</h1>
      <p class="my-2" role="alert">{{ 'unavailable.message' | t }}</p>
      @if (session.unavailableReference()) {
        <p class="my-2">{{ 'common.referenceLabel' | t }} <code>{{ session.unavailableReference() }}</code></p>
      }
      <div class="mt-2 flex flex-wrap gap-2">
        <button type="button" class="btn btn-primary" (click)="session.load()">{{ 'common.retry' | t }}</button>
      </div>
    </app-panel>
  `,
})
export class SessionUnavailable {
  readonly session = inject(SessionService);
}
