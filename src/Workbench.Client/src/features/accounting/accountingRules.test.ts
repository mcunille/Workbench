import { describe, expect, it } from 'vitest';
import { eligibleForMapping } from './accountingRules';

describe('accounting mapping eligibility', () => {
  it('requires an active account with the exact control purpose and type', () => {
    // GIVEN a supplier payable account and a general liability account
    const account = { type: 'Liability', purpose: 'SupplierPayable', isArchived: false };
    // WHEN the payable mapping is selected THEN only the typed active control qualifies
    expect(eligibleForMapping('SupplierPayable', account)).toBe(true);
    expect(eligibleForMapping('SupplierPayable', { ...account, purpose: 'General' })).toBe(false);
    expect(eligibleForMapping('SupplierPayable', { ...account, isArchived: true })).toBe(false);
    expect(eligibleForMapping('SupplierPayable', { ...account, type: 'Asset' })).toBe(false);
  });
  it('keeps classification candidates separate from control accounts', () => {
    // GIVEN inventory classification WHEN choosing an account THEN it requires a general asset
    expect(eligibleForMapping('Inventory', { type: 'Asset', purpose: 'General', isArchived: false })).toBe(true);
    expect(eligibleForMapping('Inventory', { type: 'Asset', purpose: 'SupplierAdvance', isArchived: false })).toBe(false);
  });
});
