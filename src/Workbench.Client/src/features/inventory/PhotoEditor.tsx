import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import {
  putItemPhoto,
  removeItemPhoto,
  type ItemDetail,
} from '../../api/items';
import { ItemPhoto } from './ItemPhoto';
import { preparePhoto } from './preparePhoto';
type Command = { requestId: string; version: string; file?: Blob };
export function PhotoEditor({
  item,
  onAuthLost,
  onDirtyChange,
  reload,
  onPhotoChanged,
}: {
  item: ItemDetail;
  onAuthLost(): void;
  onDirtyChange(value: boolean, uncertain: boolean): void;
  reload(): Promise<void>;
  onPhotoChanged?(): void;
}) {
  const [prepared, setPrepared] = useState<{ blob: Blob; url: string }>();
  const [busy, setBusy] = useState<'prepare' | 'save'>();
  const [error, setError] = useState('');
  const [command, setCommand] = useState<Command>();
  const [conflict, setConflict] = useState(false);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [saved, setSaved] = useState(false);
  const active = useRef(true);
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    active.current = true;
    return () => {
      active.current = false;
    };
  }, []);
  useEffect(() => {
    onDirtyChange(Boolean(prepared || busy || command), Boolean(command));
    return () => onDirtyChange(false, false);
  }, [prepared, busy, command, onDirtyChange]);
  useEffect(
    () => () => {
      if (prepared) URL.revokeObjectURL(prepared.url);
    },
    [prepared],
  );
  useEffect(() => {
    if (!confirmRemove) return;
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.showModal();
    return () => previous?.focus();
  }, [confirmRemove]);
  async function choose(file?: File) {
    if (!file) return;
    setBusy('prepare');
    setError('');
    setSaved(false);
    try {
      const blob = await preparePhoto(file);
      if (active.current) setPrepared({ blob, url: URL.createObjectURL(blob) });
    } catch (failure) {
      if (active.current)
        setError(
          failure instanceof Error
            ? failure.message
            : 'Could not prepare this photograph.',
        );
    } finally {
      if (active.current) setBusy(undefined);
    }
  }
  async function refreshConflict() {
    setBusy('save');
    try {
      await reload();
      if (active.current) {
        setConflict(false);
        setError(
          'The current item has been loaded. Review it before uploading or removing a photograph again.',
        );
      }
    } catch (failure) {
      if (
        failure instanceof ApiError &&
        (failure.status === 401 || failure.status === 403)
      )
        onAuthLost();
      if (active.current)
        setError(
          'Could not reload the current item. Retry reloading before making another change.',
        );
    } finally {
      if (active.current) setBusy(undefined);
    }
  }
  async function save(next: Command) {
    setBusy('save');
    setError('');
    setSaved(false);
    setCommand(next);
    setConfirmRemove(false);
    try {
      if (next.file)
        await putItemPhoto(item.id, next.file, next.requestId, next.version);
      else await removeItemPhoto(item.id, next.requestId, next.version);
      if (!active.current) return;
      onPhotoChanged?.();
      await reload();
      if (active.current) {
        setCommand(undefined);
        setPrepared(undefined);
        setSaved(true);
      }
    } catch (failure) {
      if (!active.current) return;
      const status = failure instanceof ApiError ? failure.status : 0;
      if (status === 401 || status === 403) {
        onAuthLost();
        return;
      }
      if (status === 409) {
        setCommand(undefined);
        setConflict(true);
        setError(
          'This item changed elsewhere. Reload it before deciding what to do.',
        );
      } else if ([400, 413, 415, 422].includes(status)) {
        setCommand(undefined);
        setError(
          status === 413
            ? 'The prepared image exceeds the upload limits. Choose a smaller photograph.'
            : status === 415
              ? 'This image format is not supported. Choose a JPEG, PNG, or WebP.'
              : 'This photograph could not be accepted. Choose another image.',
        );
      } else
        setError(
          'We could not confirm the change. Retry to safely check or finish the same request.',
        );
    } finally {
      if (active.current) setBusy(undefined);
    }
  }
  return (
    <section className="photo-editor" aria-labelledby="photo-heading">
      <h2 id="photo-heading">Photograph</h2>
      <ItemPhoto
        interactive
        url={item.photo?.detailUrl}
        name={item.name}
        onAuthLost={onAuthLost}
      />
      <p className="hint" id="photo-help">
        Choose a JPEG, PNG, or WebP up to 20 MiB and 40 megapixels. Your browser
        resizes the image to at most 2,048 pixels and removes embedded metadata
        before uploading. Only the prepared image is sent. Visible details in
        the photograph remain visible.
      </p>
      <label htmlFor="photo-file">Choose photograph</label>
      <input
        id="photo-file"
        type="file"
        accept="image/jpeg,image/png,image/webp"
        aria-describedby="photo-help"
        disabled={Boolean(busy || command || conflict)}
        onChange={(event) => {
          void choose(event.target.files?.[0]);
          event.target.value = '';
        }}
      />
      {busy ? (
        <p role="status">
          {busy === 'prepare'
            ? 'Preparing photograph on this device…'
            : 'Saving photograph…'}
        </p>
      ) : null}
      {prepared ? (
        <div className="photo-preview">
          <h3>Preview before upload</h3>
          <img src={prepared.url} alt="Prepared photograph preview" />
          <p>{(prepared.blob.size / 1024).toFixed(0)} KiB prepared upload</p>
        </div>
      ) : null}
      {error ? <p role="alert">{error}</p> : null}
      {saved ? <p role="status">Photograph updated.</p> : null}
      <div className="button-row">
        {conflict ? (
          <button
            className="primary"
            disabled={Boolean(busy)}
            onClick={() => void refreshConflict()}
          >
            Reload current item
          </button>
        ) : command ? (
          <button
            className="primary"
            disabled={Boolean(busy)}
            onClick={() => void save(command)}
          >
            {command.file ? 'Retry upload' : 'Retry removal'}
          </button>
        ) : prepared ? (
          <>
            <button
              className="primary"
              disabled={Boolean(busy)}
              onClick={() =>
                void save({
                  file: prepared.blob,
                  requestId: crypto.randomUUID(),
                  version: item.version,
                })
              }
            >
              Upload photograph
            </button>
            <button
              className="secondary"
              disabled={Boolean(busy)}
              onClick={() => {
                setPrepared(undefined);
                setError('');
              }}
            >
              Discard preview
            </button>
          </>
        ) : null}
        {item.photo ? (
          <button
            className="secondary danger"
            disabled={Boolean(busy || command || conflict || prepared)}
            onClick={() => setConfirmRemove(true)}
          >
            Remove photograph
          </button>
        ) : null}
      </div>
      {confirmRemove ? (
        <dialog
          ref={dialog}
          aria-labelledby="remove-photo-title"
          onCancel={(event) => {
            event.preventDefault();
            setConfirmRemove(false);
          }}
        >
          <h2 id="remove-photo-title">Remove photograph?</h2>
          <p>The item will remain in your collection.</p>
          <div className="button-row">
            <button
              className="primary"
              autoFocus
              onClick={() => setConfirmRemove(false)}
            >
              Keep photograph
            </button>
            <button
              className="secondary danger"
              onClick={() =>
                void save({
                  requestId: crypto.randomUUID(),
                  version: item.version,
                })
              }
            >
              Confirm removal
            </button>
          </div>
        </dialog>
      ) : null}
    </section>
  );
}
