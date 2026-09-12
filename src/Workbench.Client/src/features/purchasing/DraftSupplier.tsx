import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { DraftContent } from '../../api/purchaseOrders';
import { ApiError } from '../../api/auth';
import { getSupplier, type Supplier } from '../../api/suppliers';
import { SupplierDialog } from './SupplierDialog';
import { SupplierEditor } from './SupplierEditor';
import { SupplierList } from './SupplierList';
import { SupplierDetails } from './supplierDetails';
import { copySupplier, supplierSnapshot } from './supplierSnapshot';
interface Props { draft: DraftContent; archived: boolean; frozen: boolean; onChange(draft: DraftContent): void; onAuthLost(): void; onDirtyChange(dirty: boolean, uncertain: boolean): void; }
export function DraftSupplier({ draft, archived, frozen, onChange, onAuthLost, onDirtyChange }: Props) {
  const [previousFrozen, setPreviousFrozen] = useState(frozen);
  const [panel, setPanel] = useState<'choose' | 'new' | 'preview' | 'one-off' | null>(null);
  const [selected, setSelected] = useState<Supplier>();
  const [refreshing, setRefreshing] = useState(false);
  const [message, setMessage] = useState('');
  const [discard, setDiscard] = useState(false);
  const [uncertainSupplier, setUncertainSupplier] = useState(false);
  const [reference, setReference] = useState<'keep' | 'clear' | ''>('');
  // Discard supplier workflows when a draft command captures and freezes its input.
  if (frozen !== previousFrozen) {
    setPreviousFrozen(frozen);
    if (frozen) { setPanel(null); setSelected(undefined); setDiscard(false); setRefreshing(false); }
  }
  const supplierDirty = useRef(false);
  const active = useRef(true); const sequence = useRef(0);
  useLayoutEffect(() => { if (frozen) ++sequence.current; }, [frozen]);
  useEffect(() => { active.current = true; const requests = sequence; return () => { active.current = false; ++requests.current; }; }, []);
  const loseAccess = useCallback(() => { ++sequence.current; setPanel(null); setSelected(undefined); setDiscard(false); setMessage(''); onAuthLost(); }, [onAuthLost]);
  const reportDirty = useCallback((dirty: boolean, uncertain: boolean) => { supplierDirty.current = dirty; setUncertainSupplier(uncertain); onDirtyChange(dirty, uncertain); }, [onDirtyChange]);
  const changingIdentity = panel === 'one-off' || (selected && selected.id !== draft.supplierId);
  const needsReferenceDecision = !!draft.supplierOrderReference && changingIdentity;
  function close() { ++sequence.current; if (panel === 'new' && supplierDirty.current) setDiscard(true); else { setPanel(null); setSelected(undefined); } }
  function enter(workflow: 'choose' | 'new' | 'one-off') { ++sequence.current; setRefreshing(false); setSelected(undefined); setReference(''); setMessage(''); setPanel(workflow); }
  function preview(value: Supplier) { if (frozen) return; ++sequence.current; setRefreshing(false); setSelected(value); setReference(''); setPanel('preview'); setMessage(''); }
  async function refresh() {
    if (frozen || !draft.supplierId || refreshing) return; const generation = ++sequence.current; setRefreshing(true); setMessage('');
    try { const latest = await getSupplier(draft.supplierId); if (active.current && generation === sequence.current) preview(latest); }
    catch (error) { if (!active.current || generation !== sequence.current) return; if (error instanceof ApiError && (error.status === 401 || error.status === 403)) loseAccess(); else setMessage('Current supplier details could not be loaded. Your order details are unchanged.'); }
    finally { if (active.current && generation === sequence.current) setRefreshing(false); }
  }
  function apply() {
    if (frozen || (needsReferenceDecision && !reference)) return;
    const next = panel === 'one-off' ? { ...draft, supplierId: null } : selected ? copySupplier(draft, selected) : draft;
    onChange({ ...next, supplierOrderReference: needsReferenceDecision && reference === 'clear' ? null : draft.supplierOrderReference });
    setPanel(null); setSelected(undefined); setMessage('Supplier details changed locally. Save draft to keep them.');
  }
  return <div className="po-supplier-controls">
    <p className="po-field-help">{draft.supplierId ? `Linked to the supplier directory${archived ? ' · Archived supplier' : ''}. Contact edits here affect only this order.` : 'One-off details. Choose a reusable supplier or enter contact details for this order.'}</p>
    <div className="button-row"><button type="button" className="secondary" disabled={frozen} onClick={() => enter('choose')}>Choose supplier</button><button type="button" className="quiet" disabled={frozen} onClick={() => enter('new')}>New supplier</button>
      {draft.supplierId ? <><button type="button" className="quiet" disabled={frozen || refreshing} onClick={() => void refresh()}>Use current supplier details</button><button type="button" className="quiet" disabled={frozen} onClick={() => enter('one-off')}>Keep details as one-off</button></> : null}
    </div>{refreshing ? <p role="status">Loading supplier details…</p> : null}{message ? <p role="status">{message}</p> : null}
    {panel === 'choose' ? <SupplierDialog title="Choose supplier" cancel={close}><SupplierList onSelect={preview} onAuthLost={loseAccess} /><button className="secondary" type="button" onClick={close}>Cancel</button></SupplierDialog> : null}
    {panel === 'new' ? <SupplierDialog title="New supplier" cancel={close}><SupplierEditor inline onDirtyChange={reportDirty} onAuthLost={loseAccess} onCancel={close} onSelected={preview} /><button className="secondary" type="button" onClick={close}>Cancel</button></SupplierDialog> : null}
    {discard ? <SupplierDialog title="Discard supplier changes?" cancel={() => setDiscard(false)}><p>{uncertainSupplier ? 'The supplier may already have been saved. Leaving loses the in-memory retry request. Check and retry before creating another supplier.' : 'Unsaved supplier edits will be discarded. Any supplier already saved remains in the directory.'}</p><div className="button-row"><button type="button" className="primary" autoFocus onClick={() => setDiscard(false)}>Keep editing supplier</button><button type="button" className="secondary" onClick={() => { setDiscard(false); setPanel(null); }}>Discard supplier changes</button></div></SupplierDialog> : null}
    {panel === 'preview' || panel === 'one-off' ? <SupplierDialog title={panel === 'one-off' ? 'Keep details as one-off?' : 'Review supplier details'} cancel={close}>
      <p>{panel === 'one-off' ? 'Remove the directory link and retain all contact details on this order.' : 'These details will replace the local supplier contact snapshot. Platform stays unchanged. Save the draft separately.'}</p>
      {panel === 'preview' && selected ? <div className="po-comparison"><SupplierDetails heading="On this order" supplier={supplierSnapshot(draft)} /><SupplierDetails heading="Current supplier details" supplier={selected.supplier} archived={selected.isArchived} /></div> : null}
      {needsReferenceDecision ? <fieldset><legend>Supplier order reference: {draft.supplierOrderReference}</legend><p>Choose what to do with the previous supplier’s reference.</p><label className="po-precision-toggle"><input type="radio" name="supplier-reference-decision" checked={reference === 'clear'} onChange={() => setReference('clear')} />Clear supplier order reference</label><label className="po-precision-toggle"><input type="radio" name="supplier-reference-decision" checked={reference === 'keep'} onChange={() => setReference('keep')} />Keep supplier order reference</label></fieldset> : null}
      <div className="button-row"><button className="secondary" type="button" autoFocus onClick={close}>Cancel</button><button className="primary" type="button" disabled={!!needsReferenceDecision && !reference} onClick={apply}>{panel === 'one-off' ? 'Confirm one-off details' : 'Replace supplier details'}</button></div>
    </SupplierDialog> : null}
  </div>;
}
