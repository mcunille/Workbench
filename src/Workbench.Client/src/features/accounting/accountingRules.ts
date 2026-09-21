const requirements: Record<string, [string, string]> = {
  SupplierPayable: ['Liability', 'SupplierPayable'], SupplierAdvance: ['Asset', 'SupplierAdvance'],
  SupplierCreditReceivable: ['Asset', 'SupplierCreditReceivable'], SupplierRefundClearing: ['Liability', 'SupplierRefundClearing'],
  Inventory: ['Asset', 'General'], Expense: ['Expense', 'General'], Prepayment: ['Asset', 'General'], RecoverableTax: ['Asset', 'General'],
};
export function eligibleForMapping(slot: string, account: { type: string; purpose: string; isArchived: boolean }): boolean {
  const rule = requirements[slot];
  return !!rule && !account.isArchived && account.type === rule[0] && account.purpose === rule[1];
}
export function label(value: string) { return value.replace(/([a-z])([A-Z])/g, '$1 $2'); }
