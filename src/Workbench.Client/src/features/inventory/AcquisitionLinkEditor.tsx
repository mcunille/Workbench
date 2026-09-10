import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { getItem, ItemValidationError, type ItemDetail } from '../../api/items';
import { AcquisitionConflictError, getAcquisition, getSharedAcquisition, saveAcquisitionLink,
  type Acquisition, type AcquisitionContext, type LinkAcquisitionCommand } from '../../api/acquisitions';
import { AcquisitionValues } from './AcquisitionFields';
import { fields } from './acquisitionDraft';

export function AcquisitionLinkEditor({ item, initial, target, newlySaved = false, onClose, onDirtyChange, onAuthLost }: {
  item: ItemDetail;
  initial: AcquisitionContext;
  target: Acquisition | null;
  newlySaved?: boolean;
  onClose(context?: AcquisitionContext, currentItem?: ItemDetail): void;
  onDirtyChange(dirty: boolean, uncertain: boolean): void;
  onAuthLost(): void;
}) {
  const [base, setBase] = useState(initial);
  const [destination, setDestination] = useState(target);
  const [submitted, setSubmitted] = useState<LinkAcquisitionCommand>();
  const [pending, setPending] = useState(false);
  const [review, setReview] = useState(false);
  const [current, setCurrent] = useState<{ context: AcquisitionContext; item: ItemDetail; target: Acquisition | null }>();
  const [message, setMessage] = useState('');
  const alive = useRef(true);
  const busy = useRef(false);
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    alive.current = true; heading.current?.focus();
    return () => { alive.current = false; };
  }, []);
  useEffect(() => {
    onDirtyChange(true, Boolean(submitted));
    return () => onDirtyChange(false, false);
  }, [submitted, onDirtyChange]);
  useEffect(() => { heading.current?.focus(); }, [review, current]);
  function authFailure(error: unknown) {
    if (error instanceof ApiError && (error.status === 401 || error.status === 403)) { onAuthLost(); return true; }
    return false;
  }
  async function loadCurrent() {
    setReview(true); setCurrent(undefined); setMessage('');
    try {
      const [context, savedItem, savedTarget] = await Promise.all([
        getAcquisition(item.id), getItem(item.id), destination ? getSharedAcquisition(destination.id) : Promise.resolve(null),
      ]);
      if (alive.current) setCurrent({ context, item: savedItem, target: savedTarget });
    } catch (error) {
      if (alive.current && !authFailure(error)) setMessage('We could not load the current connection. Your intended connection is kept. Try loading again.');
    }
  }
  async function reviewCurrent() {
    if (busy.current) return;
    busy.current = true; setPending(true);
    await loadCurrent();
    busy.current = false; if (alive.current) setPending(false);
  }
  async function save() {
    if (busy.current || review) return;
    busy.current = true; setPending(true); setMessage('');
    const command = submitted ?? {
      expectedItemVersion: base.itemVersion,
      expectedAcquisitionId: base.acquisition?.id ?? null,
      expectedAcquisitionVersion: base.acquisition?.version ?? null,
      targetAcquisitionId: destination?.id ?? null,
      targetAcquisitionVersion: destination?.version ?? null,
    };
    setSubmitted(command);
    try {
      const saved = await saveAcquisitionLink(item.id, command);
      const savedItem = await getItem(item.id);
      if (alive.current) onClose(saved, savedItem);
    } catch (error) {
      if (!alive.current || authFailure(error)) return;
      if (error instanceof AcquisitionConflictError) await loadCurrent();
      else if (error instanceof ItemValidationError) {
        setSubmitted(undefined); setMessage(Object.values(error.errors).flat().join(' '));
      } else setMessage(newlySaved
        ? 'Piece saved; acquisition connection not confirmed. Retry only this connection save, or review the current connection.'
        : 'We could not confirm whether the connection was saved. Retry this exact save, or review the current connection.');
    } finally { busy.current = false; if (alive.current) setPending(false); }
  }
  return <section aria-labelledby="connection-editor-title">
    <h3 id="connection-editor-title" tabIndex={-1} ref={heading}>{review ? 'Review current connection' : destination ? 'Review connection' : 'Remove connection'}</h3>
    <p>Piece: <strong>{item.name}</strong> <span className="identifier">({item.id})</span></p>
    {newlySaved ? <p role="status">Piece saved. Save its acquisition connection separately.</p> : null}
    <div className="edit-comparison">
      <section><h4>{review ? 'Current saved connection' : 'Current connection'}</h4>
        {review && !current ? <p>Current connection has not loaded.</p> : (review ? current?.context : base)?.acquisition
          ? <AcquisitionValues value={fields((review ? current!.context : base).acquisition)} /> : <p>No acquisition connected.</p>}
      </section>
      <section><h4>Intended connection</h4>{destination ? <AcquisitionValues value={fields(destination)} /> : <p>No acquisition connected.</p>}
        {destination ? <p className="identifier">{destination.id}</p> : null}
      </section>
    </div>
    {!destination ? <p>Remove the connection between {item.name} and {base.acquisition?.source ?? base.acquisition?.id ?? 'this acquisition'}. Both the piece and acquisition remain saved, even when this is the last connection.</p> : <p>This replaces any current acquisition in one save. Both acquisition records remain saved.</p>}
    {review ? <>
      <p>The record may have changed. Matching current and intended connections does not establish whether the earlier request saved. Review before making another save.</p>
      {current?.target ? <section><h4>Current target acquisition</h4><AcquisitionValues value={fields(current.target)} /></section> : null}
      {current?.item.archivedAtUtc ? <p role="status">This piece is archived and read-only. Your intended connection is kept for reference.</p> : null}
      <div className="button-row">{current ? <>
        <button className="secondary" disabled={pending} onClick={() => onClose(current.context, current.item)}>Use saved connection</button>
        {!current.item.archivedAtUtc ? <button className="primary" disabled={pending} onClick={() => {
          setBase(current.context); setDestination(current.target); setSubmitted(undefined); setReview(false); setMessage('');
        }}>Review intended connection with current versions</button> : null}
      </> : <button className="secondary" disabled={pending} onClick={() => void reviewCurrent()}>Retry loading current connection</button>}</div>
    </> : <div className="button-row">
      <button className={destination ? 'primary' : 'secondary danger'} disabled={pending} onClick={() => void save()}>{submitted ? 'Retry connection save' : destination ? 'Save connection' : 'Remove connection'}</button>
      {submitted ? <button className="secondary" disabled={pending} onClick={() => void reviewCurrent()}>Review current connection</button>
        : <button className="secondary" onClick={() => onClose()}>Cancel</button>}
    </div>}
    {pending ? <p role="status">{review ? 'Loading current connection…' : 'Saving connection…'}</p> : null}
    {message ? <p role="alert">{message}</p> : null}
  </section>;
}
