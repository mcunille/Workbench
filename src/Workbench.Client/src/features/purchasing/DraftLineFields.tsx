import { units, unitLabel, quantityLabel } from './draftLine';
import { useState } from 'react';
import type { DraftEntry } from '../../api/purchaseOrders';
import { FloatingField } from '../../FloatingField';
import { ReferencePriceField } from './ReferencePriceField';
import { formatReferencePrice } from './referencePrice';

export function DraftLineFields({ entry, index, errors, disabled, priceDisabled, change, gross, currency }: {
  entry: DraftEntry; index: number; errors: Record<string, string[]>; disabled: boolean; priceDisabled: boolean;
  change(patch: Partial<DraftEntry>): void; gross?: string | null; currency: string | null;
}) {
  const path = `draft.entries[${index - 1}]`;
  const id = (key: string) => `po-${`${path}.${key}`.replace(/[^a-zA-Z0-9]/g, '-')}`;
  const error = (key: string) => errors[`${path}.${key}`]?.join(' ');
  const [expanded, setExpanded] = useState(false);
  function field(key: keyof DraftEntry, label: string, decimal = false, multiline = false) {
    const common = { id: id(key), value: entry[key] ?? '', disabled, placeholder: ' ', 'aria-label': `${label} ${index}`, 'aria-invalid': !!error(key), 'aria-describedby': error(key) ? `${id(key)}-error` : undefined,
      onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => change({ [key]: event.target.value || null }) };
    return <div className="po-field"><FloatingField htmlFor={id(key)} label={label} compact={decimal}>
      {multiline ? <textarea {...common} rows={2} /> : <input {...common} inputMode={decimal ? 'decimal' : undefined} list={key === 'itemType' ? `${id(key)}-suggestions` : undefined} />}
    </FloatingField>{key === 'itemType' ? <datalist id={`${id(key)}-suggestions`}>{['Gemstone', 'Finding', 'Material', 'Supply', 'Jewelry', 'Other'].map(value => <option key={value} value={value} />)}</datalist> : null}{error(key) ? <p id={`${id(key)}-error`} className="form-message error">{error(key)}</p> : null}</div>;
  }
  function unit(key: 'unitOfMeasure' | 'pricingUnit', label: string) {
    return <div className="po-field po-unit-field"><FloatingField htmlFor={id(key)} label={label} compact>
      <select id={id(key)} aria-label={`${label} ${index}`} value={entry[key] ?? ''} disabled={disabled} aria-invalid={!!error(key)} aria-describedby={error(key) ? `${id(key)}-error` : undefined} onChange={event => {
        const value = event.target.value || null;
        change({ [key]: value });
      }}><option value="">Not set</option>{units.map(([value, title]) => <option key={value} value={value}>{title}</option>)}</select></FloatingField>
      {error(key) ? <p id={`${id(key)}-error`} className="form-message error">{error(key)}</p> : null}</div>;
  }
  const differing = !!entry.unitOfMeasure && !!entry.pricingUnit && entry.unitOfMeasure !== entry.pricingUnit;
  return <>
    {field('description', 'Description')}
    <div className="po-line-quantity">{field('quantity', 'Quantity', true)}{unit('unitOfMeasure', 'Unit')}</div>
    {entry.indicativePrice !== null ? <div className="po-legacy-price">
      <ReferencePriceField id={id('indicativePrice')} index={index} value={entry.indicativePrice} disabled={priceDisabled} error={error('indicativePrice')} onChange={indicativePrice => change({ indicativePrice })} />
      <p>Reference price — basis not recorded</p><button type="button" className="quiet" disabled={priceDisabled} onClick={() => change({ unitPrice: entry.indicativePrice, indicativePrice: null })}>Use as unit price</button>
    </div> : <div className="po-line-pricing">
      <ReferencePriceField id={id('unitPrice')} label="Unit price" index={index} value={entry.unitPrice} disabled={priceDisabled} error={error('unitPrice')} onChange={unitPrice => change({ unitPrice })} />
      {field('pricePerQuantity', 'Per quantity', true)}{unit('pricingUnit', 'Pricing unit')}
    </div>}
    {differing || entry.pricingQuantity !== null ? <div className="po-priced-quantity">
      {field('pricingQuantity', 'Total quantity priced', true)}
      <p className="po-field-help">{differing ? `${unitLabel(entry.pricingUnit)}: enter the total weight or quantity used for this price; it is separate from the ordered quantity.` : 'Clear the separate pricing quantity when the ordered and pricing units match or are not set.'}</p>
    </div> : null}
    <div className="po-line-estimate">
      <span>{gross === undefined ? 'Estimate pending' : gross === null ? 'Line estimate: Unknown' : `${currency} ${formatReferencePrice(gross)}`}</span>
      {gross != null ? <p>{quantityLabel(differing ? entry.pricingQuantity : entry.quantity, entry.pricingUnit)} at {currency} {formatReferencePrice(entry.unitPrice)} per {quantityLabel(entry.pricePerQuantity, entry.pricingUnit)}{differing ? ` · Ordered ${quantityLabel(entry.quantity, entry.unitOfMeasure)}` : ''}</p> : null}
    </div>
    <details className="po-entry-details" open={expanded || ['notes', 'sourceLink', 'supplierSku', 'itemType'].some(key => !!error(key))} onToggle={event => setExpanded(event.currentTarget.open)}>
      <summary onClick={event => { event.preventDefault(); setExpanded(value => !value); }}>Line details{[entry.supplierSku, entry.itemType, entry.notes ? 'Notes' : null, entry.sourceLink ? 'Source link' : null].filter(Boolean).length ? <span className="po-line-metadata">{[entry.supplierSku, entry.itemType, entry.notes ? 'Notes' : null, entry.sourceLink ? 'Source link' : null].filter(Boolean).join(' · ')}</span> : null}</summary><div className="po-entry-secondary">
        {field('supplierSku', 'Supplier SKU')}{field('itemType', 'Item type')}
        {field('notes', 'Line notes', false, true)}{field('sourceLink', 'Line source link')}
      </div>
    </details>
  </>;
}
