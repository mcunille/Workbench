import { FloatingField } from '../../FloatingField';
import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import {
  createItem,
  ItemValidationError,
  type CreateItemRequest,
  type ItemDetail,
} from '../../api/items';
interface Props {
  onSaved(item: ItemDetail): void;
  onCancel(): void;
  onDirtyChange(dirty: boolean, uncertain: boolean): void;
  onAuthLost(): void;
}
export function AddItem({
  onSaved,
  onCancel,
  onDirtyChange,
  onAuthLost,
}: Props) {
  const [name, setName] = useState('');
  const [notes, setNotes] = useState('');
  const [location, setLocation] = useState('');
  const [requestId] = useState(() => crypto.randomUUID());
  const [pending, setPending] = useState(false);
  const [submitted, setSubmitted] = useState<CreateItemRequest>();
  const [message, setMessage] = useState('');
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const busy = useRef(false);
  const form = useRef<HTMLFormElement>(null);
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  const dirty = !!(name || notes || location || submitted);
  useEffect(() => {
    onDirtyChange(dirty, !!submitted);
  }, [dirty, submitted, onDirtyChange]);
  useEffect(() => {
    const field = Object.keys(errors)[0]?.toLowerCase();
    if (field)
      form.current?.querySelector<HTMLElement>(`[name="${field}"]`)?.focus();
  }, [errors]);
  async function save() {
    if (busy.current) return;
    busy.current = true;
    const payload = submitted ?? {
      creationRequestId: requestId,
      name,
      notes,
      location,
    };
    setSubmitted(payload);
    setPending(true);
    setErrors({});
    setMessage('');
    try {
      const item = await createItem(payload);
      if (alive.current) {
        onDirtyChange(false, false);
        onSaved(item);
      }
    } catch (error) {
      if (!alive.current) return;
      if (error instanceof ItemValidationError) {
        setSubmitted(undefined);
        setErrors(error.errors);
        setMessage('Check the highlighted fields and save again.');
      } else if (
        error instanceof ApiError &&
        (error.status === 401 || error.status === 403)
      ) {
        onAuthLost();
      } else {
        setMessage(
          'We could not confirm whether your item was saved. Retry this save to check safely. Your submitted details are kept unchanged.',
        );
      }
    } finally {
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
    <section className="editor">
      <h1>Add item</h1>
      <p className="lede">
        Start with a name. Add whatever helps you recognize and find this piece.
      </p>
      <form
        ref={form}
        className="form-stack"
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        <FloatingField label="Name">
          <input
            id="item-name"
            name="name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            disabled={!!submitted}
            placeholder="For example, blue sapphire"
            aria-invalid={!!errorFor('name')}
            aria-describedby={errorFor('name') ? 'name-error' : undefined}
          />
        </FloatingField>
        {errorFor('name') ? (
          <p id="name-error" className="form-message error">
            {errorFor('name')}
          </p>
        ) : null}
        <FloatingField label="Notes (optional)">
          <textarea
            id="item-notes"
            name="notes"
            rows={6}
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            disabled={!!submitted}
            placeholder="What would you like to remember?"
            aria-invalid={!!errorFor('notes')}
            aria-describedby={errorFor('notes') ? 'notes-error' : undefined}
          />
        </FloatingField>
        {errorFor('notes') ? (
          <p id="notes-error" className="form-message error">
            {errorFor('notes')}
          </p>
        ) : null}
        <FloatingField label="Storage location (optional)">
          <input
            id="item-location"
            name="location"
            value={location}
            onChange={(e) => setLocation(e.target.value)}
            disabled={!!submitted}
            placeholder="For example, tray A, slot 3"
            aria-invalid={!!errorFor('location')}
            aria-describedby={errorFor('location') ? 'location-error' : undefined}
          />
        </FloatingField>
        {errorFor('location') ? (
          <p id="location-error" className="form-message error">
            {errorFor('location')}
          </p>
        ) : null}
        {pending ? <p role="status">Saving item…</p> : null}
        {message ? (
          <p role="alert" className="form-message error">
            {message}
          </p>
        ) : null}
        <div className="button-row">
          <button className="primary" disabled={pending} type="submit">
            {pending ? 'Saving…' : submitted ? 'Retry save' : 'Save item'}
          </button>
          <button className="secondary" type="button" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </form>
    </section>
  );
}
