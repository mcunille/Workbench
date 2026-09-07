// @vitest-environment node
import { vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
vi.stubGlobal('window', { location: { origin: 'http://localhost:3000' } });
const { putItemPhoto, removeItemPhoto, getPhoto } = await import('./items');
it('sends prepared bytes and concurrency command as multipart with CSRF', async () => {
  // GIVEN prepared bytes and an authenticated mutation token.
  let received = '';
  let csrf: string | null = null;
  server.use(
    http.get('*/api/auth/antiforgery', () =>
      HttpResponse.json({ requestToken: 'photo-csrf' }),
    ),
    http.put('*/api/items/item/photo', async ({ request }) => {
      received = await request.text();
      csrf = request.headers.get('X-CSRF-TOKEN');
      return HttpResponse.json({
        requestId: 'request',
        version: 'next',
        photoId: 'photo',
      });
    }),
  );
  // WHEN uploading THEN the server gets the exact prepared bytes and command.
  await putItemPhoto(
    'item',
    new Blob(['small'], { type: 'image/webp' }),
    'request',
    'version',
  );
  expect(received).toContain('name="requestId"' + '\r\n\r\nrequest');
  expect(received).toContain('name="expectedVersion"' + '\r\n\r\nversion');
  expect(received).toContain('name="file"; filename="photograph.webp"');
  expect(received).toContain('small');
  expect(csrf).toBe('photo-csrf');
});
it('preserves conflict and image authentication failure statuses', async () => {
  // GIVEN conflict and an expired image session.
  server.use(
    http.get('*/api/auth/antiforgery', () =>
      HttpResponse.json({ requestToken: 'photo-csrf' }),
    ),
    http.delete(
      '*/api/items/item/photo',
      () => new HttpResponse(null, { status: 409 }),
    ),
    http.get('*/photo', () => new HttpResponse(null, { status: 401 })),
  );
  // WHEN calling THEN callers can reconcile or end the session explicitly.
  await expect(
    removeItemPhoto('item', 'request', 'version'),
  ).rejects.toMatchObject({ status: 409 });
  await expect(getPhoto('/photo')).rejects.toMatchObject({ status: 401 });
});
