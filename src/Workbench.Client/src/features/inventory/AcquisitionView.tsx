import { useEffect, useRef, useState, type MouseEvent } from 'react';
import { ApiError } from '../../api/auth';
import { getItem, type ItemDetail } from '../../api/items';
import { getAcquisition, getAcquisitionItems, getSharedAcquisition, type Acquisition, type AcquisitionContext, type AcquisitionItems } from '../../api/acquisitions';
import { AcquisitionValues } from './AcquisitionFields';
import { fields } from './acquisitionDraft';
import { AddItem } from './AddItem';
import { AcquisitionEditor } from './AcquisitionEditor';
import { AcquisitionLinkEditor } from './AcquisitionLinkEditor';
import { ExistingPiecePicker } from './ExistingPiecePicker';
import { AcquisitionDocumentsPanel } from './AcquisitionDocumentsPanel';

export function AcquisitionView({ id, originId, collectionOrigin, follow, onDirtyChange, onAuthLost, onItemSaved }: {
  id: string;
  originId: string;
  collectionOrigin?: 'active' | 'archived';
  follow(event: MouseEvent<HTMLAnchorElement>): void;
  onDirtyChange(dirty: boolean, uncertain: boolean): void;
  onAuthLost(): void;
  onItemSaved(): void;
}) {
  const [value, setValue] = useState<Acquisition>();
  const [origin, setOrigin] = useState<ItemDetail>();
  const [page, setPage] = useState<AcquisitionItems>();
  const [archived, setArchived] = useState<boolean>();
  const [attempt, setAttempt] = useState(0);
  const [failed, setFailed] = useState(false);
  const [piecesFailed, setPiecesFailed] = useState(false);
  const [pending, setPending] = useState(false);
  const [documentsEditing, setDocumentsEditing] = useState(false);
  const [mode, setMode] = useState<'view' | 'existing' | 'new' | 'link' | 'edit' | 'saved'>('view');
  const [selected, setSelected] = useState<{ item: ItemDetail; context: AcquisitionContext }>();
  const [savedNew, setSavedNew] = useState<ItemDetail>();
  const [message, setMessage] = useState('');
  const heading = useRef<HTMLHeadingElement>(null);
  const active = useRef(true);
  const pieceGeneration = useRef(0);
  const busy = useRef(false);
  const actionButton = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    active.current = true; heading.current?.focus();
    return () => { active.current = false; };
  }, []);
  useEffect(() => {
    let current = true;
    void Promise.all([getSharedAcquisition(id), getItem(originId)]).then(([acquisition, item]) => {
      if (!current) return;
      setValue(acquisition); setOrigin(item); setFailed(false);
    }, error => {
      if (!current) return;
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) onAuthLost();
      else setFailed(true);
    });
    return () => { current = false; };
  }, [id, originId, attempt, onAuthLost]);
  const includeArchived = archived ?? Boolean(origin?.archivedAtUtc);
  useEffect(() => {
    if (!origin) return;
    const request = ++pieceGeneration.current;
    void getAcquisitionItems(id, includeArchived).then(result => {
      if (pieceGeneration.current === request) { setPage(result); setPiecesFailed(false); }
    }, error => {
      if (pieceGeneration.current !== request) return;
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) onAuthLost();
      else setPiecesFailed(true);
    });
    return () => { pieceGeneration.current = request + 1; };
  }, [id, origin, includeArchived, attempt, onAuthLost]);
  function failure(error: unknown) {
    if (error instanceof ApiError && (error.status === 401 || error.status === 403)) onAuthLost();
    else setMessage('We could not load current saved context. Try again; no connection has been assumed.');
  }
  function close() {
    setMode('view'); setSelected(undefined); setPage(undefined); setAttempt(previous => previous + 1); onDirtyChange(false, false);
    requestAnimationFrame(() => actionButton.current?.focus());
  }
  async function more() {
    if (busy.current || !page?.nextCursor) return;
    busy.current = true; setPending(true); setPiecesFailed(false);
    const request = pieceGeneration.current;
    try {
      const next = await getAcquisitionItems(id, includeArchived, page.nextCursor);
      if (pieceGeneration.current === request) setPage({ items: [...page.items, ...next.items], nextCursor: next.nextCursor });
    } catch (error) { if (active.current) failure(error); }
    finally { busy.current = false; if (active.current) setPending(false); }
  }
  async function prepare(item: ItemDetail, edit = false) {
    if (busy.current) return;
    busy.current = true; setPending(true); setMessage('');
    try {
      const [currentItem, context, target] = await Promise.all([getItem(item.id), getAcquisition(item.id), getSharedAcquisition(id)]);
      if (!active.current) return;
      if (currentItem.archivedAtUtc || edit && context.acquisition?.id !== id) {
        setMessage('This piece changed. Reload the acquisition before continuing.'); return;
      }
      setValue(target); setSelected({ item: currentItem, context }); setMode(edit ? 'edit' : 'link');
    } catch (error) { if (active.current) failure(error); }
    finally { busy.current = false; if (active.current) setPending(false); }
  }
  const readOnly = !origin || Boolean(origin.archivedAtUtc);
  const backToArchive = collectionOrigin === 'archived' || (!collectionOrigin && Boolean(origin?.archivedAtUtc));
  const editableItem = page?.items.find(item => !item.archivedAtUtc);
  return <section className="editor acquisition-view">
    <div className="button-row"><a className="text-link back-link" href={`/inventory/${originId}`} onClick={follow}>Back to piece</a>
      <a className="text-link back-link" href={backToArchive ? '/inventory/archive' : '/inventory'} onClick={follow}>{backToArchive ? 'Back to archive' : 'Back to collection'}</a></div>
    <h1 ref={heading} tabIndex={-1}>Acquisition</h1>
    {failed ? <div role="alert"><p>We could not load this acquisition.</p><button className="secondary" onClick={() => { setFailed(false); setAttempt(previous => previous + 1); }}>Retry loading acquisition</button></div> : !value || !origin ? <p role="status">Loading acquisition…</p> : <>
      <p className="hint">Shared context recorded by you; not independently verified.</p>
      <AcquisitionValues value={fields(value)} />
      <p className="identifier">Acquisition identifier: {value.id}</p>
      {readOnly ? <p role="status">Opened from an archived piece. This acquisition view is read-only.</p> : null}
      {mode === 'existing' ? <ExistingPiecePicker onAuthLost={onAuthLost} onCancel={close} onSelect={(item, context) => {
        setSelected({ item, context }); setMode('link');
      }} /> : mode === 'new' ? <AddItem onAuthLost={onAuthLost} onDirtyChange={onDirtyChange} onCancel={close} onSaved={item => {
        setSavedNew(item); setMode('saved'); onDirtyChange(true, false); onItemSaved(); void prepare(item);
      }} /> : mode === 'link' && selected ? <AcquisitionLinkEditor item={selected.item} initial={selected.context} target={value}
        newlySaved={savedNew?.id === selected.item.id} onAuthLost={onAuthLost} onDirtyChange={onDirtyChange} onClose={(context) => {
          if (context) { setMessage('Current saved connection loaded.'); setSavedNew(undefined); close(); }
          else if (savedNew) { setMode('saved'); onDirtyChange(false, false); }
          else close();
        }} /> : mode === 'edit' && selected ? <AcquisitionEditor itemId={selected.item.id} initial={selected.context} onAuthLost={onAuthLost}
        onDirtyChange={onDirtyChange} onClose={() => { setMessage('Current saved acquisition loaded.'); close(); }} />
        : mode === 'saved' && savedNew ? <section><h2>Piece saved</h2><p>{savedNew.name} is saved. Its acquisition connection is not confirmed.</p>
          <p className="identifier">Saved piece: {savedNew.id}</p>
          <button className="primary" disabled={pending} onClick={() => void prepare(savedNew)}>Continue connection for saved piece</button>
          <a className="text-link" href={`/inventory/${savedNew.id}`} onClick={follow}>View saved piece</a>
        </section> : <>
          {!readOnly ? <div className="button-row">
            <button ref={actionButton} className="primary" disabled={documentsEditing} onClick={() => { setMode('existing'); setMessage(''); onDirtyChange(true, false); }}>Connect existing piece</button>
            <button className="secondary" disabled={documentsEditing} onClick={() => { setMode('new'); setMessage(''); }}>Record a new piece</button>
            {editableItem ? <button className="secondary" disabled={pending || documentsEditing} onClick={() => void prepare(editableItem, true)}>Edit shared acquisition</button> : null}
          </div> : null}
          <AcquisitionDocumentsPanel key={`${origin.id}-${value.id}`} itemId={origin.id} acquisitionId={value.id}
            itemVersion={origin.version} acquisitionVersion={value.version} archived={readOnly} disabled={pending}
            onDirtyChange={onDirtyChange} onEditingChange={setDocumentsEditing} onAuthLost={onAuthLost}
            onCurrent={async () => {
              const [currentItem, currentAcquisition] = await Promise.all([getItem(origin.id), getSharedAcquisition(value.id)]);
              if (!active.current) return;
              setOrigin(currentItem);
              setValue(currentAcquisition);
            }} />
          <h2>Associated pieces</h2>
          <label className="checkbox-row"><input type="checkbox" checked={includeArchived} onChange={event => {
            setPage(undefined); setPiecesFailed(false); setArchived(event.target.checked);
          }} />Show archived pieces</label>
          {!page && !piecesFailed ? <p role="status">Loading associated pieces…</p> : null}
          {piecesFailed ? <div role="alert"><p>We could not load associated pieces.</p><button className="secondary" onClick={() => { setPiecesFailed(false); setAttempt(previous => previous + 1); }}>Retry loading associated pieces</button></div> : null}
          {page?.items.length === 0 ? <p>No {includeArchived ? '' : 'active '}pieces connected. The acquisition remains saved.</p> : null}
          <ul className="acquisition-piece-list">{page?.items.map(item => <li key={item.id}>
            <a className="text-link" href={`/inventory/${item.id}`} onClick={follow}>{item.name}</a>
            {item.archivedAtUtc ? <span className="hint">Archived · read-only</span> : null}
          </li>)}</ul>
          {origin.archivedAtUtc && includeArchived && page && !page.items.some(item => item.id === origin.id) ? <p>Originating archived piece: <a className="text-link" href={`/inventory/${origin.id}`} onClick={follow}>{origin.name}</a> · Archived · read-only</p> : null}
          {page?.nextCursor ? <button className="secondary" disabled={pending} onClick={() => void more()}>Load more associated pieces</button> : null}
        </>}
    </>}
    {pending ? <p role="status">Loading current context…</p> : null}
    {message ? <p role="status">{message}</p> : null}
  </section>;
}
