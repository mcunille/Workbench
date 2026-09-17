import { useEffect, useRef, useState } from 'react';
import { FloatingField } from '../../FloatingField';
import { Icon } from '../../Icon';
import type { DraftContent } from '../../api/purchaseOrders';
import { ReferencePriceField } from './ReferencePriceField';
import { categoryLabel, chargeCategories, financialFieldId, newCharge, type Charge } from './draftFinances';

export function DraftCharges({ draft, baseline, disabled, amountDisabled, errors, change }: {
  draft: DraftContent; baseline?: DraftContent; disabled: boolean; amountDisabled: boolean;
  errors: Record<string, string[]>; change(charges: Charge[]): void;
}) {
  const [removed, setRemoved] = useState<{ charge: Charge; index: number }[]>([]);
  const [boundary, setBoundary] = useState({ disabled, currency: draft.currency });
  const focusId = useRef<string | undefined>(undefined);
  const addButton = useRef<HTMLButtonElement>(null);
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
    <div className="po-section-heading"><div><h3>Additional charges</h3><p>Record each charge once, with who collects it.</p></div><button ref={addButton} type="button" className="secondary" disabled={disabled || draft.charges.length >= 50} onClick={() => { const charge = newCharge(); focusId.current = charge.id; change([...draft.charges, charge]); }}><Icon name="plus" />Add charge</button></div>
    {draft.charges.map((charge, index) => {
      const path = `draft.charges[${index}]`;
      const id = (key: string) => financialFieldId(`${path}.${key}`);
      const error = (key: string) => errors[`${path}.${key}`]?.join(' ');
      const update = (patch: Partial<Charge>) => change(draft.charges.map(old => old.id === charge.id ? { ...old, ...patch } : old));
      const old = baseline?.charges.find(saved => saved.id === charge.id);
      const correction = old?.amountStatus === 'confirmed' && (charge.amount !== old.amount || charge.amountStatus !== old.amountStatus || charge.payeeKind !== old.payeeKind || charge.payeeName !== old.payeeName);
      const field = (key: 'label' | 'payeeName' | 'reference' | 'notes', label: string) => <div className="po-field"><FloatingField htmlFor={id(key)} label={label}>
        {key === 'notes' ? <textarea id={id(key)} aria-label={`${label} ${index + 1}`} rows={3} value={charge[key] ?? ''} disabled={disabled} placeholder=" " aria-invalid={!!error(key)} aria-describedby={error(key) ? `${id(key)}-error` : undefined} onChange={event => update({ [key]: event.target.value || null })} /> : <input id={id(key)} aria-label={`${label} ${index + 1}`} value={charge[key] ?? ''} disabled={disabled} placeholder=" " aria-invalid={!!error(key)} aria-describedby={error(key) ? `${id(key)}-error` : undefined} onChange={event => update({ [key]: key === 'label' ? event.target.value : event.target.value || null })} />}
      </FloatingField>{error(key) ? <p id={`${id(key)}-error`} className="form-message error">{error(key)}</p> : null}</div>;
      return <fieldset className="po-charge" key={charge.id} disabled={disabled} id={financialFieldId(path)}>
        <legend>Charge {index + 1}</legend>
        <div className="po-charge-primary">
          <div className="po-field"><FloatingField htmlFor={id('category')} label="Category"><select id={id('category')} aria-label={`Category ${index + 1}`} value={charge.category} aria-invalid={!!error('category')} onChange={event => { const category = event.target.value; update({ category, label: charge.label === categoryLabel(charge.category) || !charge.label ? category === 'other' ? '' : categoryLabel(category) : charge.label }); }}>
            {chargeCategories.map(([group, values]) => <optgroup key={group} label={group}>{values.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</optgroup>)}
          </select></FloatingField>{error('category') ? <p className="form-message error">{error('category')}</p> : null}</div>
          {field('label', 'Charge label')}
        </div>
        <div className="po-charge-primary">
          <ReferencePriceField id={id('amount')} index={index + 1} label="Charge amount" precisionLabel={`charge ${index + 1}`} value={charge.amount} disabled={amountDisabled} required={charge.amountStatus === 'confirmed'} emptyHint={charge.amountStatus === 'confirmed' ? 'Confirmed charges require an amount. Choose Estimated if it is not yet known.' : undefined} error={error('amount')} onChange={amount => update({ amount })} />
          <div className="po-charge-party">
            <div className="po-field"><FloatingField htmlFor={id('payeeKind')} label="Payee"><select id={id('payeeKind')} aria-label={`Payee ${index + 1}`} value={charge.payeeKind} aria-invalid={!!error('payeeKind')} onChange={event => update({ payeeKind: event.target.value, payeeName: null })}><option value="supplier">Supplier{draft.supplierName ? ` · ${draft.supplierName}` : ''}</option><option value="thirdParty">Third party</option></select></FloatingField>{error('payeeKind') ? <p className="form-message error">{error('payeeKind')}</p> : null}</div>
            {charge.payeeKind === 'thirdParty' ? field('payeeName', 'Payee name') : null}
            <div className="po-field"><FloatingField htmlFor={id('amountStatus')} label="Amount status"><select id={id('amountStatus')} aria-label={`Amount status ${index + 1}`} value={charge.amountStatus} disabled={amountDisabled} aria-invalid={!!error('amountStatus')} onChange={event => update({ amountStatus: event.target.value })}><option value="estimated">Estimated</option><option value="confirmed">Confirmed from source</option></select></FloatingField>{error('amountStatus') ? <p className="form-message error">{error('amountStatus')}</p> : null}</div>
          </div>
        </div>
        {charge.payeeKind === 'thirdParty' ? <p className="po-field-help">Included in the purchase estimate, separate from the supplier estimate.</p> : null}
        <details className="po-charge-details" open={correction || !!error('reference') || !!error('notes') || undefined}><summary>Source and notes{charge.reference ? ` · ${charge.reference}` : ''}</summary><div className="po-charge-primary">{field('reference', 'Supporting reference')}{field('notes', 'Charge notes')}</div>{correction ? <p className="po-field-help">This changes a saved confirmed charge. Append an explanation to its notes before saving.</p> : null}</details>
        <button type="button" className="quiet danger" aria-label={`Remove charge ${index + 1}`} onClick={() => { setRemoved([...removed, { charge, index }]); focusId.current = ''; change(draft.charges.filter(old => old.id !== charge.id)); }}>Remove charge</button>
      </fieldset>;
    })}
    {removed.length ? <div className="po-removal-recovery"><p role="status">Charge removed. Undo is available until you save or change currency.</p><button type="button" className="quiet" disabled={disabled} onClick={() => { const last = removed[removed.length - 1]; const charges = [...draft.charges]; charges.splice(last.index, 0, last.charge); focusId.current = last.charge.id; setRemoved(removed.slice(0, -1)); change(charges); }}>Undo charge removal</button></div> : null}
    {errors['draft.charges'] ? <p id={financialFieldId('draft.charges')} tabIndex={-1} className="form-message error">{errors['draft.charges'].join(' ')}</p> : null}
  </div>;
}
