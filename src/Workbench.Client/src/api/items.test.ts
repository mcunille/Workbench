import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import { createItem, getItems, ItemValidationError } from './items';
const request = {
  creationRequestId: '11111111-1111-1111-1111-111111111111',
  name: 'Sapphire',
  notes: null,
  location: null,
};
describe('Inventory API', () => {
  it('encodes literal search text and the page cursor independently', async () => {
    // GIVEN literal punctuation that would be special in a query string.
    server.use(
      http.get('*/api/items', ({ request }) => {
        const query = new URL(request.url).searchParams;
        expect(query.get('q')).toBe('stone %_[]\\ &+#');
        expect(query.get('cursor')).toBe('opaque+/=');
        return HttpResponse.json({ items: [], nextCursor: null });
      }),
    );
    // WHEN loading a search page THEN neither value is altered in transport.
    await getItems('opaque+/=', 'stone %_[]\\ &+#');
  });
  it('sends the draft and antiforgery token through the generated contract', async () => {
    // GIVEN a valid antiforgery token and a saved response
    let received: unknown;
    let csrf: string | null = null;
    server.use(
      http.get('*/api/auth/antiforgery', () =>
        HttpResponse.json({ requestToken: 'csrf-test' }),
      ),
      http.post('*/api/items', async ({ request: incoming }) => {
        received = await incoming.json();
        csrf = incoming.headers.get('X-CSRF-TOKEN');
        return HttpResponse.json(
          {
            id: 'saved',
            name: 'Sapphire',
            notes: null,
            location: null,
            createdAtUtc: '2026-09-06T00:00:00Z',
          },
          { status: 201 },
        );
      }),
    );
    // WHEN creating an item THEN preserve the command and return the authoritative item
    expect((await createItem(request)).id).toBe('saved');
    expect(received).toEqual(request);
    expect(csrf).toBe('csrf-test');
  });
  it('preserves field validation and passes the opaque page cursor unchanged', async () => {
    // GIVEN authoritative validation and a cursor containing reserved characters
    server.use(
      http.post('*/api/items', () =>
        HttpResponse.json(
          { errors: { Name: ['Name is required.'] } },
          { status: 400 },
        ),
      ),
      http.get('*/api/items', ({ request }) => {
        expect(new URL(request.url).searchParams.get('cursor')).toBe(
          'opaque+/=',
        );
        return HttpResponse.json({ items: [], nextCursor: null });
      }),
    );
    // WHEN calling these endpoints THEN validation remains actionable and cursor is encoded once
    await expect(createItem(request)).rejects.toBeInstanceOf(
      ItemValidationError,
    );
    expect(await getItems('opaque+/=')).toEqual({
      items: [],
      nextCursor: null,
    });
  });
});

it('sends a checked update with antiforgery and preserves validation and conflict codes', async () => {
  // GIVEN a checked command and authoritative responses.
  const { updateItem, ItemConflictError } = await import('./items');
  const body = {
    expectedVersion: 'AAAAAAAAAAA=',
    name: 'Edited',
    notes: null,
    location: 'Tray',
  };
  let status = 200;
  server.use(
    http.get('*/api/auth/antiforgery', () =>
      HttpResponse.json({ requestToken: 'csrf-test' }),
    ),
    http.put('*/api/items/item', async ({ request }) => {
      expect(await request.json()).toEqual(body);
      expect(request.headers.get('X-CSRF-TOKEN')).toBe('csrf-test');
      return HttpResponse.json(
        status === 200
          ? { id: 'item', name: 'Edited', version: 'new' }
          : status === 400
            ? { errors: { Name: ['Required'] } }
            : { code: 'item_version_conflict' },
        { status },
      );
    }),
  );
  // WHEN saving THEN return server details and distinguish recoverable failures.
  expect(await updateItem('item', body)).toMatchObject({
    id: 'item',
    name: 'Edited',
    version: 'new',
  });
  status = 400;
  await expect(updateItem('item', body)).rejects.toBeInstanceOf(
    ItemValidationError,
  );
  status = 409;
  await expect(updateItem('item', body)).rejects.toBeInstanceOf(
    ItemConflictError,
  );
});
