import { clearPendingPublication, readPendingPublication, writePendingPublication } from './publicationAttempt';
const request = { requestId: '11111111-1111-4111-8111-111111111111', drafts: [{ draftId: '22222222-2222-4222-8222-222222222222', expectedDraftRowVersion: 'version' }] };
beforeEach(() => sessionStorage.clear());
describe('pending publication storage', () => {
  it('recovers only identity and immutable selection for the original account', () => {
    // GIVEN a request that must survive reload without retaining editorial content.
    const supplied = { ...request, password: 'synthetic-secret', content: 'unsaved', drafts: request.drafts.map((draft) => ({ ...draft })) };
    writePendingPublication('admin-a', supplied);
    supplied.drafts[0].expectedDraftRowVersion = 'later-version';
    // WHEN the original account reloads THEN the exact saved body is recovered without extra fields.
    expect(readPendingPublication('admin-a')).toEqual(request);
    const stored = sessionStorage.getItem(sessionStorage.key(0)!)!;
    expect(stored).not.toContain('synthetic-secret');
    expect(stored).not.toContain('unsaved');
    // AND another account cannot recover or clear that receipt.
    expect(readPendingPublication('admin-b')).toBeNull();
    clearPendingPublication('admin-b');
    expect(readPendingPublication('admin-a')).toEqual(request);
    clearPendingPublication('admin-a');
    expect(readPendingPublication('admin-a')).toBeNull();
  });
  it.each([[], Array.from({ length: 51 }, (_, index) => ({ draftId: `22222222-2222-4222-8222-${String(index).padStart(12, '0')}`, expectedDraftRowVersion: 'v' })), [request.drafts[0], request.drafts[0]], [{ draftId: 'invalid', expectedDraftRowVersion: '' }]].map((drafts) => ({ drafts })))('rejects malformed or out-of-bounds selection before storage (%j)', ({ drafts }) => {
    // GIVEN a malformed request WHEN it is persisted THEN publication identity cannot be stored.
    expect(() => writePendingPublication('admin', { ...request, drafts })).toThrow();
    expect(readPendingPublication('admin')).toBeNull();
  });
  it('ignores malformed stored data', () => {
    // GIVEN a receipt written by an older or corrupted tab.
    writePendingPublication('admin', request);
    const key = sessionStorage.key(0)!;
    sessionStorage.setItem(key, '{invalid');
    // WHEN recovering THEN no invalid request can be retried.
    expect(readPendingPublication('admin')).toBeNull();
  });
});
