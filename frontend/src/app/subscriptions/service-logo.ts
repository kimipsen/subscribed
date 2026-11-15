import { Component, computed, input, linkedSignal } from '@angular/core';

/**
 * A service's logo, or an initial-letter fallback when there is none or it fails to load. The logo is decorative (`alt=""`):
 * the service name is always rendered as adjacent text.
 */
@Component({
  selector: 'app-service-logo',
  template: `
    @if (logoUrl() && !failed()) {
      <img class="logo-image" [src]="logoUrl()" alt="" loading="lazy" [attr.width]="size()" [attr.height]="size()" (error)="failed.set(true)" />
    } @else {
      <span class="logo-fallback" aria-hidden="true" [style.width.px]="size()" [style.height.px]="size()" [style.font-size.px]="size() * 0.55">{{ initial() }}</span>
    }
  `,
  styles: `
    :host {
      display: inline-flex;
      flex: none;
      vertical-align: middle;
    }
    .logo-image {
      object-fit: contain;
      border-radius: 4px;
    }
    .logo-fallback {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      border-radius: 4px;
      background: var(--color-nav-active-bg);
      color: var(--color-nav-active-text);
      font-weight: 600;
      line-height: 1;
    }
  `,
})
export class ServiceLogo {
  readonly name = input.required<string>();
  readonly logoUrl = input<string | null>(null);
  readonly size = input(24);

  /** Set when the image fails to load; reset whenever the URL changes. */
  readonly failed = linkedSignal<string | null, boolean>({ source: this.logoUrl, computation: () => false });

  /** The first letter, grapheme-safe for emoji and non-Latin names; '?' for an empty name. */
  readonly initial = computed(() => (Array.from(this.name().trim())[0] ?? '?').toLocaleUpperCase());
}
