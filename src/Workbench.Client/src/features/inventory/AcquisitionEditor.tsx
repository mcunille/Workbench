import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { getItem, ItemValidationError, type ItemDetail } from '../../api/items';
import {
  AcquisitionConflictError,
  getAcquisition,
  saveAcquisition,
  type AcquisitionContext,
  type AcquisitionCommand,
} from '../../api/acquisitions';
import { AcquisitionFields, AcquisitionValues } from './AcquisitionFields';
import { fields, type Draft } from './acquisitionDraft';

export function AcquisitionEditor({
  itemId,
  initial,
  onClose,
  onDirtyChange,
  onAuthLost,
}: {
  itemId: string;
  initial: AcquisitionContext;
  onClose(context?: AcquisitionContext, item?: ItemDetail): void;
  onDirtyChange(dirty: boolean, uncertain: boolean): void;
  onAuthLost(): void;
}) {
  const [base, setBase] = useState(initial);
  const [draft, setDraft] = useState(() => fields(initial.acquisition));
  const [earlier, setEarlier] = useState<Draft>();
  const [submitted, setSubmitted] = useState<AcquisitionCommand>();
  const [review, setReview] = useState(false);
  const [current, setCurrent] = useState<{
    context: AcquisitionContext;
    item: ItemDetail;
  }>();
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState('');
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const requestId = useRef(crypto.randomUUID());
  const busy = useRef(false);
  const alive = useRef(true);
  const heading = useRef<HTMLHeadingElement>(null);
  const form = useRef<HTMLFormElement>(null);
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
    if (review) heading.current?.focus();
    else form.current?.querySelector<HTMLElement>('[name="method"]')?.focus();
  }, [review]);
  useEffect(() => {
    const field = Object.keys(errors)[0]?.toLowerCase();
    if (field)
      form.current?.querySelector<HTMLElement>(`[name="${field}"]`)?.focus();
  }, [errors]);
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
      const [context, item] = await Promise.all([
        getAcquisition(itemId),
        getItem(itemId),
      ]);
      if (alive.current) setCurrent({ context, item });
    } catch (error) {
      if (alive.current && !authFailure(error))
        setMessage(
          'We could not load the current acquisition. Your draft is kept. Try loading again.',
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
    const required: Record<string, string[]> = {};
    if (!draft.method)
      required.method = [
        'Choose an acquisition method, including Unknown if needed.',
      ];
    for (const field of ['year', 'month', 'day'] as const) {
      const needed =
        draft.precision !== 'Unknown' &&
        (field === 'year' ||
          (field === 'month' && draft.precision !== 'Year') ||
          (field === 'day' && draft.precision === 'Exact date'));
      if (needed && !draft[field])
        required[field] = [
          'Enter the known ' +
            field +
            ' or choose less precise date information.',
        ];
    }
    if (!submitted && Object.keys(required).length) {
      setErrors(required);
      return;
    }
    busy.current = true;
    setPending(true);
    setErrors({});
    setMessage('');
    const command = submitted ?? {
      expectedItemVersion: base.itemVersion,
      ...(base.acquisition
        ? { expectedAcquisitionVersion: base.acquisition.version }
        : { creationRequestId: requestId.current }),
      method: draft.method,
      source: draft.source.trim() || null,
      notes: draft.notes.trim() ? draft.notes : null,
      year: draft.precision === 'Unknown' ? null : Number(draft.year),
      month:
        draft.precision === 'Unknown' || draft.precision === 'Year'
          ? null
          : Number(draft.month),
      day: draft.precision === 'Exact date' ? Number(draft.day) : null,
    };
    setSubmitted(command);
    try {
      const saved = await saveAcquisition(
        itemId,
        base.acquisition?.id,
        command,
      );
      const savedItem = await getItem(itemId);
      if (alive.current) onClose(saved, savedItem);
    } catch (error) {
      if (!alive.current || authFailure(error)) return;
      if (error instanceof AcquisitionConflictError) await loadCurrent();
      else if (error instanceof ItemValidationError) {
        setSubmitted(undefined);
        setErrors(error.errors);
        setMessage('Check the highlighted fields and save again.');
      } else
        setMessage(
          'We could not confirm whether your acquisition was saved. Your submitted input is kept unchanged. Retry this save or review the current acquisition.',
        );
    } finally {
      busy.current = false;
      if (alive.current) setPending(false);
    }
  }
  return (
    <section aria-labelledby="acquisition-editor-title">
      <h3 id="acquisition-editor-title" ref={heading} tabIndex={-1}>
        {review
          ? 'Review current acquisition'
          : base.acquisition
            ? 'Edit acquisition'
            : 'Add acquisition'}
      </h3>
      {review ? (
        <>
          <p>
            The record may have changed. Compare the current saved context with
            your draft before deciding what to keep.
          </p>
          <div className="edit-comparison">
            <section>
              <h4>Your draft</h4>
              <AcquisitionValues value={draft} />
            </section>
            {current ? (
              <section>
                <h4>Current saved acquisition</h4>
                {current.context.acquisition ? (
                  <AcquisitionValues
                    value={fields(current.context.acquisition)}
                  />
                ) : (
                  <p>No acquisition recorded.</p>
                )}
              </section>
            ) : null}
          </div>
          {current?.item.archivedAtUtc ? (
            <p role="status">
              This item is archived and read-only. Your draft is kept for
              reference.
            </p>
          ) : null}
          <div className="button-row">
            {current ? (
              <>
                <button
                  className="secondary"
                  disabled={pending}
                  onClick={() => onClose(current.context, current.item)}
                >
                  Use saved record
                </button>
                {!current.item.archivedAtUtc ? (
                  <button
                    className="primary"
                    disabled={pending}
                    onClick={() => {
                      setEarlier(draft);
                      setDraft(fields(current.context.acquisition));
                      setBase(current.context);
                      // Reviewing a resolved outcome is an explicit new attempt, never an automatic retry.
                      requestId.current = crypto.randomUUID();
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
                Retry loading current acquisition
              </button>
            )}
          </div>
        </>
      ) : (
        <>
          {earlier ? (
            <section>
              <h4>Your earlier draft</h4>
              <p>
                Start from the saved values. Copy any earlier edits you still
                want, then save explicitly.
              </p>
              <AcquisitionValues value={earlier} />
            </section>
          ) : null}
          <form
            className="form-stack acquisition-form"
            ref={form}
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              void save();
            }}
          >
            <AcquisitionFields
              draft={draft}
              setDraft={setDraft}
              disabled={Boolean(submitted)}
              errors={errors}
            />
            <div className="button-row">
              <button type="submit" className="primary" disabled={pending}>
                {submitted ? 'Retry save' : 'Save acquisition'}
              </button>
              {submitted ? (
                <button
                  type="button"
                  className="secondary"
                  disabled={pending}
                  onClick={() => void reviewCurrent()}
                >
                  Review current acquisition
                </button>
              ) : (
                <button
                  type="button"
                  className="secondary"
                  onClick={() => onClose(current?.context, current?.item)}
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
          {review ? 'Loading current acquisition…' : 'Saving acquisition…'}
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
