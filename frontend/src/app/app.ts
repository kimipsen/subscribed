import { Component, DestroyRef, ElementRef, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet, NavigationEnd } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { SessionService } from './session';

@Component({
  selector: 'app-root',
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  host: { '(keydown.escape)': 'menuOpen.set(false)' },
})
export class App {
  readonly session = inject(SessionService);
  readonly router = inject(Router);
  readonly menuOpen = signal(false);
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);

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
