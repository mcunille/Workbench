import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type MouseEvent,
} from 'react';
import { CollectionMemory } from './collectionMemory';
import { ApiError } from '../../api/auth';
import { Icon } from '../../Icon';
import { ItemPhoto } from './ItemPhoto';
import { PhotoEditor } from './PhotoEditor';
import { DetailEditor } from './DetailEditor';
import { ArchiveItem } from './ArchiveItem';
import {
  getItem,
  getItems,
  type ItemDetail,
  type ItemPage,
} from '../../api/items';
type Props = {
  follow(event: MouseEvent<HTMLAnchorElement>): void;
  onAuthLost(): void;
};
export function Collection({
  follow,
  onAuthLost,
  memory: providedMemory,
}: Props & { memory?: CollectionMemory }) {
  const [localMemory] = useState(() => new CollectionMemory());
  const memory = providedMemory ?? localMemory;
  const [view, setView] = useState(() => memory.snapshot?.view ?? 'grid');
  const [draft, setDraft] = useState(() => memory.snapshot?.draft ?? '');
  const [query, setQuery] = useState(() => memory.snapshot?.query ?? '');
  const [page, setPage] = useState<ItemPage | undefined>(
    () => memory.snapshot?.page,
  );
  const [request, setRequest] = useState<{ cursor?: string } | undefined>(
    () => (memory.snapshot?.page ? undefined : {}),
  );
  const [failed, setFailed] = useState<number>();
  const heading = useRef<HTMLHeadingElement>(null);
  const links = useRef(new Map<string, HTMLAnchorElement>());
  const loading = Boolean(request);
  useLayoutEffect(
    () =>
      memory.subscribeInvalidation(() => {
        setPage(undefined);
        setFailed(undefined);
        setRequest({});
      }),
    [memory],
  );
  useLayoutEffect(
    () =>
      memory.subscribePhotos((id, photo) => {
        setPage((previous) =>
          previous
            ? {
                ...previous,
                items: previous.items.map((item) =>
                  item.id === id ? { ...item, photo } : item,
                ),
              }
            : previous,
        );
      }),
    [memory],
  );
  useEffect(() => {
    memory.save({ view, draft, query, page });
  }, [memory, view, draft, query, page]);
  useLayoutEffect(() => {
    if (memory.snapshot?.page) {
      const selected = memory.selectedId
        ? links.current.get(memory.selectedId)
        : undefined;
      (selected ?? heading.current)?.focus({ preventScroll: true });
      window.scrollTo({ top: memory.scrollY ?? 0, behavior: 'instant' });
    }
    const savePosition = () => {
      memory.savePosition(window.scrollY);
    };
    window.addEventListener('scroll', savePosition, { passive: true });
    return () => window.removeEventListener('scroll', savePosition);
  }, [memory]);
  useEffect(() => {
    if (!request) return;
    let current = true;
    void getItems(request.cursor, query || undefined).then(
      (result) => {
        if (!current) return;
        setPage((previous) =>
          request.cursor && previous
            ? {
                items: [...previous.items, ...result.items],
                nextCursor: result.nextCursor,
              }
            : result,
        );
        setFailed(undefined);
        setRequest(undefined);
      },
      (error) => {
        if (!current) return;
        const status = error instanceof ApiError ? error.status : 500;
        if (status === 401 || status === 403) onAuthLost();
        setFailed(status);
        setRequest(undefined);
      },
    );
    return () => {
      current = false;
    };
  }, [request, query, onAuthLost]);
  function search(value: string) {
    setQuery(value.trim());
    setPage(undefined);
    setFailed(undefined);
    memory.select(undefined);
    memory.savePosition(0);
    setRequest({});
  }
  return (
    <section>
      <div className="page-heading">
        <div>
          <h1 ref={heading} tabIndex={-1}>
            Collection
          </h1>
          <p className="lede">A place for the pieces you want to remember.</p>
        </div>
        <a className="primary button" href="/inventory/new" onClick={follow}>
          <Icon name="plus" />
          Add item
        </a>
      </div>
      <form
        className="collection-search"
        role="search"
        onSubmit={(event) => {
          event.preventDefault();
          search(draft);
        }}
      >
        <label htmlFor="collection-query">Search collection</label>
        <div className="collection-search-controls">
          <input
            id="collection-query"
            type="search"
            value={draft}
            aria-describedby="collection-search-help"
            onChange={(event) => setDraft(event.target.value)}
          />
          <button className="primary" type="submit">
            Search
          </button>
          <button
            className="secondary"
            type="button"
            onClick={() => {
              setDraft('');
              search('');
            }}
          >
            Clear
          </button>
        </div>
        <p className="hint" id="collection-search-help">
          Find words in a name, notes, or location. Up to 200 characters.
        </p>
      </form>
      {loading ? (
        <p role="status">
          {page ? 'Loading more items…' : 'Loading collection…'}
        </p>
      ) : null}
      {failed ? (
        <div role="alert">
          <p>
            {failed === 400
              ? 'The search could not be accepted. Use up to 200 characters and remove unsupported characters.'
              : 'We could not load the collection. ' +
                (page
                  ? 'The items below are still available.'
                  : 'Please try again.')}
          </p>
          <button
            className="secondary"
            onClick={() => {
              setFailed(undefined);
              setRequest({ cursor: page?.nextCursor ?? undefined });
            }}
          >
            Retry
          </button>
        </div>
      ) : null}
      {page?.items.length === 0 && !loading && !failed ? (
        query ? (
          <div className="empty-state" role="status">
            <h2>No matches</h2>
            <p>Try different words or clear the search.</p>
          </div>
        ) : (
          <div className="empty-state">
            <span className="photo-placeholder" aria-hidden="true">
              <Icon name="image" />
            </span>
            <h2>Your collection starts here</h2>
            <p>
              Add your first item with just a name. Notes and a location can
              help tell its story.
            </p>
          </div>
        )
      ) : null}
      {page?.items.length ? (
        <>
          {!loading && !failed ? (
            <p role="status">
              {page.items.length} {query ? 'matching ' : ''}
              {page.items.length === 1 ? 'item' : 'items'} loaded
              {page.nextCursor ? '; more available.' : '.'}
            </p>
          ) : null}
          <div
            className="collection-view"
            role="group"
            aria-label="Collection view"
          >
            {(['grid', 'list'] as const).map((mode) => (
              <button
                key={mode}
                type="button"
                aria-pressed={view === mode}
                onClick={() => setView(mode)}
              >
                <Icon name={mode} />
                {mode === 'grid' ? 'Grid' : 'List'}
              </button>
            ))}
          </div>
          <ul className="collection-list" data-view={view}>
            {page.items.map((item) => (
              <li key={item.id}>
                <a
                  href={'/inventory/' + item.id}
                  ref={(node) => {
                    if (node) links.current.set(item.id, node);
                    else links.current.delete(item.id);
                  }}
                  onClick={(event) => {
                    memory.select(item.id);
                    memory.scrollY = window.scrollY;
                    follow(event);
                  }}
                >
                  <ItemPhoto
                    url={item.photo?.thumbnailUrl}
                    name={item.name}
                    onAuthLost={onAuthLost}
                  />
                  <span>
                    <strong className="item-title">{item.name}</strong>
                    <small className="item-location">
                      <Icon name="location" />
                      {item.location ?? 'No location recorded'}
                    </small>
                  </span>
                  <Icon name="chevron" />
                </a>
              </li>
            ))}
          </ul>
        </>
      ) : null}
      {page?.nextCursor ? (
        <button
          className="secondary"
          disabled={loading}
          onClick={() => {
            setFailed(undefined);
            setRequest({ cursor: page.nextCursor! });
          }}
        >
          Load more
        </button>
      ) : null}
    </section>
  );
}
export function ItemDetails({
  id,
  onDirtyChange,
  follow,
  onAuthLost,
  memory,
}: Props & {
  id: string;
  memory?: CollectionMemory;
  onDirtyChange(value: boolean, uncertain: boolean): void;
}) {
  const [item, setItem] = useState<ItemDetail>();
  const [editing, setEditing] = useState(false);
  const [archiving, setArchiving] = useState(false);
  const archiveButton = useRef<HTMLButtonElement>(null);
  const [photoDirty, setPhotoDirty] = useState(false);
  const [savedMessage, setSavedMessage] = useState(false);
  const editButton = useRef<HTMLButtonElement>(null);
  const photoDirtyChange = useCallback(
    (dirty: boolean, uncertain: boolean) => {
      setPhotoDirty(dirty);
      onDirtyChange(dirty, uncertain);
    },
    [onDirtyChange],
  );
  const currentId = useRef(id);
  const [failed, setFailed] = useState<number>();
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let current = true;
    currentId.current = id;
    void getItem(id).then(
      (result) => {
        if (current) {
          setItem(result);
          if (result.archivedAtUtc) memory?.invalidate();
          memory?.updatePhoto(id, result.photo);
          setFailed(undefined);
        }
      },
      (error) => {
        if (!current) return;
        if (
          error instanceof ApiError &&
          (error.status === 401 || error.status === 403)
        )
          onAuthLost();
        if (error instanceof ApiError && error.status === 404)
          memory?.removeUnavailable(id);
        setFailed(error instanceof ApiError ? error.status : 500);
      },
    );
    return () => {
      current = false;
      currentId.current = '';
    };
  }, [id, retry, onAuthLost, memory]);
  return (
    <section className="editor">
      <a className="text-link back-link" href="/inventory" onClick={follow}>
        <Icon name="back" />
        Back to collection
      </a>
      {failed ? (
        <div role="alert">
          <h1>{failed === 404 ? 'Item not found' : 'Item unavailable'}</h1>
          <p>
            {failed === 404
              ? 'This item is not available in your collection.'
              : 'We could not load this item.'}
          </p>
          {failed !== 404 ? (
            <button
              className="secondary"
              onClick={() => setRetry((value) => value + 1)}
            >
              Retry
            </button>
          ) : null}
        </div>
      ) : item?.id === id ? (
        <div className="detail-surface">
          <div className="item-identity">
            <span className="photo-placeholder" aria-hidden="true">
              <Icon name="image" />
            </span>
            <h1 className="item-title">{item.name}</h1>
          </div>
          {savedMessage ? (
            <p role="status">Current saved record loaded.</p>
          ) : null}
          {item.archivedAtUtc ? (
            <p role="status">
              <strong>Archived</strong> —{' '}
              <time dateTime={item.archivedAtUtc}>
                {new Date(item.archivedAtUtc).toLocaleString()}
              </time>
              . This record is read-only.
            </p>
          ) : null}
          {archiving ? (
            <ArchiveItem
              item={item}
              onDirtyChange={onDirtyChange}
              onAuthLost={onAuthLost}
              invalidate={() => memory?.invalidate()}
              onUnavailable={() => {
                memory?.removeUnavailable(id);
                setFailed(404);
                setArchiving(false);
              }}
              onCancel={() => {
                setArchiving(false);
                requestAnimationFrame(() => archiveButton.current?.focus());
              }}
              onCurrent={(current) => {
                setItem(current);
                setArchiving(false);
                setSavedMessage(true);
                onDirtyChange(false, false);
              }}
            />
          ) : null}
          {editing ? (
            <DetailEditor
              key={item.id}
              item={item}
              onAuthLost={onAuthLost}
              onDirtyChange={onDirtyChange}
              onRecordMayHaveChanged={() => memory?.invalidate()}
              onCancel={(current) => {
                if (current) {
                  setItem(current);
                  memory?.invalidate();
                }
                setEditing(false);
                onDirtyChange(false, false);
                requestAnimationFrame(() => editButton.current?.focus());
              }}
              onSaved={(saved) => {
                setItem(saved);
                setEditing(false);
                setSavedMessage(true);
                memory?.invalidate();
                onDirtyChange(false, false);
                requestAnimationFrame(() => editButton.current?.focus());
              }}
            />
          ) : (
            <>
              {!item.archivedAtUtc ? (
                <div className="button-row record-actions">
                  <button
                    ref={editButton}
                    className="secondary"
                    disabled={photoDirty || archiving}
                    onClick={() => {
                      setEditing(true);
                      setSavedMessage(false);
                    }}
                  >
                    Edit details
                  </button>
                  <button
                    ref={archiveButton}
                    className="secondary danger"
                    disabled={photoDirty || archiving}
                    onClick={() => {
                      setArchiving(true);
                      setSavedMessage(false);
                    }}
                  >
                    Archive record
                  </button>
                </div>
              ) : null}
              <PhotoEditor
                key={item.id}
                item={item}
                disabled={archiving}
                onAuthLost={onAuthLost}
                onDirtyChange={photoDirtyChange}
                onPhotoChanged={() => {
                  memory?.updatePhoto(id, null);
                }}
                reload={async () => {
                  const result = await getItem(id);
                  if (currentId.current === id) {
                    setItem(result);
                    if (result.archivedAtUtc) memory?.invalidate();
                    memory?.updatePhoto(id, result.photo);
                  }
                }}
              />
            </>
          )}
          <dl className="item-details">
            <div className="detail-field">
              <dt>Storage location</dt>
              <dd>{item.location ?? 'No location recorded'}</dd>
            </div>
            <div className="detail-field">
              <dt>Notes</dt>
              <dd className="notes">{item.notes ?? 'No notes recorded'}</dd>
            </div>
            <div className="detail-field record-metadata">
              <dt>Item identifier</dt>
              <dd className="identifier">{item.id}</dd>
            </div>
            <div className="detail-field">
              <dt>Added</dt>
              <dd>
                <time dateTime={item.createdAtUtc}>
                  {new Date(item.createdAtUtc).toLocaleString()}
                </time>
              </dd>
            </div>
          </dl>
        </div>
      ) : (
        <p role="status">Loading item…</p>
      )}
    </section>
  );
}
