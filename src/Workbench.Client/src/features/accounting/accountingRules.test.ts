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
  it('allows only active general liabilities for receipt accrual', () => {
    // GIVEN a general liability, an asset, and an archived liability
    const account = { type: 'Liability', purpose: 'General', isArchived: false };
    // WHEN choosing the receipt-accrual mapping
    // THEN only the active general liability is eligible
    expect(eligibleForMapping('GoodsReceivedNotInvoiced', account)).toBe(true);
    expect(eligibleForMapping('GoodsReceivedNotInvoiced', { ...account, type: 'Asset' })).toBe(false);
    expect(eligibleForMapping('GoodsReceivedNotInvoiced', { ...account, isArchived: true })).toBe(false);
  });
});
