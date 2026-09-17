import { useRef, useEffect } from 'react';
import { FloatingField } from '../../FloatingField';
import { ReferencePriceField } from './ReferencePriceField';
import { formatReferencePrice } from './referencePrice';
import { financialFieldId, type Discount } from './draftFinances';

export function DiscountFields({ label, path, discount, base, amount, currency, disabled, errors, change }: {
  label: string; path: string; discount: Discount | null; base?: string | null; amount?: string | null;
  currency: string | null; disabled: boolean; errors: Record<string, string[]>; change(value: Discount | null): void;
}) {
  const added = useRef(false);
  const valueId = financialFieldId(`${path}.value`);
  const button = useRef<HTMLButtonElement>(null);
  useEffect(() => { if (added.current && discount) { document.getElementById(valueId)?.focus(); added.current = false; } }, [discount, valueId]);
  if (!discount) return <button ref={button} type="button" className="quiet" disabled={disabled} aria-label={`Add ${label.toLowerCase()}`} onClick={() => { added.current = true; change({ mode: 'fixed', value: '' }); }}>Add {label.startsWith('Line') ? 'discount' : 'order discount'}</button>;
  const error = errors[`${path}.value`]?.join(' ');
  const modeId = financialFieldId(`${path}.mode`);
  return <fieldset className="po-discount" disabled={disabled} id={financialFieldId(path)}>
    <legend>{label}</legend>
    <div className="po-adjustment-fields">
      <div className="po-field"><FloatingField htmlFor={modeId} label="Discount type" compact><select id={modeId} aria-label={`${label} type`} value={discount.mode} aria-invalid={!!errors[`${path}.mode`]} aria-describedby={errors[`${path}.mode`] ? `${modeId}-error` : undefined} onChange={event => change({ mode: event.target.value, value: '' })}><option value="fixed">Fixed amount</option><option value="percentage">Percentage</option></select></FloatingField>{errors[`${path}.mode`] ? <p id={`${modeId}-error`} className="form-message error">{errors[`${path}.mode`].join(' ')}</p> : null}</div>
      {discount.mode === 'percentage' ? <div className="po-field"><FloatingField htmlFor={valueId} label="Percentage" compact><input id={valueId} aria-label={`${label} percentage`} inputMode="decimal" placeholder=" " value={discount.value} required aria-invalid={!!error} aria-describedby={error ? `${valueId}-error` : undefined} onChange={event => change({ ...discount, value: event.target.value })} /></FloatingField>{error ? <p className="form-message error" id={`${valueId}-error`}>{error}</p> : null}</div> : <ReferencePriceField id={valueId} label={`${label} amount`} value={discount.value || null} disabled={disabled} required emptyHint="Enter an amount, or remove this discount." error={error} onChange={value => change({ ...discount, value: value ?? '' })} />}
    </div>
    <p className="po-field-help">{base === undefined ? 'Calculating discount…' : base === null ? 'Enter the missing line prices or quantities to calculate this discount.' : `Discount applies to: ${currency} ${formatReferencePrice(base)}${amount == null ? '' : ` · Reduction: ${currency} ${formatReferencePrice(amount)}`}`}{label === 'Order discount' ? ' Applies to merchandise after line discounts, excluding all charges.' : ' Applies to this line before discounts.'}</p>
    <button type="button" className="quiet danger" onClick={() => { change(null); requestAnimationFrame(() => button.current?.focus()); }}>Remove {label.startsWith('Line') ? 'line discount' : 'order discount'}</button>
  </fieldset>;
}
