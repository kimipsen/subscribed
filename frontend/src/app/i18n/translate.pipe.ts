import { Pipe, PipeTransform, inject } from '@angular/core';
import { LanguageService, MessageKey, MessageParams } from './i18n';

@Pipe({ name: 't', pure: false })
export class TranslatePipe implements PipeTransform {
  private readonly i18n = inject(LanguageService);

  transform(key: MessageKey, params?: MessageParams): string {
    return this.i18n.translate(key, params);
  }
}
