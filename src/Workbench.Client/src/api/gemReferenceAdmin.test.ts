import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import * as gems from './gemReferenceAdmin';

const base = '*/api/beta/service-admin/gem-reference';
const selection = [{ draftId: 'draft-1', expectedDraftRowVersion: 'version-1' }];

beforeEach(() => {
  server.use(http.get('*/api/beta/service-admin/auth/antiforgery', () => HttpResponse.json({ requestToken: 'admin-token' })));
});

describe('shared curation transport', () => {
  it('uses the admin browse/detail/draft routes with server filters and cursor', async () => {
    // GIVEN shared admin endpoints with a cursor page and stable detail/draft IDs.
    let query: URLSearchParams | undefined;
    let cursor: string | null = null;
    server.use(
      http.get(base, ({ request }) => { query = new URL(request.url).searchParams; return HttpResponse.json({ entries: [], nextCursor: 'next' }); }),
      http.get(`${base}/entry-1`, () => HttpResponse.json({ id: 'entry-1', commonName: 'Ruby' })),
      http.get(`${base}/drafts`, ({ request }) => { cursor = new URL(request.url).searchParams.get('cursor'); return HttpResponse.json({ drafts: [], nextCursor: null }); }),
      http.get(`${base}/drafts/draft-1`, () => HttpResponse.json({ id: 'draft-1', rowVersion: 'version-1' })),
    );
    // WHEN the caller browses a filtered page and retrieves detail and draft content.
    const page = await gems.browseSharedGems({ query: 'Ruby', materialKind: 'Mineral', group: 'Corundum', cursor: 'page-2' });
    const detail = await gems.getSharedGem('entry-1');
    await gems.listGemDrafts('draft-page-2');
    const draft = await gems.getGemDraft('draft-1');
    // THEN the admin routes preserve query parameters, server cursors, and response versions.
    expect(Object.fromEntries(query!)).toEqual({ query: 'Ruby', materialKind: 'Mineral', group: 'Corundum', cursor: 'page-2' });
    expect(page.nextCursor).toBe('next');
    expect(detail.commonName).toBe('Ruby');
    expect(cursor).toBe('draft-page-2');
    expect(draft.rowVersion).toBe('version-1');
  });

  it('serializes mutations with independent admin antiforgery and explicit versions', async () => {
    // GIVEN endpoints that require the admin token and capture mutation bodies.
    const received: { token: string | null; body: unknown }[] = [];
    const capture = async ({ request }: { request: Request }) => {
      received.push({ token: request.headers.get('X-CSRF-TOKEN'), body: await request.json() });
      return HttpResponse.json({ id: 'draft-1', entries: [], code: 'published', requestId: 'attempt-1', review: [] });
    };
    server.use(http.put(`${base}/drafts/draft-1`, capture), http.post(`${base}/review`, capture), http.post(`${base}/publish`, capture));
    const save: gems.GemReferenceDraftSaveRequest = { entryId: 'entry-1', content: { id: 'entry-1', commonName: 'Ruby', materialKind: 'Mineral', group: null, species: null, variety: null, description: null, aliases: [], sources: [], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null }, expectedDraftRowVersion: 'version-1', expectedPublishedRowVersion: 'published-1' };
    // WHEN a saved draft is reviewed and published.
    await gems.saveGemDraft('draft-1', save);
    await gems.reviewGemDrafts(selection);
    await gems.publishGemDrafts({ requestId: 'attempt-1', drafts: selection });
    // THEN each request uses admin CSRF and carries the original content/selection/version.
    expect(received).toEqual([
      { token: 'admin-token', body: save },
      { token: 'admin-token', body: { drafts: selection } },
      { token: 'admin-token', body: { requestId: 'attempt-1', drafts: selection } },
    ]);
  });

  it.each([409, 422])('returns structured terminal publication outcomes for %s', async (status) => {
    // GIVEN a rejected batch with its durable outcome and per-entry review.
    const outcome = { requestId: 'attempt-1', code: status === 409 ? 'stale' : 'invalid', entries: [], review: [{ draftId: 'draft-1', errors: { CommonName: ['Required'] } }] };
    server.use(http.post(`${base}/publish`, () => HttpResponse.json(outcome, { status })));
    // WHEN publication is attempted THEN callers receive the outcome for recovery instead of a generic error.
    await expect(gems.publishGemDrafts({ requestId: 'attempt-1', drafts: selection })).resolves.toEqual(outcome);
  });

  it('maps only missing publication outcomes to null and preserves other failures', async () => {
    // GIVEN no recorded attempt and unavailable/authentication failures on other endpoints.
    server.use(
      http.get(`${base}/publications/unknown`, () => new HttpResponse(null, { status: 404 })),
      http.get(`${base}/publications/recorded`, () => HttpResponse.json({ requestId: 'recorded', code: 'published', entries: [{ entryId: 'entry-1', rowVersion: 'published-2' }], review: [] })),
      http.get(`${base}/publications/unavailable`, () => new HttpResponse(null, { status: 503 })),
      http.get(`${base}/missing`, () => new HttpResponse(null, { status: 404 })),
      http.get(base, () => new HttpResponse(null, { status: 401 })),
      http.put(`${base}/drafts/draft-1`, () => HttpResponse.json({ title: 'Draft changed', errors: { rowVersion: ['Stale'] } }, { status: 409 })),
    );
    // WHEN those operations run THEN missing outcomes remain distinguishable from failed reads/writes.
    await expect(gems.getGemPublication('unknown')).resolves.toBeNull();
    await expect(gems.getGemPublication('recorded')).resolves.toMatchObject({ code: 'published', entries: [{ entryId: 'entry-1', rowVersion: 'published-2' }] });
    await expect(gems.getGemPublication('unavailable')).rejects.toMatchObject({ status: 503 });
    await expect(gems.getSharedGem('missing')).rejects.toMatchObject({ status: 404 });
    await expect(gems.browseSharedGems({})).rejects.toMatchObject({ status: 401 });
    await expect(gems.saveGemDraft('draft-1', {} as gems.GemReferenceDraftSaveRequest)).rejects.toMatchObject({ status: 409, problem: { title: 'Draft changed', errors: { rowVersion: ['Stale'] } } });
  });
});
