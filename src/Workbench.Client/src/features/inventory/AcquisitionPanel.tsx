import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import {
  getAcquisition,
  type AcquisitionContext,
} from '../../api/acquisitions';
import type { ItemDetail } from '../../api/items';
import { AcquisitionEditor } from './AcquisitionEditor';
import { AcquisitionValues } from './AcquisitionFields';
import { fields } from './acquisitionDraft';

export function AcquisitionPanel({
  item,
  disabled,
  onEditingChange,
  onDirtyChange,
  onAuthLost,
  onCurrent,
}: {
  item: ItemDetail;
  disabled: boolean;
  onEditingChange(editing: boolean): void;
  onDirtyChange(dirty: boolean, uncertain: boolean): void;
  onAuthLost(): void;
  onCurrent(version: string, item?: ItemDetail): void;
}) {
  const [context, setContext] = useState<AcquisitionContext>();
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [editing, setEditing] = useState(false);
  const [message, setMessage] = useState('');
  const button = useRef<HTMLButtonElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const restoreFocus = useRef(false);
  useLayoutEffect(() => {
    if (!editing && restoreFocus.current) {
      restoreFocus.current = false;
      (button.current ?? heading.current)?.focus();
    }
  }, [editing]);
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
      {editing && context ? (
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
            <AcquisitionValues value={fields(context.acquisition)} />
          ) : (
            <p>No acquisition recorded.</p>
          )}
          {!item.archivedAtUtc ? (
            <button
              className="secondary"
              ref={button}
              disabled={disabled}
              onClick={() => {
                setEditing(true);
                onEditingChange(true);
                setMessage('');
              }}
            >
              {context.acquisition ? 'Edit acquisition' : 'Add acquisition'}
            </button>
          ) : null}
        </>
      ) : (
        <p role="status">Loading acquisition…</p>
      )}
    </section>
  );
}
