import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import {
  archiveItem,
  getItem,
  ItemConflictError,
  type ItemDetail,
} from '../../api/items';

export function ArchiveItem({
  item,
  onCancel,
  onCurrent,
  onDirtyChange,
  onAuthLost,
  onUnavailable,
  invalidate,
}: {
  item: ItemDetail;
  onCancel(): void;
  onCurrent(item: ItemDetail): void;
  onDirtyChange(dirty: boolean, uncertain: boolean): void;
  onAuthLost(): void;
  onUnavailable(): void;
  invalidate(): void;
}) {
  const [version] = useState(item.version);
  const [submitted, setSubmitted] = useState(false);
  const [review, setReview] = useState(false);
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState('');
  const busy = useRef(false);
  const alive = useRef(true);
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  useEffect(() => {
    onDirtyChange(true, submitted);
    return () => onDirtyChange(false, false);
  }, [submitted, onDirtyChange]);
  function handled(error: unknown) {
    if (
      error instanceof ApiError &&
      (error.status === 401 || error.status === 403)
    ) {
      onAuthLost();
      return true;
    }
    if (error instanceof ApiError && error.status === 404) {
      onUnavailable();
      return true;
    }
    return false;
  }
  async function load() {
    setReview(true);
    try {
      const current = await getItem(item.id);
      if (alive.current) onCurrent(current);
    } catch (error) {
      if (alive.current && !handled(error))
        setMessage(
          'Could not load the current record. Retry loading before making another change.',
        );
    }
  }
  async function run(readOnly = false) {
    if (busy.current) return;
    busy.current = true;
    setPending(true);
    setMessage('');
    try {
      if (readOnly) await load();
      else {
        setSubmitted(true);
        const current = await archiveItem(item.id, {
          expectedVersion: version,
        });
        if (alive.current) onCurrent(current);
      }
    } catch (error) {
      if (!alive.current || handled(error)) return;
      if (error instanceof ItemConflictError) await load();
      else
        setMessage(
          'Archiving could not be confirmed. Retry the same request or review the current record. Leaving this page does not undo a submitted request.',
        );
    } finally {
      invalidate();
      busy.current = false;
      if (alive.current) setPending(false);
    }
  }
  return (
    <section aria-labelledby="archive-title">
      <h2 id="archive-title" tabIndex={-1} ref={heading}>
        {review ? 'Review current record' : 'Archive record?'}
      </h2>
      <p>
        Archive “{item.name}”? It will leave collection browsing and search.
        Its details and photograph remain accessible through its link.
        Restoring it to browsing is currently unavailable.
      </p>
      {pending ? (
        <p role="status">
          {review ? 'Loading current record…' : 'Archiving record…'}
        </p>
      ) : null}
      {message ? <p role="alert">{message}</p> : null}
      <div className="button-row">
        {review ? (
          <button
            className="secondary"
            disabled={pending}
            onClick={() => void run(true)}
          >
            Retry loading current record
          </button>
        ) : (
          <>
            <button
              className="secondary danger"
              disabled={pending}
              onClick={() => void run()}
            >
              {submitted ? 'Retry archive' : 'Confirm archive record'}
            </button>
            {submitted ? (
              <button
                className="secondary"
                disabled={pending}
                onClick={() => void run(true)}
              >
                Review current record
              </button>
            ) : (
              <button className="secondary" onClick={onCancel}>
                Cancel
              </button>
            )}
          </>
        )}
      </div>
    </section>
  );
}
