import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import { browseGems, getGem } from './gemReference';

it('reads tenant-effective pages and preserves qualified identities and filters', async () => {
  // GIVEN effective reads distinguish shared and tenant entries with the same ID.
  let received: URLSearchParams | undefined;
  server.use(
    http.get('*/api/beta/gem-reference', ({ request }) => {
      received = new URL(request.url).searchParams;
      return HttpResponse.json({ entries: [], nextCursor: 'next' });
    }),
    http.get('*/api/beta/gem-reference/same', ({ request }) => HttpResponse.json({ commonName: new URL(request.url).searchParams.get('origin') === 'tenant' ? 'My ruby' : 'Ruby' })),
  );
  // WHEN browsing with all filters and opening each origin.
  const page = await browseGems({ query: 'ruby & red', materialKind: 'mineral', group: 'Corundum', cursor: 'opaque+/=' });
  const tenant = await getGem('same', 'tenant');
  const shared = await getGem('same', 'workbench');
  // THEN server cursors/filters and the selected identity survive transport.
  expect(Object.fromEntries(received!)).toEqual({ query: 'ruby & red', materialKind: 'mineral', group: 'Corundum', cursor: 'opaque+/=' });
  expect(page.nextCursor).toBe('next');
  expect(tenant.commonName).toBe('My ruby');
  expect(shared.commonName).toBe('Ruby');
});

it.each([400, 401, 403, 404, 503])('preserves effective read failure %s for recovery', async status => {
  // GIVEN a server problem rather than an empty successful response.
  server.use(http.get('*/api/beta/gem-reference/missing', () => HttpResponse.json({ title: 'Unavailable' }, { status })));
  // WHEN reading THEN the caller can distinguish validation/auth/not-found/transient errors.
  await expect(getGem('missing', 'tenant')).rejects.toMatchObject({ status, problem: { title: 'Unavailable' } });
});

it('cancels an obsolete effective read', async () => {
  // GIVEN cancellation before a request is sent.
  const controller = new AbortController();
  controller.abort();
  // WHEN reading THEN cancellation is preserved rather than reported as an empty catalog.
  await expect(browseGems({}, controller.signal)).rejects.toMatchObject({ name: 'AbortError' });
});
