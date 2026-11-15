import { AddSubscriptionBody, UpdateSubscriptionBody } from './subscription.models';

describe('request body contracts', () => {
  it('requires catalogId on the update body, so an edit can never silently unlink (D-10, P7)', () => {
    // @ts-expect-error catalogId is required: omitting it must not compile.
    const withoutLink: UpdateSubscriptionBody = { name: 'x', category: null, notes: null, currency: 'DKK', startDate: '2026-01-01', endDate: null };
    const withLink: UpdateSubscriptionBody = { ...withoutLink, catalogId: null };
    expect(withLink.catalogId).toBeNull();
  });

  it('requires an explicit catalogId on the add body', () => {
    // @ts-expect-error catalogId is required: a free-form add sends an explicit null.
    const withoutLink: AddSubscriptionBody = {
      name: 'x', category: null, notes: null, currency: 'DKK', startDate: '2026-01-01', endDate: null, amount: '1.00', cadenceUnit: 'month', cadenceCount: 1,
    };
    expect(withoutLink).toBeDefined();
  });
});
