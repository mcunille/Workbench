import { useState, type ReactNode } from 'react';
import type { DraftEntry } from '../../api/purchaseOrders';
import { quantityLabel } from './draftLine';
import { formatReferencePrice } from './referencePrice';
import { Icon } from '../../Icon';

export function DraftLine({ entry, index, initialOpen, invalid, gross, currency, children }: {
  entry: DraftEntry; index: number; initialOpen: boolean; invalid: boolean;
  gross?: string | null; currency: string | null; children: ReactNode;
}) {
  const [expanded, setExpanded] = useState(initialOpen);
  return <details className="po-line-disclosure" open={expanded || invalid} onToggle={event => setExpanded(event.currentTarget.open)}>
    <summary aria-label={`Edit line ${index}: ${entry.description?.trim() || `Line ${index}`}`}
      onClick={event => { event.preventDefault(); setExpanded(value => !value); }}>
      <Icon name="chevron" />
      <span className="po-line-summary-copy">
        <span id={`po-entry-title-${entry.id}`} className="po-line-title">{entry.description?.trim() || `Line ${index}`}</span>
        <span className="po-line-summary-quantity">{quantityLabel(entry.quantity, entry.unitOfMeasure)}</span>
        {entry.discount ? <span className="po-line-summary-quantity">Net after {entry.discount.mode === 'percentage' ? `${entry.discount.value}%` : `${currency} ${formatReferencePrice(entry.discount.value)}`} discount</span> : null}
      </span>
      <span className="po-line-summary-price">{invalid ? 'Review line' : gross === undefined ? 'Estimate pending' : gross === null ? 'Estimate unknown' : `${currency} ${formatReferencePrice(gross)}`}</span>
    </summary>
    {children}
  </details>;
}
