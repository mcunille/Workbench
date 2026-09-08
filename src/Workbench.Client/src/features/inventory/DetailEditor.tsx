import { FloatingField } from '../../FloatingField';
import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { ItemPhoto } from './ItemPhoto';
import {
  getItem,
  updateItem,
  ItemConflictError,
  ItemValidationError,
  type ItemDetail,
  type UpdateItemRequest,
} from '../../api/items';
type TextFields = { name: string; notes: string; location: string };
function fields(item: ItemDetail): TextFields {
  return {
    name: item.name,
    notes: item.notes ?? '',
    location: item.location ?? '',
  };
}
function SavedText({ title, value }: { title: string; value: TextFields }) {
  return (
    <section className="edit-comparison-record">
      <h3>{title}</h3>
      <dl className="item-details">
        <div>
          <dt>Name</dt>
          <dd>{value.name || 'No name'}</dd>
        </div>
        <div>
          <dt>Notes</dt>
          <dd className="notes">{value.notes || 'No notes recorded'}</dd>
        </div>
        <div>
          <dt>Storage location</dt>
          <dd>{value.location || 'No location recorded'}</dd>
        </div>
      </dl>
    </section>
  );
}
export function DetailEditor({
  item,
  onSaved,
  onCancel,
  onDirtyChange,
  onAuthLost,
  onRecordMayHaveChanged,
}: {
  item: ItemDetail;
  onSaved(item: ItemDetail): void;
  onCancel(current?: ItemDetail): void;
  onDirtyChange(dirty: boolean, uncertain: boolean): void;
  onAuthLost(): void;
  onRecordMayHaveChanged?(): void;
}) {
  const [draft, setDraft] = useState(() => fields(item));
  const [version, setVersion] = useState(item.version);
  const [submitted, setSubmitted] = useState<UpdateItemRequest>();
  const [review, setReview] = useState(false);
  const [current, setCurrent] = useState<ItemDetail>();
  const [previousDraft, setPreviousDraft] = useState<TextFields>();
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState('');
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const busy = useRef(false);
  const alive = useRef(true);
  const form = useRef<HTMLFormElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  useEffect(() => {
    onDirtyChange(true, Boolean(submitted));
    return () => onDirtyChange(false, false);
  }, [submitted, onDirtyChange]);
  useEffect(() => {
    const field = Object.keys(errors)[0]?.toLowerCase();
    if (field)
      form.current?.querySelector<HTMLElement>(`[name="${field}"]`)?.focus();
  }, [errors]);
  useEffect(() => {
    if (review) heading.current?.focus();
    else
      form.current?.querySelector<HTMLInputElement>('[name="name"]')?.focus();
  }, [review]);
  function authFailure(error: unknown) {
    if (
      error instanceof ApiError &&
      (error.status === 401 || error.status === 403)
    ) {
      onAuthLost();
      return true;
    }
    return false;
  }
  async function loadCurrent() {
    setReview(true);
    setCurrent(undefined);
    setMessage('');
    try {
      const result = await getItem(item.id);
      if (alive.current) setCurrent(result);
    } catch (error) {
      if (alive.current && !authFailure(error))
        setMessage(
          'Could not load the current record. Your draft is kept. Retry loading before making another change.',
        );
    }
  }
  async function reviewCurrent() {
    if (busy.current) return;
    busy.current = true;
    setPending(true);
    await loadCurrent();
    busy.current = false;
    if (alive.current) setPending(false);
  }
  async function save() {
    if (busy.current || review) return;
    busy.current = true;
    setPending(true);
    setErrors({});
    setMessage('');
    const command = submitted ?? { expectedVersion: version, ...draft };
    setSubmitted(command);
    try {
      const saved = await updateItem(item.id, command);
      if (alive.current) {
        onDirtyChange(false, false);
        onSaved(saved);
      }
    } catch (error) {
      if (!alive.current || authFailure(error)) return;
      if (error instanceof ItemConflictError) {
        await loadCurrent();
      } else if (error instanceof ItemValidationError) {
        setSubmitted(undefined);
        setErrors(error.errors);
        setMessage('Check the highlighted fields and save again.');
      } else
        setMessage(
          'We could not confirm whether your changes were saved. Your submitted details are kept unchanged. Retry this save or review the current record.',
        );
    } finally {
      onRecordMayHaveChanged?.();
      busy.current = false;
      if (alive.current) setPending(false);
    }
  }
  function errorFor(field: string) {
    return Object.entries(errors)
      .find(([key]) => key.toLowerCase() === field)?.[1]
      ?.join(' ');
  }
  return (
    <section aria-labelledby="edit-details-title">
      <h2 id="edit-details-title" ref={heading} tabIndex={-1}>
        {review ? 'Review current record' : 'Edit details'}
      </h2>
      {review ? (
        <>
          <p>
            This record may have changed in another session. Compare the
            current saved record with your draft before deciding what to keep.
          </p>
          <div className="edit-comparison">
            <SavedText title="Your draft" value={draft} />
            {current ? (
              <SavedText
                title="Current saved record"
                value={fields(current)}
              />
            ) : null}
          </div>
          <div className="button-row">
            {current ? (
              <>
                {current.archivedAtUtc ? (
                  <p role="status">
                    The current record is archived and read-only. Your draft
                    is kept for reference.
                  </p>
                ) : null}
                <button
                  className="secondary"
                  onClick={() => onSaved(current)}
                >
                  {current.archivedAtUtc
                    ? 'Discard draft and view archived record'
                    : 'Use saved record'}
                </button>
                {!current.archivedAtUtc ? (
                  <button
                    className="primary"
                    onClick={() => {
                      setPreviousDraft(draft);
                      setDraft(fields(current));
                      setVersion(current.version);
                      setSubmitted(undefined);
                      setErrors({});
                      setMessage('');
                      setReview(false);
                    }}
                  >
                    Review my edits
                  </button>
                ) : null}
              </>
            ) : (
              <button
                className="secondary"
                disabled={pending}
                onClick={() => void reviewCurrent()}
              >
                Retry loading current record
              </button>
            )}
          </div>
          {current?.archivedAtUtc ? (
            <ItemPhoto
              interactive
              url={current.photo?.detailUrl}
              name={current.name}
              onAuthLost={onAuthLost}
            />
          ) : null}
        </>
      ) : (
        <>
          {previousDraft ? (
            <>
              <p>
                Start from the current saved values. Copy any of your earlier
                edits you still want, then save explicitly.
              </p>
              <SavedText title="Your earlier draft" value={previousDraft} />
            </>
          ) : null}
          <form
            ref={form}
            className="form-stack"
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              void save();
            }}
          >
            {(['name', 'notes', 'location'] as const).map((field) => (
              <div className="edit-field" key={field}>
                <FloatingField
                  label={field === 'name'
                    ? 'Name'
                    : field === 'notes'
                      ? 'Notes (optional)'
                      : 'Storage location (optional)'}
                >
                  {field === 'notes' ? (
                    <textarea
                      placeholder=" "
                      id={'edit-' + field}
                      name={field}
                      rows={6}
                      value={draft[field]}
                      disabled={Boolean(submitted)}
                      aria-invalid={Boolean(errorFor(field))}
                      aria-describedby={
                        errorFor(field) ? 'edit-error-' + field : undefined
                      }
                      onChange={(event) =>
                        setDraft({ ...draft, [field]: event.target.value })
                      }
                    />
                  ) : (
                    <input
                      placeholder=" "
                      id={'edit-' + field}
                      name={field}
                      value={draft[field]}
                      disabled={Boolean(submitted)}
                      aria-invalid={Boolean(errorFor(field))}
                      aria-describedby={
                        errorFor(field) ? 'edit-error-' + field : undefined
                      }
                      onChange={(event) =>
                        setDraft({ ...draft, [field]: event.target.value })
                      }
                    />
                  )}
                </FloatingField>
                {errorFor(field) ? (
                  <p
                    id={'edit-error-' + field}
                    className="form-message error"
                  >
                    {errorFor(field)}
                  </p>
                ) : null}
              </div>
            ))}
            <div className="button-row">
              <button className="primary" type="submit" disabled={pending}>
                {submitted ? 'Retry save' : 'Save changes'}
              </button>
              {submitted ? (
                <button
                  className="secondary"
                  type="button"
                  disabled={pending}
                  onClick={() => void reviewCurrent()}
                >
                  Review current record
                </button>
              ) : (
                <button
                  className="secondary"
                  type="button"
                  onClick={() => onCancel(current)}
                >
                  Cancel
                </button>
              )}
            </div>
          </form>
        </>
      )}
      {pending ? (
        <p role="status">
          {review ? 'Loading current record…' : 'Saving changes…'}
        </p>
      ) : null}
      {message ? (
        <p role="alert" className="form-message error">
          {message}
        </p>
      ) : null}
    </section>
  );
}
