import { useEffect, useState, type MouseEvent } from 'react';
import { ApiError } from '../../api/auth';
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
export function Collection({ follow, onAuthLost }: Props) {
  const [page, setPage] = useState<ItemPage>();
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let current = true;
    void getItems().then(
      (result) => {
        if (current) {
          setPage(result);
          setLoading(false);
          setFailed(false);
        }
      },
      (error) => {
        if (!current) return;
        if (
          error instanceof ApiError &&
          (error.status === 401 || error.status === 403)
        )
          onAuthLost();
        setLoading(false);
        setFailed(true);
      },
    );
    return () => {
      current = false;
    };
  }, [retry, onAuthLost]);
  async function more() {
    if (loading || !page?.nextCursor) return;
    setLoading(true);
    setFailed(false);
    try {
      const next = await getItems(page.nextCursor);
      setPage({
        items: [...page.items, ...next.items],
        nextCursor: next.nextCursor,
      });
    } catch (error) {
      if (
        error instanceof ApiError &&
        (error.status === 401 || error.status === 403)
      )
        onAuthLost();
      setFailed(true);
    } finally {
      setLoading(false);
    }
  }
  return (
    <section>
      <div className="page-heading">
        <div>
          <p className="eyebrow">Inventory</p>
          <h1>Collection</h1>
          <p className="lede">A place for the pieces you want to remember.</p>
        </div>
        <a className="primary button" href="/inventory/new" onClick={follow}>
          Add item
        </a>
      </div>
      {loading ? <p role="status">Loading collection…</p> : null}
      {failed ? (
        <div role="alert">
          <p>
            We could not load the collection.{' '}
            {page
              ? 'The items below are still available.'
              : 'Please try again.'}
          </p>
          <button
            className="secondary"
            onClick={() => {
              if (page) void more();
              else {
                setLoading(true);
                setRetry((value) => value + 1);
              }
            }}
          >
            Retry
          </button>
        </div>
      ) : null}
      {page?.items.length === 0 && !loading && !failed ? (
        <div className="empty-state">
          <span className="photo-placeholder" aria-hidden="true">
            ◇
          </span>
          <h2>Your collection starts here</h2>
          <p>
            Add your first item with just a name. Notes and a location can help
            tell its story.
          </p>
        </div>
      ) : null}
      {page?.items.length ? (
        <ul className="collection-list">
          {page.items.map((item) => (
            <li key={item.id}>
              <a href={`/inventory/${item.id}`} onClick={follow}>
                <span className="photo-placeholder" aria-hidden="true">
                  ◇
                </span>
                <span>
                  <strong className="item-title">{item.name}</strong>
                  <small>{item.location ?? 'No location recorded'}</small>
                </span>
                <span aria-hidden="true">→</span>
              </a>
            </li>
          ))}
        </ul>
      ) : null}
      {page?.nextCursor ? (
        <button
          className="secondary"
          disabled={loading}
          onClick={() => void more()}
        >
          Load more
        </button>
      ) : null}
    </section>
  );
}
export function ItemDetails({
  id,
  follow,
  onAuthLost,
}: Props & { id: string }) {
  const [item, setItem] = useState<ItemDetail>();
  const [failed, setFailed] = useState<number>();
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let current = true;
    void getItem(id).then(
      (result) => {
        if (current) {
          setItem(result);
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
        setFailed(error instanceof ApiError ? error.status : 500);
      },
    );
    return () => {
      current = false;
    };
  }, [id, retry, onAuthLost]);
  return (
    <section className="editor">
      <a className="text-link back-link" href="/inventory" onClick={follow}>
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
      ) : item ? (
        <>
          <h1 className="item-title">{item.name}</h1>
          <dl className="item-details">
            <dt>Storage location</dt>
            <dd>{item.location ?? 'No location recorded'}</dd>
            <dt>Notes</dt>
            <dd className="notes">{item.notes ?? 'No notes recorded'}</dd>
            <dt>Item identifier</dt>
            <dd className="identifier">{item.id}</dd>
            <dt>Added</dt>
            <dd>
              <time dateTime={item.createdAtUtc}>
                {new Date(item.createdAtUtc).toLocaleString()}
              </time>
            </dd>
          </dl>
        </>
      ) : (
        <p role="status">Loading item…</p>
      )}
    </section>
  );
}
