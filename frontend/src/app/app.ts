import { Component, DestroyRef, ElementRef, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet, NavigationEnd } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { SessionService } from './session';
import { LanguageService } from './i18n/i18n';
import { SUPPORTED_LANGUAGES, isLanguage } from './i18n/languages';
import { TranslatePipe } from './i18n/translate.pipe';

@Component({
  selector: 'app-root',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, FormsModule, TranslatePipe],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  host: { '(keydown.escape)': 'menuOpen.set(false)' },
})
export class App {
  readonly i18n = inject(LanguageService);
  readonly languages = SUPPORTED_LANGUAGES;
  readonly session = inject(SessionService);
  readonly router = inject(Router);
  readonly menuOpen = signal(false);
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);

  onLanguage(value: string): void {
    if (isLanguage(value)) this.i18n.setLanguage(value);
  }

  constructor() {
    this.session.load();
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe(() => {
      this.menuOpen.set(false);
      setTimeout(() => this.element.nativeElement.querySelector<HTMLElement>('h1')?.focus());
    });
  }
}
