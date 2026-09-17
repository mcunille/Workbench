import { useEffect, useRef, useState } from 'react';
import { FloatingField } from '../../FloatingField';
import { Icon } from '../../Icon';
import type { DraftContent } from '../../api/purchaseOrders';
import { ReferencePriceField } from './ReferencePriceField';
import { categoryLabel, chargeCategories, financialFieldId, newCharge, type Charge } from './draftFinances';
import { formatReferencePrice } from './referencePrice';

export function DraftCharges({ draft, baseline, disabled, amountDisabled, errors, change }: {
  draft: DraftContent; baseline?: DraftContent; disabled: boolean; amountDisabled: boolean;
  errors: Record<string, string[]>; change(charges: Charge[]): void;
}) {
  const [removed, setRemoved] = useState<{ charge: Charge; index: number }[]>([]);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [boundary, setBoundary] = useState({ disabled, currency: draft.currency });
  const focusId = useRef<string | undefined>(undefined);
  const addButton = useRef<HTMLButtonElement>(null);
  const invalidCharges = draft.charges.filter((_, index) => Object.keys(errors).some(key => key === `draft.charges[${index}]` || key.startsWith(`draft.charges[${index}].`)));
  if (invalidCharges.some(charge => expanded[charge.id] !== true)) {
    setExpanded(previous => ({ ...previous, ...Object.fromEntries(invalidCharges.map(charge => [charge.id, true])) }));
  }
  if (boundary.disabled !== disabled || boundary.currency !== draft.currency) {
    setBoundary({ disabled, currency: draft.currency });
    if (disabled || boundary.currency !== draft.currency) setRemoved([]);
  }
  useEffect(() => {
    if (focusId.current === undefined) return;
    const index = draft.charges.findIndex(charge => charge.id === focusId.current);
    const target = index < 0 ? addButton.current : document.getElementById(financialFieldId(`draft.charges[${index}].label`));
    target?.focus(); focusId.current = undefined;
  }, [draft.charges]);
  return <div className="po-charges">
    <div className="po-section-heading"><div><h3>Additional charges</h3><p>Enter each charge once and choose who collects it.</p></div><button ref={addButton} type="button" className="secondary" disabled={disabled || draft.charges.length >= 50} onClick={() => { const charge = newCharge(); focusId.current = charge.id; change([...draft.charges, charge]); }}><Icon name="plus" />Add charge</button></div>
    {draft.charges.map((charge, index) => {
      const path = `draft.charges[${index}]`;
      const id = (key: string) => financialFieldId(`${path}.${key}`);
      const error = (key: string) => errors[`${path}.${key}`]?.join(' ');
      const update = (patch: Partial<Charge>) => change(draft.charges.map(old => old.id === charge.id ? { ...old, ...patch } : old));
      const old = baseline?.charges.find(saved => saved.id === charge.id);
      const invalid = Object.keys(errors).some(key => key === path || key.startsWith(`${path}.`));
      const open = (expanded[charge.id] ?? !old) || invalid;
      const correction = old?.amountStatus === 'confirmed' && (charge.amount !== old.amount || charge.amountStatus !== old.amountStatus || charge.payeeKind !== old.payeeKind || charge.payeeName !== old.payeeName);
      const field = (key: 'label' | 'payeeName' | 'reference' | 'notes', label: string) => <div className="po-field"><FloatingField htmlFor={id(key)} label={label}>
        {key === 'notes' ? <textarea id={id(key)} aria-label={`${label} ${index + 1}`} rows={3} value={charge[key] ?? ''} disabled={disabled} placeholder=" " aria-invalid={!!error(key)} aria-describedby={error(key) ? `${id(key)}-error` : undefined} onChange={event => update({ [key]: event.target.value || null })} /> : <input id={id(key)} aria-label={`${label} ${index + 1}`} value={charge[key] ?? ''} disabled={disabled} placeholder=" " aria-invalid={!!error(key)} aria-describedby={error(key) ? `${id(key)}-error` : undefined} onChange={event => update({ [key]: key === 'label' ? event.target.value : event.target.value || null })} />}
      </FloatingField>{error(key) ? <p id={`${id(key)}-error`} className="form-message error">{error(key)}</p> : null}</div>;
      return <details className="po-line-disclosure po-charge-disclosure" key={charge.id} open={open}
        onInvalidCapture={() => setExpanded(previous => ({ ...previous, [charge.id]: true }))}>
        <summary aria-label={`Edit charge ${index + 1}: ${charge.label.trim() || 'Untitled charge'}`}
          onClick={event => { event.preventDefault(); setExpanded(previous => ({ ...previous, [charge.id]: !open })); }}>
          <Icon name="chevron" />
          <span className="po-line-summary-copy">
            <span className="po-line-title">{charge.label.trim() || 'Untitled charge'}</span>
            <span className="po-line-summary-quantity">{charge.payeeKind === 'supplier' ? 'Supplier' : charge.payeeName?.trim() || 'Third party'} · {charge.amountStatus === 'confirmed' ? 'Confirmed from source' : 'Estimated'}</span>
          </span>
          <span className="po-line-summary-price">{invalid ? 'Review charge' : charge.amount == null ? 'Unknown' : `${draft.currency ?? ''} ${formatReferencePrice(charge.amount)}`}</span>
        </summary>
        <fieldset className="po-charge" disabled={disabled} id={financialFieldId(path)} aria-label={`Charge ${index + 1} details`}>
        <div className="po-charge-primary">
          <div className="po-field"><FloatingField htmlFor={id('category')} label="Category"><select id={id('category')} aria-label={`Category ${index + 1}`} value={charge.category} aria-invalid={!!error('category')} aria-describedby={error('category') ? `${id('category')}-error` : undefined} onChange={event => { const category = event.target.value; update({ category, label: charge.label === categoryLabel(charge.category) || !charge.label ? category === 'other' ? '' : categoryLabel(category) : charge.label }); }}>
            {chargeCategories.map(([group, values]) => <optgroup key={group} label={group}>{values.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</optgroup>)}
          </select></FloatingField>{error('category') ? <p id={`${id('category')}-error`} className="form-message error">{error('category')}</p> : null}</div>
          {field('label', 'Charge label')}
        </div>
        <div className="po-charge-primary">
          <ReferencePriceField id={id('amount')} index={index + 1} label="Charge amount" precisionLabel={`charge ${index + 1}`} value={charge.amount} disabled={amountDisabled} required={charge.amountStatus === 'confirmed'} emptyHint={charge.amountStatus === 'confirmed' ? 'Confirmed charges require an amount. Choose Estimated if it is not yet known.' : undefined} error={error('amount')} onChange={amount => update({ amount })} />
          <div className="po-field"><FloatingField htmlFor={id('amountStatus')} label="Amount status"><select id={id('amountStatus')} aria-label={`Amount status ${index + 1}`} value={charge.amountStatus} disabled={amountDisabled} aria-invalid={!!error('amountStatus')} aria-describedby={error('amountStatus') ? `${id('amountStatus')}-error` : undefined} onChange={event => update({ amountStatus: event.target.value })}><option value="estimated">Estimated</option><option value="confirmed">Confirmed from source</option></select></FloatingField>{error('amountStatus') ? <p id={`${id('amountStatus')}-error`} className="form-message error">{error('amountStatus')}</p> : null}</div>
        </div>
        <div className="po-charge-primary po-charge-party">
          <div className="po-field"><FloatingField htmlFor={id('payeeKind')} label="Payee"><select id={id('payeeKind')} aria-label={`Payee ${index + 1}`} value={charge.payeeKind} aria-invalid={!!error('payeeKind')} aria-describedby={error('payeeKind') ? `${id('payeeKind')}-error` : undefined} onChange={event => update({ payeeKind: event.target.value, payeeName: null })}><option value="supplier">Supplier{draft.supplierName ? ` · ${draft.supplierName}` : ''}</option><option value="thirdParty">Third party</option></select></FloatingField>{error('payeeKind') ? <p id={`${id('payeeKind')}-error`} className="form-message error">{error('payeeKind')}</p> : null}</div>
          {charge.payeeKind === 'thirdParty' ? field('payeeName', 'Payee name') : null}
        </div>
        {charge.payeeKind === 'thirdParty' ? <p className="po-field-help">Included in the purchase estimate, separate from the supplier estimate.</p> : null}
        <details className="po-charge-details" open={correction || !!error('reference') || !!error('notes') || undefined}><summary>Source and notes{charge.reference ? ` · ${charge.reference}` : ''}</summary><div className="po-charge-primary">{field('reference', 'Supporting reference')}{field('notes', 'Charge notes')}</div>{correction ? <p className="po-field-help">You changed a confirmed charge. Add an explanation to Charge notes before saving.</p> : null}</details>
        <button type="button" className="quiet danger" aria-label={`Remove charge ${index + 1}`} onClick={() => { setRemoved([...removed, { charge, index }]); focusId.current = ''; change(draft.charges.filter(old => old.id !== charge.id)); }}>Remove charge</button>
        </fieldset>
      </details>;
    })}
    {removed.length ? <div className="po-removal-recovery"><p role="status">Charge removed. Undo is available until you save or change currency.</p><button type="button" className="quiet" disabled={disabled} onClick={() => { const last = removed[removed.length - 1]; const charges = [...draft.charges]; charges.splice(last.index, 0, last.charge); focusId.current = last.charge.id; setExpanded(previous => ({ ...previous, [last.charge.id]: true })); setRemoved(removed.slice(0, -1)); change(charges); }}>Undo charge removal</button></div> : null}
    {errors['draft.charges'] ? <p id={financialFieldId('draft.charges')} tabIndex={-1} className="form-message error">{errors['draft.charges'].join(' ')}</p> : null}
  </div>;
}
