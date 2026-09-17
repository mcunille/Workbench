import type { DraftCalculation } from '../api/purchaseOrders';

/** Complete server-shaped calculation for fixtures without discounts or charges. */
export function zeroAdjustmentCalculation(input: {
  lines: { id: string; gross: string | null }[];
  incompleteLineCount: number;
  merchandiseEstimate: string | null;
}): DraftCalculation {
  const net = input.lines.length > 0 && input.incompleteLineCount === 0 ? input.merchandiseEstimate : null;
  return {
    ...input,
    lines: input.lines.map(line => ({ ...line, discountBase: line.gross, discountAmount: line.gross === null ? null : '0.0000', net: line.gross })),
    lineDiscountTotal: net === null ? null : '0.0000',
    merchandiseNet: net,
    orderDiscountBase: net,
    orderDiscountAmount: net === null ? null : '0.0000',
    discountedMerchandise: net,
    supplierCharges: '0.0000',
    thirdPartyCharges: '0.0000',
    supplierEstimate: net,
    purchaseEstimate: net,
    incompleteChargeCount: 0,
  };
}
