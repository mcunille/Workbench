import { useCallback, useEffect, useLayoutEffect, useRef, useState, type MouseEvent } from 'react';
import { ApiError } from '../../api/auth';
import {
  getAcquisition,
  type AcquisitionContext,
  type Acquisition,
} from '../../api/acquisitions';
import { getItem, type ItemDetail } from '../../api/items';
import { AcquisitionEditor } from './AcquisitionEditor';
import { AcquisitionValues } from './AcquisitionFields';
import { fields } from './acquisitionDraft';
import { AcquisitionPicker } from './AcquisitionPicker';
import { AcquisitionLinkEditor } from './AcquisitionLinkEditor';
import { AcquisitionDocumentsPanel } from './AcquisitionDocumentsPanel';

export function AcquisitionPanel({
  item,
  disabled,
  onEditingChange,
  onDirtyChange,
  onAuthLost,
  onCurrent,
  follow,
  viewHref,
}: {
  item: ItemDetail;
  disabled: boolean;
  onEditingChange(editing: boolean): void;
  onDirtyChange(dirty: boolean, uncertain: boolean): void;
  onAuthLost(): void;
  onCurrent(version: string, item?: ItemDetail): void;
  follow?(event: MouseEvent<HTMLAnchorElement>): void;
  viewHref?: string;
}) {
  const [context, setContext] = useState<AcquisitionContext>();
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [editing, setEditing] = useState(false);
  const [documentsEditing, setDocumentsEditing] = useState(false);
  const documentEditingChange = useCallback((value: boolean) => {
    setDocumentsEditing(value); onEditingChange(value);
  }, [onEditingChange]);
  const [picking, setPicking] = useState(false);
  const [target, setTarget] = useState<Acquisition | null>();
  const [message, setMessage] = useState('');
  const button = useRef<HTMLButtonElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const restoreFocus = useRef(false);
  const active = useRef(true);
  useEffect(() => { active.current = true; return () => { active.current = false; }; }, []);
  useLayoutEffect(() => {
    if (!editing && !picking && target === undefined && restoreFocus.current) {
      restoreFocus.current = false;
      (button.current ?? heading.current)?.focus();
    }
  }, [editing, picking, target]);
  useEffect(() => {
    let current = true;
    void getAcquisition(item.id).then(
      (result) => {
        if (current) setContext(result);
      },
      (error) => {
        if (!current) return;
        if (
          error instanceof ApiError &&
          (error.status === 401 || error.status === 403)
        )
          onAuthLost();
        else setFailed(true);
      },
    );
    return () => {
      current = false;
    };
  }, [item.id, attempt, onAuthLost]);
  return (
    <section
      className="acquisition-section"
      aria-labelledby="acquisition-title"
    >
      <h2 id="acquisition-title" ref={heading} tabIndex={-1}>
        Acquisition
      </h2>
      <p className="hint">
        Record when and how you got this piece. Recorded by you; not
        independently verified.
      </p>
      {message ? <p role="status">{message}</p> : null}
      {picking ? <AcquisitionPicker onAuthLost={onAuthLost} onCancel={() => {
        restoreFocus.current = true; setPicking(false); onEditingChange(false); onDirtyChange(false, false);
      }} onSelect={value => { setPicking(false); setTarget(value); }} /> : target !== undefined && context ? (
        <AcquisitionLinkEditor item={item} initial={{ ...context, itemVersion: item.version }} target={target} onDirtyChange={onDirtyChange} onAuthLost={onAuthLost}
          onClose={(saved, currentItem) => {
            if (saved) { setContext(saved); onCurrent(currentItem?.version ?? saved.itemVersion, currentItem); setMessage('Current saved connection loaded.'); }
            restoreFocus.current = true; setTarget(undefined); onEditingChange(false); onDirtyChange(false, false);
          }} />
      ) : editing && context ? (
        <AcquisitionEditor
          itemId={item.id}
          initial={{ ...context, itemVersion: item.version }}
          onDirtyChange={onDirtyChange}
          onAuthLost={onAuthLost}
          onClose={(saved, currentItem) => {
            if (saved) {
              setContext(saved);
              onCurrent(currentItem?.version ?? saved.itemVersion, currentItem);
              setMessage('Current saved acquisition loaded.');
            }
            restoreFocus.current = true;
            setEditing(false);
            onEditingChange(false);
            onDirtyChange(false, false);
          }}
        />
      ) : failed ? (
        <div role="alert">
          <p>We could not load acquisition context.</p>
          <button
            className="secondary"
            onClick={() => {
              setFailed(false);
              setContext(undefined);
              setAttempt((value) => value + 1);
            }}
          >
            Retry loading acquisition
          </button>
        </div>
      ) : context ? (
        <>
          {context.acquisition ? (
            <>
              <AcquisitionValues value={fields(context.acquisition)} />
              <a className="text-link" href={!item.archivedAtUtc && viewHref?.split('/')[2] === context.acquisition.id ? viewHref : `/acquisitions/${context.acquisition.id}/from/${item.id}`} onClick={follow}>View acquisition</a>
            </>
          ) : (
            <p>No acquisition recorded.</p>
          )}
          {!item.archivedAtUtc ? (
            <div className="button-row">
            <button
              className="secondary"
              ref={button}
              disabled={disabled || documentsEditing}
              onClick={() => {
                setEditing(true);
                onEditingChange(true);
                setMessage('');
              }}
            >
              {context.acquisition ? 'Edit acquisition' : 'Add acquisition'}
            </button>
            <button className="secondary" disabled={disabled || documentsEditing} onClick={() => {
              setPicking(true); onEditingChange(true); onDirtyChange(true, false); setMessage('');
            }}>{context.acquisition ? 'Change acquisition' : 'Connect to an acquisition'}</button>
            {context.acquisition ? <button className="secondary danger" disabled={disabled || documentsEditing} onClick={() => {
              setTarget(null); onEditingChange(true); setMessage('');
            }}>Remove connection</button> : null}
            </div>
          ) : null}
          {context.acquisition ? <AcquisitionDocumentsPanel key={context.acquisition.id}
            itemId={item.id} acquisitionId={context.acquisition.id} itemVersion={item.version}
            acquisitionVersion={context.acquisition.version} archived={Boolean(item.archivedAtUtc)} disabled={disabled}
            onDirtyChange={onDirtyChange} onEditingChange={documentEditingChange} onAuthLost={onAuthLost}
            onCurrent={async () => {
              const [currentItem, currentContext] = await Promise.all([getItem(item.id), getAcquisition(item.id)]);
              if (!active.current) return;
              setContext(currentContext);
              onCurrent(currentItem.version, currentItem);
            }} /> : null}
        </>
      ) : (
        <p role="status">Loading acquisition…</p>
      )}
    </section>
  );
}
