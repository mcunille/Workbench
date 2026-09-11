import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import { AcquisitionConflictError, findAcquisitions, getAcquisitionItems, saveAcquisitionLink } from './acquisitions';

it('encodes discovery and archived pagination and requests private uncached reads', async () => {
  // GIVEN source text and opaque cursors containing reserved URL characters.
  const requests: Request[] = [];
  server.use(http.get('*/api/acquisitions', ({ request }) => { requests.push(request); return HttpResponse.json({ items: [], nextCursor: null }); }),
    http.get('*/api/acquisitions/fair/items', ({ request }) => { requests.push(request); return HttpResponse.json({ items: [], nextCursor: null }); }));
  // WHEN reading THEN each query reaches the intended field without cached private data.
  await findAcquisitions('Fair & gift', 'a+b/=');
  await getAcquisitionItems('fair', true, 'x+y/=');
  expect(new URL(requests[0].url).searchParams.get('search')).toBe('Fair & gift');
  expect(new URL(requests[0].url).searchParams.get('cursor')).toBe('a+b/=');
  expect(new URL(requests[1].url).searchParams.get('includeArchived')).toBe('true');
  expect(new URL(requests[1].url).searchParams.get('cursor')).toBe('x+y/=');
  expect(requests.every(request => request.cache === 'no-store' && request.credentials === 'same-origin')).toBe(true);
});
it('sends one protected conditional link command and surfaces stale state as a conflict', async () => {
  // GIVEN the full expected connection and target versions.
  const command = { expectedItemVersion: 'i1', expectedAcquisitionId: 'old', expectedAcquisitionVersion: 'a1', targetAcquisitionId: 'new', targetAcquisitionVersion: 'b1' };
  let received: unknown; let csrf: string | null = null;
  server.use(http.get('*/api/auth/antiforgery', () => HttpResponse.json({ requestToken: 'csrf-test' })),
    http.put('*/api/items/stone/acquisition-link', async ({ request }) => {
      received = await request.json(); csrf = request.headers.get('X-CSRF-TOKEN'); return HttpResponse.json({ code: 'acquisition_version_conflict' }, { status: 409 });
    }));
  // WHEN the server rejects stale state THEN do not reinterpret it as success.
  await expect(saveAcquisitionLink('stone', command)).rejects.toBeInstanceOf(AcquisitionConflictError);
  expect(received).toEqual(command); expect(csrf).toBe('csrf-test');
});
