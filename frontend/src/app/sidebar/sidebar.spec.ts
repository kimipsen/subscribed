import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { SIDEBAR_STORAGE, SIDEBAR_STORAGE_KEY, SidebarService } from './sidebar';

class FakeStorage {
  readonly values = new Map<string, string>();
  readonly getItem = vi.fn((key: string) => this.values.get(key) ?? null);
  readonly setItem = vi.fn((key: string, value: string) => void this.values.set(key, value));
}

describe('SidebarService', () => {
  let storage: FakeStorage;

  beforeEach(() => {
    storage = new FakeStorage();
  });

  function create(store: Pick<Storage, 'getItem' | 'setItem'> | null = storage): SidebarService {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: SIDEBAR_STORAGE, useValue: store }] });
    return TestBed.inject(SidebarService);
  }

  it('uses the documented storage key', () => {
    expect(SIDEBAR_STORAGE_KEY).toBe('subscribed.sidebar');
  });

  it('starts expanded and does not write on construction', () => {
    const service = create();
    expect(service.collapsed()).toBe(false);
    expect(storage.setItem).not.toHaveBeenCalled();
  });

  it('restores a stored collapsed state', () => {
    storage.values.set(SIDEBAR_STORAGE_KEY, 'collapsed');
    expect(create().collapsed()).toBe(true);
  });

  it('restores a stored expanded state', () => {
    storage.values.set(SIDEBAR_STORAGE_KEY, 'expanded');
    expect(create().collapsed()).toBe(false);
  });

  it.each(['true', '1', '', 'Collapsed', 'COLLAPSED', ' collapsed', 'collapsed ', 'open', '{}'])(
    'treats the stored value %j as expanded',
    (value) => {
      storage.values.set(SIDEBAR_STORAGE_KEY, value);
      expect(create().collapsed()).toBe(false);
    },
  );

  it('toggle writes collapsed, then expanded', () => {
    const service = create();
    service.toggle();
    expect(service.collapsed()).toBe(true);
    expect(storage.setItem).toHaveBeenLastCalledWith(SIDEBAR_STORAGE_KEY, 'collapsed');
    service.toggle();
    expect(service.collapsed()).toBe(false);
    expect(storage.setItem).toHaveBeenLastCalledWith(SIDEBAR_STORAGE_KEY, 'expanded');
    expect(storage.setItem).toHaveBeenCalledTimes(2);
  });

  it('a toggled state is restored by a new service on the same storage', () => {
    create().toggle();
    expect(create().collapsed()).toBe(true);
  });

  it('starts expanded without throwing when getItem throws', () => {
    storage.getItem.mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    let service!: SidebarService;
    expect(() => (service = create())).not.toThrow();
    expect(service.collapsed()).toBe(false);
  });

  it('still flips the state when setItem throws', () => {
    storage.setItem.mockImplementation(() => {
      throw new DOMException('full', 'QuotaExceededError');
    });
    const service = create();
    expect(() => service.toggle()).not.toThrow();
    expect(service.collapsed()).toBe(true);
    expect(() => service.toggle()).not.toThrow();
    expect(service.collapsed()).toBe(false);
  });

  it('works without storage', () => {
    const service = create(null);
    expect(service.collapsed()).toBe(false);
    expect(() => service.toggle()).not.toThrow();
    expect(service.collapsed()).toBe(true);
  });

  it('exposes a read-only signal', () => {
    const service = create();
    expect('set' in service.collapsed).toBe(false);
  });
});

describe('SIDEBAR_STORAGE factory', () => {
  function inject(doc: unknown) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: DOCUMENT, useValue: doc }] });
    return TestBed.inject(SIDEBAR_STORAGE);
  }

  it('returns null when reading defaultView.localStorage throws', () => {
    const view = {
      get localStorage(): Storage {
        throw new DOMException('denied', 'SecurityError');
      },
    };
    expect(inject({ defaultView: view })).toBeNull();
  });

  it('returns null when there is no defaultView', () => {
    expect(inject({ defaultView: null })).toBeNull();
  });

  it('returns the window localStorage when it is readable', () => {
    const local = new FakeStorage();
    expect(inject({ defaultView: { localStorage: local } })).toBe(local);
  });
});
