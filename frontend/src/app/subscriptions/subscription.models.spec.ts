import { AddSubscriptionBody, UpdateSubscriptionBody } from './subscription.models';

describe('request body contracts', () => {
  it('requires catalogId on the update body, so an edit can never silently unlink (D-10, P7)', () => {
    // @ts-expect-error catalogId is required: omitting it must not compile.
    const withoutLink: UpdateSubscriptionBody = { name: 'x', category: null, notes: null, currency: 'DKK', startDate: '2026-01-01', endDate: null, unsubscribeUrl: null };
    const withLink: UpdateSubscriptionBody = { ...withoutLink, catalogId: null };
    expect(withLink.catalogId).toBeNull();
  });

  it('requires unsubscribeUrl on the update body, so an edit can never silently clear it', () => {
    // @ts-expect-error unsubscribeUrl is required: omitting it must not compile.
    const withoutUrl: UpdateSubscriptionBody = { name: 'x', category: null, notes: null, currency: 'DKK', startDate: '2026-01-01', endDate: null, catalogId: null };
    expect(withoutUrl).toBeDefined();
  });

  it('requires an explicit catalogId on the add body', () => {
    // @ts-expect-error catalogId is required: a free-form add sends an explicit null.
    const withoutLink: AddSubscriptionBody = {
      name: 'x', category: null, notes: null, currency: 'DKK', startDate: '2026-01-01', endDate: null, amount: '1.00', cadenceUnit: 'month', cadenceCount: 1, unsubscribeUrl: null,
    };
    expect(withoutLink).toBeDefined();
  });

  it('requires cancelBy on the update body, so an edit can never silently clear the deadline', () => {
    // @ts-expect-error cancelBy is required: omitting it must not compile.
    const withoutDeadline: UpdateSubscriptionBody = { name: 'x', category: null, notes: null, currency: 'DKK', startDate: '2026-01-01', endDate: null, catalogId: null, unsubscribeUrl: null };
    expect(withoutDeadline).toBeDefined();
  });

  it('requires an explicit cancelBy on the add body', () => {
    // @ts-expect-error cancelBy is required: an add without a deadline sends an explicit null.
    const withoutDeadline: AddSubscriptionBody = {
      name: 'x', category: null, notes: null, currency: 'DKK', startDate: '2026-01-01', endDate: null, amount: '1.00', cadenceUnit: 'month', cadenceCount: 1, catalogId: null, paymentOptionId: null, unsubscribeUrl: null,
    };
    expect(withoutDeadline).toBeDefined();
  });
});
