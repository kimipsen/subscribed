// Spec-only helper (it lives under `testing`, which Tailwind's source scan and the styles contract skip).
//
// jsdom 27 has no HTMLDialogElement.showModal()/close(). This installs a stand-in on the prototype so a spec that
// renders the app shell can open and close the navigation drawer: it toggles `open`, throws InvalidStateError when
// showModal() is called on an open dialog (as browsers do), and dispatches `close` from close().
//
// It is a simplification. Browsers fire `close` as a queued task, after close() returns, so a stale `close` can arrive
// after the dialog was re-opened; this stub dispatches it synchronously. A spec that needs the late event replaces
// `close` on one element (see the stale-close test in app.spec.ts). The stub also does not trap focus, restore focus
// or handle Escape.

type DialogMethods = Pick<HTMLDialogElement, 'showModal' | 'close'>;

let original: Partial<Record<keyof DialogMethods, unknown>> | undefined;

export function installDialogStub(): void {
  const prototype = HTMLDialogElement.prototype as unknown as Record<string, unknown>;
  original = { showModal: prototype['showModal'], close: prototype['close'] };
  prototype['showModal'] = function (this: HTMLDialogElement) {
    if (this.open) throw new DOMException('The dialog is already open.', 'InvalidStateError');
    this.open = true;
  };
  prototype['close'] = function (this: HTMLDialogElement) {
    if (!this.open) return;
    this.open = false;
    this.dispatchEvent(new Event('close'));
  };
}

export function restoreDialogStub(): void {
  if (!original) return;
  const prototype = HTMLDialogElement.prototype as unknown as Record<string, unknown>;
  for (const name of ['showModal', 'close'] as const) {
    if (original[name] === undefined) delete prototype[name];
    else prototype[name] = original[name];
  }
  original = undefined;
}
