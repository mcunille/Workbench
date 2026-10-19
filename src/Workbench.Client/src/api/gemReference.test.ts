import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import { browseGems, getGem } from './gemReference';
import * as gems from './gemReference';
import type { components } from './generated';

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

it('sends tenant mutations with stable identity, composite versions and tenant antiforgery', async () => {
  // GIVEN tenant writes require antiforgery and both reference versions.
  const content: components['schemas']['GemReferenceContent'] = { id: 'stable', materialKind: 'mineral', commonName: 'My gem', group: null, species: 'Corundum', variety: null, description: null, aliases: [], sources: [], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null };
  const effectiveVersion = { sharedRowVersion: 'shared', tenantRowVersion: 'tenant' };
  const overrides = { description: { state: 'clear', value: null, sources: [] } };
  const observed: { path: string; body: unknown; csrf: string | null }[] = [];
  const handler = async ({ request }: { request: Request }) => {
    observed.push({ path: new URL(request.url).pathname, body: await request.json(), csrf: request.headers.get('X-CSRF-TOKEN') });
    return HttpResponse.json({ id: 'stable', commonName: 'Saved' });
  };
  server.use(http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'gem-csrf' })), http.post('*/api/beta/gem-reference/*', handler), http.put('*/api/beta/gem-reference/*', handler));
  // WHEN adding, updating, customizing, resetting, archiving and restoring.
  await gems.createTenantGem(content);
  await gems.updateTenantGem('stable', content, effectiveVersion);
  await gems.saveGemOverrides('stable', overrides, effectiveVersion);
  await gems.resetGem('stable', 'description', effectiveVersion);
  await gems.setTenantGemArchived('stable', true, effectiveVersion);
  await gems.setTenantGemArchived('stable', false, effectiveVersion);
  // THEN every request preserves the contract and uses tenant antiforgery.
  expect(observed.map(item => item.path.replace('/api/beta/gem-reference', ''))).toEqual(['/tenant-entries', '/tenant-entries/stable', '/stable/overrides', '/stable/reset', '/tenant-entries/stable/archive', '/tenant-entries/stable/restore']);
  expect(observed.map(item => item.body)).toEqual([content, { content, effectiveVersion }, { overrides, effectiveVersion }, { field: 'description', effectiveVersion }, { effectiveVersion }, { effectiveVersion }]);
  expect(observed.every(item => item.csrf === 'gem-csrf')).toBe(true);
});

it.each([400, 403, 404, 409])('preserves tenant write problem %s and current entry for explicit reconciliation', async status => {
  // GIVEN a rejected mutation supplies current data rather than a successful receipt.
  const problem = { code: 'stale_entry', errors: { species: ['Review species.'] }, current: { id: 'entry', commonName: 'Current' } };
  server.use(http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'csrf' })), http.post('*/api/beta/gem-reference/entry/reset', () => HttpResponse.json(problem, { status })));
  // WHEN resetting THEN status, field reasons and current data remain available.
  await expect(gems.resetGem('entry', null, { sharedRowVersion: 'shared', tenantRowVersion: null })).rejects.toMatchObject({ status, problem });
});

it('includes archived entries explicitly without losing the search cursor', async () => {
  // GIVEN archived inclusion belongs to the server query and cursor scope.
  let query: URLSearchParams | undefined;
  server.use(http.get('*/api/beta/gem-reference', ({ request }) => { query = new URL(request.url).searchParams; return HttpResponse.json({ entries: [], nextCursor: null }); }));
  // WHEN browsing THEN both archive inclusion and continuation reach the API.
  await browseGems({ includeArchived: true, cursor: 'archive-cursor' });
  expect(Object.fromEntries(query!)).toEqual({ includeArchived: 'true', cursor: 'archive-cursor' });
});
