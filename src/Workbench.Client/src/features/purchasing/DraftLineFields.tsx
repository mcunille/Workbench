import { units, unitLabel, quantityLabel } from './draftLine';
import { useState } from 'react';
import type { DraftEntry } from '../../api/purchaseOrders';
import { FloatingField } from '../../FloatingField';
import { ReferencePriceField } from './ReferencePriceField';
import { formatReferencePrice } from './referencePrice';
import { LegacyPricingDetails } from './LegacyPricingDetails';
import { DiscountFields } from './DiscountFields';

export function DraftLineFields({ entry, index, errors, disabled, priceDisabled, change, gross, net, discountAmount, currency }: {
  entry: DraftEntry; index: number; errors: Record<string, string[]>; disabled: boolean; priceDisabled: boolean;
  change(patch: Partial<DraftEntry>): void; gross?: string | null; net?: string | null; discountAmount?: string | null; currency: string | null;
}) {
  const path = `draft.entries[${index - 1}]`;
  const id = (key: string) => `po-${`${path}.${key}`.replace(/[^a-zA-Z0-9]/g, '-')}`;
  const error = (key: string) => errors[`${path}.${key}`]?.join(' ');
  const [expanded, setExpanded] = useState(false);
  function field(key: 'description' | 'quantity' | 'supplierSku' | 'itemType' | 'notes' | 'sourceLink', label: string, decimal = false, multiline = false) {
    const common = { id: id(key), value: entry[key] ?? '', disabled, placeholder: ' ', 'aria-label': `${label} ${index}`, 'aria-invalid': !!error(key), 'aria-describedby': error(key) ? `${id(key)}-error` : undefined,
      onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => change({ [key]: event.target.value || null }) };
    return <div className="po-field"><FloatingField htmlFor={id(key)} label={label} compact={decimal}>
      {multiline ? <textarea {...common} rows={2} /> : <input {...common} inputMode={decimal ? 'decimal' : undefined} list={key === 'itemType' ? `${id(key)}-suggestions` : undefined} />}
    </FloatingField>{key === 'itemType' ? <datalist id={`${id(key)}-suggestions`}>{['Gemstone', 'Finding', 'Material', 'Supply', 'Jewelry', 'Other'].map(value => <option key={value} value={value} />)}</datalist> : null}{error(key) ? <p id={`${id(key)}-error`} className="form-message error">{error(key)}</p> : null}</div>;
  }
  function unit(key: 'unitOfMeasure', label: string) {
    return <div className="po-field po-unit-field"><FloatingField htmlFor={id(key)} label={label} compact>
      <select id={id(key)} aria-label={`${label} ${index}`} value={entry[key] ?? ''} disabled={disabled} aria-invalid={!!error(key)} aria-describedby={error(key) ? `${id(key)}-error` : undefined} onChange={event => {
        const value = event.target.value || null;
        change({ [key]: value });
      }}><option value="">Not set</option>{units.map(([value, title]) => <option key={value} value={value}>{title}</option>)}</select></FloatingField>
      {error(key) ? <p id={`${id(key)}-error`} className="form-message error">{error(key)}</p> : null}</div>;
  }
  const legacy = entry.indicativePrice !== null || entry.legacyPricing !== null;
  const setPricing = (priceMode: string) => change({ priceMode });
  const adoptReference = (priceMode: string) => change({ priceMode, price: entry.indicativePrice, indicativePrice: null, legacyPricing: null });
  return <>
    {field('description', 'Description')}
    <div className="po-line-quantity">{field('quantity', 'Quantity', true)}{unit('unitOfMeasure', 'Unit')}</div>
    {entry.indicativePrice !== null ? <div className="po-legacy-price">
      <p>Reference price — basis not recorded</p>
      <p>{currency} {formatReferencePrice(entry.indicativePrice)}</p>
      <div className="button-row">
        <button type="button" className="quiet" disabled={priceDisabled} onClick={() => adoptReference('perUnit')}>Use as unit price</button>
        <button type="button" className="quiet" disabled={priceDisabled} onClick={() => adoptReference('lineTotal')}>Use as total line price</button>
      </div>
      {error('indicativePrice') ? <p className="form-message error">{error('indicativePrice')}</p> : null}
    </div> : null}
    {entry.legacyPricing ? <div className="po-legacy-price">
      <p>Previous pricing needs review</p>
      <LegacyPricingDetails pricing={entry.legacyPricing} currency={currency} />
      <button id={id('legacyPricing')} type="button" className="quiet" disabled={priceDisabled}
        onClick={() => change({ legacyPricing: null, price: null })}>Replace previous pricing</button>
      {error('legacyPricing') ? <p className="form-message error">{error('legacyPricing')}</p> : null}
    </div> : null}
    {!legacy ? <div className="po-line-pricing">
      <fieldset className="po-pricing-mode" disabled={priceDisabled} id={id('priceMode')}>
        <legend>Pricing</legend>
        <div className="po-pricing-options">
          <label><input type="radio" name={id('priceMode')} value="perUnit" checked={entry.priceMode === 'perUnit'}
            aria-label={`Per unit ${index}`} onChange={() => setPricing('perUnit')} />Per unit</label>
          <label><input type="radio" name={id('priceMode')} value="lineTotal" checked={entry.priceMode === 'lineTotal'}
            aria-label={`Total line ${index}`} onChange={() => setPricing('lineTotal')} />Total line</label>
        </div>
        <p className="po-field-help">{entry.priceMode === 'lineTotal' ? 'The supplier’s price for the entire line.' : entry.unitOfMeasure ? `Price for one ${unitLabel(entry.unitOfMeasure).toLowerCase()}.` : 'Choose the supplier’s unit above.'}</p>
        {error('priceMode') ? <p className="form-message error">{error('priceMode')}</p> : null}
      </fieldset>
      <ReferencePriceField id={id('price')} label={entry.priceMode === 'lineTotal' ? 'Total line price' : 'Unit price'}
        index={index} value={entry.price} disabled={priceDisabled} error={error('price')} onChange={price => change({ price })} />
    </div> : null}
    <div className="po-line-estimate">
      <span>{gross === undefined ? 'Estimate pending' : gross === null ? 'Line estimate: Unknown' : `${currency} ${formatReferencePrice(gross)}`}</span>
      {gross != null ? <p>{entry.priceMode === 'lineTotal' ? 'Total line price' : `${quantityLabel(entry.quantity, entry.unitOfMeasure)} × ${currency} ${formatReferencePrice(entry.price)}`}</p> : null}
    </div>
    <DiscountFields label={`Line discount ${index}`} path={`${path}.discount`} discount={entry.discount} base={gross} amount={discountAmount} currency={currency} disabled={priceDisabled} errors={errors} change={discount => change({ discount })} />
    {entry.discount ? <p className="po-line-net">Line net: {net === undefined ? 'Calculating…' : net === null ? 'Unknown' : `${currency} ${formatReferencePrice(net)}`}</p> : null}
    <details className="po-entry-details" open={expanded || ['notes', 'sourceLink', 'supplierSku', 'itemType'].some(key => !!error(key))} onToggle={event => setExpanded(event.currentTarget.open)}>
      <summary onClick={event => { event.preventDefault(); setExpanded(value => !value); }}>Line details{[entry.supplierSku, entry.itemType, entry.notes ? 'Notes' : null, entry.sourceLink ? 'Source link' : null].filter(Boolean).length ? <span className="po-line-metadata">{[entry.supplierSku, entry.itemType, entry.notes ? 'Notes' : null, entry.sourceLink ? 'Source link' : null].filter(Boolean).join(' · ')}</span> : null}</summary><div className="po-entry-secondary">
        {field('supplierSku', 'Supplier SKU')}{field('itemType', 'Item type')}
        {field('notes', 'Line notes', false, true)}{field('sourceLink', 'Line source link')}
      </div>
    </details>
  </>;
}
