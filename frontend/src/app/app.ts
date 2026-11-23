import { DOCUMENT } from '@angular/common';
import { Component, DestroyRef, ElementRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet, NavigationEnd } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { House, LucideAngularModule, PanelLeftClose, PanelLeftOpen, Users, Wallet } from 'lucide-angular';
import { SessionService } from './session';
import { Notice } from './ui/notice';
import { SidebarService } from './sidebar/sidebar';
import { LanguageService, MessageKey } from './i18n/i18n';
import { SUPPORTED_LANGUAGES, isLanguage } from './i18n/languages';
import { TranslatePipe } from './i18n/translate.pipe';
import { THEME_MODES, ThemeMode, ThemeService, isThemeMode } from './theme/theme';
import { APP_VERSION } from './version';

/** The `nav` breakpoint of styles.css (900px). Keep the two in step: the drawer must not stay open at or above it. */
const NAV_BREAKPOINT_QUERY = '(min-width: 900px)';

@Component({
  selector: 'app-root',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, FormsModule, TranslatePipe, LucideAngularModule, Notice],
  templateUrl: './app.html',
  host: { class: 'block min-h-dvh', '(keydown.escape)': 'menuOpen.set(false)' },
})
export class App {
  readonly i18n = inject(LanguageService);
  readonly languages = SUPPORTED_LANGUAGES;
  readonly theme = inject(ThemeService);
  readonly themeModes = THEME_MODES;
  readonly themeLabels: Record<ThemeMode, MessageKey> = {
    system: 'theme.system',
    light: 'theme.light',
    dark: 'theme.dark',
  };
  readonly session = inject(SessionService);
  readonly sidebar = inject(SidebarService);
  protected readonly icons = { House, Users, Wallet, PanelLeftClose, PanelLeftOpen };
  readonly router = inject(Router);
  readonly menuOpen = signal(false);
  /** Members get the Menu button and the drawer, so their header controls move into the drawer below `nav`. */
  protected readonly isMember = computed(() => this.session.current().state === 'member');
  protected readonly version = APP_VERSION;
  protected readonly repoUrl = 'https://github.com/kimipsen/subscribed';
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  /** The modal navigation drawer. A signal query, because the dialog only exists while the session is `member`. */
  readonly drawer = viewChild<ElementRef<HTMLDialogElement>>('drawer');
  private readonly menuButton = viewChild<ElementRef<HTMLButtonElement>>('menuButton');
  private backdropPress = false;

  onLanguage(value: string): void {
    if (isLanguage(value)) this.i18n.setLanguage(value);
  }

  onTheme(value: string): void {
    if (isThemeMode(value)) this.theme.setMode(value);
  }

  /** Sign out closes the drawer first: a failed sign-out leaves the session `member`, so the dialog would stay modal over the error notice. */
  onDrawerSignOut(): void {
    this.menuOpen.set(false);
    this.session.signOut();
  }

  /** `close` fires for Escape, the close button and `close()`. It can arrive late, so a re-opened dialog is left alone. */
  onDrawerClosed(): void {
    if (!this.drawer()?.nativeElement.open) this.menuOpen.set(false);
    this.restoreFocusFallback();
  }

  onDrawerPointerDown(event: Event): void {
    this.backdropPress = event.target === event.currentTarget;
  }

  /** A backdrop tap is a press and a release that both land on the dialog element itself, not on its content. */
  onDrawerClick(event: Event): void {
    if (this.backdropPress && event.target === event.currentTarget) this.menuOpen.set(false);
    this.backdropPress = false;
  }

  constructor() {
    this.session.load();
    const destroyRef = inject(DestroyRef);
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      takeUntilDestroyed(destroyRef),
    ).subscribe(() => {
      this.menuOpen.set(false);
      // Close now so the dialog's focus restore runs before the h1 takes focus below.
      const dialog = this.drawer()?.nativeElement;
      if (dialog?.open) dialog.close();
      setTimeout(() => this.element.nativeElement.querySelector<HTMLElement>('h1')?.focus());
    });

    // Without a member session there is no dialog and no Menu button, so nothing may stay "open".
    effect(() => {
      if (this.session.current().state !== 'member') untracked(() => this.menuOpen.set(false));
    });

    // menuOpen is the source of truth; the native dialog follows it. Effects run during change detection.
    effect((onCleanup) => {
      const dialog = this.drawer()?.nativeElement;
      if (!dialog) return;
      if (this.menuOpen()) {
        try {
          if (!dialog.open) dialog.showModal();
        } catch {
          // showModal() throws InvalidStateError for a detached or already-modal element: stay closed, lock nothing.
          untracked(() => this.menuOpen.set(false));
          return;
        }
        const body = this.document.body;
        body.classList.add('overflow-hidden');
        onCleanup(() => body.classList.remove('overflow-hidden'));
      } else if (dialog.open) {
        dialog.close();
        this.restoreFocusFallback();
      }
    });

    // At or above the breakpoint the drawer is hidden by CSS, but an open modal would still make the page inert.
    const query = this.document.defaultView?.matchMedia?.(NAV_BREAKPOINT_QUERY);
    if (query) {
      const onChange = (event: MediaQueryListEvent) => {
        if (event.matches) this.menuOpen.set(false);
      };
      query.addEventListener('change', onChange);
      destroyRef.onDestroy(() => query.removeEventListener('change', onChange));
    }
  }

  /** The platform returns focus to the opener on close; if it did not, fall back to the Menu button. */
  private restoreFocusFallback(): void {
    queueMicrotask(() => {
      if (this.document.activeElement === this.document.body) this.menuButton()?.nativeElement.focus();
    });
  }
}
