import { vi } from 'vitest';
import { defaultIsVisible, focusVisible } from './focus-visible-target';

describe('focusVisible', () => {
  let host: HTMLElement;

  beforeEach(() => {
    host = document.createElement('div');
    document.body.appendChild(host);
  });
  afterEach(() => host.remove());

  const render = (html: string) => {
    host.innerHTML = html;
    return host;
  };

  it('focuses a plainly rendered copy with the default predicate', () => {
    render('<button id="only" data-focus-key="action-1">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(true);
    expect(document.activeElement?.id).toBe('only');
  });

  it('focuses the first copy when both are present and the first is visible', () => {
    render('<button id="table" data-focus-key="action-1">Pause</button><button id="card" data-focus-key="action-1">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(true);
    expect(document.activeElement?.id).toBe('table');
  });

  it('focuses the second copy when the first has the hidden attribute', () => {
    render('<button id="table" hidden data-focus-key="action-1">Pause</button><button id="card" data-focus-key="action-1">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(true);
    expect(document.activeElement?.id).toBe('card');
  });

  it('focuses the second copy when the first sits inside a [hidden] ancestor', () => {
    render('<section hidden><button id="table" data-focus-key="action-1">Pause</button></section><button id="card" data-focus-key="action-1">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(true);
    expect(document.activeElement?.id).toBe('card');
  });

  it('returns false when every copy is hidden', () => {
    render('<button hidden data-focus-key="action-1">Pause</button><section hidden><button data-focus-key="action-1">Pause</button></section>');
    expect(focusVisible(document, 'action-1')).toBe(false);
    expect(document.activeElement).toBe(document.body);
  });

  it('returns false when nothing matches the key', () => {
    render('<button data-focus-key="action-2">Pause</button>');
    expect(focusVisible(document, 'action-1')).toBe(false);
    expect(document.activeElement).toBe(document.body);
  });

  it('escapes keys that are not valid CSS identifiers', () => {
    render('<button id="odd" data-focus-key="a&quot;]b">Odd</button>');
    expect(focusVisible(document, 'a"]b')).toBe(true);
    expect(document.activeElement?.id).toBe('odd');
  });

  it('uses an injected predicate instead of the default', () => {
    render('<button id="first" data-focus-key="action-1">Pause</button><button id="second" data-focus-key="action-1">Pause</button>');
    const isVisible = vi.fn((element: HTMLElement) => element.id === 'second');
    expect(focusVisible(host, 'action-1', isVisible)).toBe(true);
    expect(isVisible).toHaveBeenCalledTimes(2);
    expect(document.activeElement?.id).toBe('second');
  });

  it('treats an element as hidden when checkVisibility reports false', () => {
    render('<button id="x" data-focus-key="k">x</button>');
    const element = host.querySelector<HTMLElement>('#x')!;
    element.checkVisibility = () => false;
    expect(defaultIsVisible(element)).toBe(false);
    element.checkVisibility = () => true;
    expect(defaultIsVisible(element)).toBe(true);
  });
});
