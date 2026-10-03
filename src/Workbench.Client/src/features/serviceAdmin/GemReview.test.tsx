import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as api from '../../api/gemReferenceAdmin';
import { GemReview, type GemReviewProps } from './GemReview';
import { readPendingPublication, writePendingPublication } from './publicationAttempt';
vi.mock('../../api/gemReferenceAdmin', async (original) => ({ ...await original<typeof import('../../api/gemReferenceAdmin')>(), reviewGemDrafts: vi.fn(), publishGemDrafts: vi.fn(), getGemPublication: vi.fn() }));
const draftId = '22222222-2222-4222-8222-222222222222';
const selection = [{ draftId, expectedDraftRowVersion: 'draft-v1' }];
const entry = { draftId, entryId: 'entry', isStale: false, errors: {}, changes: [{ field: 'commonName', before: 'Ruby', after: 'Burmese ruby' }, { field: 'aliases', before: ['Old alias'], after: ['New alias'] }, { field: 'sources', before: [{ title: 'Old source', field: 'commonName', url: 'javascript:bad' }], after: [{ title: 'Handbook', publisher: 'Institute', field: 'commonName', citation: 'Volume 1', reviewedOn: '2026-09-01' }] }] };
const success = (requestId: string): api.GemReferencePublishOutcome => ({ requestId, code: 'published', entries: [{ entryId: 'entry', rowVersion: 'published-v2' }], review: [entry] });
const callbacks = { onPublished: vi.fn(), onEdit: vi.fn(), onAuthLost: vi.fn(), onPendingChange: vi.fn(), onDirtyChange: vi.fn() };
function show(props: Partial<GemReviewProps> = {}) { return render(<GemReview accountId="admin-a" selection={selection} {...callbacks} {...props} />); }
async function confirm() { await screen.findByText('Burmese ruby'); fireEvent.click(screen.getByLabelText('I confirm these reviewed changes should be published.')); fireEvent.click(screen.getByRole('button', { name: 'Publish 1 draft' })); }
beforeEach(() => { sessionStorage.clear(); vi.resetAllMocks(); vi.mocked(api.reviewGemDrafts).mockResolvedValue({ entries: [entry] }); vi.mocked(api.getGemPublication).mockResolvedValue(null); vi.mocked(api.publishGemDrafts).mockImplementation(async (request) => success(request.requestId)); });
describe('combined gem review', () => {
  it('renders the server field, alias and source differences as inert before/after content', async () => {
    // GIVEN a combined server review WHEN the selection opens THEN all claim values remain readable.
    show();
    expect(await screen.findByText('Burmese ruby')).toBeVisible();
    for (const value of ['Ruby', 'Old alias', 'New alias', 'Old source', 'Handbook', 'Volume 1']) expect(screen.getByText(value)).toBeVisible();
    expect(screen.queryByRole('link', { name: 'Handbook' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Publish 1 draft' })).toBeDisabled();
  });
  it.each([{ ...entry, isStale: true }, { ...entry, errors: { sources: ['A supporting citation is required.'] } }])('blocks a stale or invalid batch and allows repair (%j)', async (invalid) => {
    // GIVEN a server review that cannot publish WHEN it opens THEN confirmation cannot bypass it.
    vi.mocked(api.reviewGemDrafts).mockResolvedValue({ entries: [invalid] }); show();
    await screen.findByText('Burmese ruby');
    expect(screen.getByLabelText('I confirm these reviewed changes should be published.')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Publish 1 draft' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Edit draft' })); expect(callbacks.onEdit).toHaveBeenCalledWith(draftId);
    expect(api.publishGemDrafts).not.toHaveBeenCalled();
  });
  it('invalidates the old review and confirmation when a selected draft version changes', async () => {
    // GIVEN a confirmed review and a pending replacement review.
    const view = show(); await screen.findByText('Burmese ruby'); fireEvent.click(screen.getByLabelText('I confirm these reviewed changes should be published.'));
    vi.mocked(api.reviewGemDrafts).mockImplementation(() => new Promise(() => {}));
    // WHEN selection changes THEN the previous receipt cannot authorize publishing it.
    view.rerender(<GemReview accountId="admin-a" selection={[{ draftId, expectedDraftRowVersion: 'draft-v2' }]} {...callbacks} />);
    expect(screen.getByRole('button', { name: 'Publish 1 draft' })).toBeDisabled();
    expect(screen.queryByText('Burmese ruby')).not.toBeInTheDocument();
  });
  it('retains a lost-response body across selection changes and exact retry', async () => {
    // GIVEN a publish response lost after dispatch.
    vi.mocked(api.publishGemDrafts).mockRejectedValueOnce(new Error('Network')).mockImplementation(async (request) => success(request.requestId));
    const view = show(); await confirm(); await screen.findByRole('button', { name: 'Retry identical publication' });
    const original = readPendingPublication('admin-a')!; expect(original.drafts).toEqual(selection);
    // WHEN incoming selection changes and lookup finds no receipt THEN retry retains the original identity and body.
    view.rerender(<GemReview accountId="admin-a" selection={[{ draftId, expectedDraftRowVersion: 'new-version' }]} {...callbacks} />);
    fireEvent.click(screen.getByRole('button', { name: 'Retry identical publication' }));
    await waitFor(() => expect(callbacks.onPublished).toHaveBeenCalled());
    expect(api.publishGemDrafts).toHaveBeenNthCalledWith(2, original);
    expect(readPendingPublication('admin-a')).toBeNull();
  });
  it('recovers a committed publication by outcome lookup after reload without publishing again', async () => {
    // GIVEN a persisted uncertain attempt from the original account.
    const pending = { requestId: '11111111-1111-4111-8111-111111111111', drafts: selection }; writePendingPublication('admin-a', pending);
    vi.mocked(api.getGemPublication).mockResolvedValue(success(pending.requestId));
    // WHEN review reloads THEN its durable success is delivered and cleared.
    show(); await waitFor(() => expect(callbacks.onPublished).toHaveBeenCalledWith(success(pending.requestId)));
    expect(api.getGemPublication).toHaveBeenCalledWith(pending.requestId); expect(api.publishGemDrafts).not.toHaveBeenCalled(); expect(readPendingPublication('admin-a')).toBeNull();
  });
  it('requires fresh review and confirmation before using a new identity after terminal rejection', async () => {
    // GIVEN a terminal validation outcome.
    vi.mocked(api.publishGemDrafts).mockImplementationOnce(async (request) => ({ ...success(request.requestId), code: 'validation_failed', entries: [], review: [{ ...entry, errors: { sources: ['Repair the citation.'] } }] })).mockImplementation(async (request) => success(request.requestId));
    show(); await confirm(); expect(await screen.findByText(/Sources: Repair the citation/)).toBeVisible();
    const first = vi.mocked(api.publishGemDrafts).mock.calls[0][0]; expect(callbacks.onPublished).not.toHaveBeenCalled(); expect(readPendingPublication('admin-a')).toBeNull();
    // WHEN the editor has repaired the selection and the admin reviews again THEN a fresh confirmation permits a new request.
    fireEvent.click(screen.getByRole('button', { name: 'Review again' })); await confirm();
    await waitFor(() => expect(callbacks.onPublished).toHaveBeenCalled()); expect(vi.mocked(api.publishGemDrafts).mock.calls[1][0].requestId).not.toBe(first.requestId);
  });
  it('prevents publication when pending storage fails and offers retry', async () => {
    // GIVEN storage cannot persist the receipt before dispatch.
    const storage = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage full'); }); show(); await confirm();
    // THEN the request was not sent and the review is available for another storage attempt.
    expect(await screen.findByRole('alert')).toHaveTextContent('could not preserve'); expect(api.publishGemDrafts).not.toHaveBeenCalled(); storage.mockRestore();
    fireEvent.click(screen.getByRole('button', { name: 'Publish 1 draft' })); await waitFor(() => expect(callbacks.onPublished).toHaveBeenCalled());
  });
  it('suspends recovery after auth loss and resumes the same request after same-account sign-in', async () => {
    // GIVEN an uncertain attempt whose lookup detects session loss.
    const pending = { requestId: '11111111-1111-4111-8111-111111111111', drafts: selection }; writePendingPublication('admin-a', pending);
    vi.mocked(api.getGemPublication).mockRejectedValueOnce(new api.GemReferenceAdminApiError(401, {})); const view = show();
    await waitFor(() => expect(callbacks.onAuthLost).toHaveBeenCalled());
    view.rerender(<GemReview accountId="admin-a" selection={selection} {...callbacks} writesSuspended sessionRevision={0} />);
    expect(screen.getByRole('button', { name: 'Retry identical publication' })).toBeDisabled();
    // WHEN the same account restores its session THEN lookup resolves the original attempt.
    vi.mocked(api.getGemPublication).mockResolvedValue(success(pending.requestId)); view.rerender(<GemReview accountId="admin-a" selection={selection} {...callbacks} sessionRevision={1} />);
    await waitFor(() => expect(callbacks.onPublished).toHaveBeenCalledWith(success(pending.requestId)));
  });
  it('does not attribute an outstanding receipt to a replacement account', async () => {
    // GIVEN a publication request outstanding for one account.
    let resolve!: (value: api.GemReferencePublishOutcome) => void;
    vi.mocked(api.publishGemDrafts).mockImplementation(() => new Promise((done) => { resolve = done; })); const view = show(); await confirm();
    const pending = readPendingPublication('admin-a')!;
    // WHEN sign-out unmounts the review and another account enters THEN completion belongs only to the original account.
    view.unmount(); show({ accountId: 'admin-b', selection: [] });
    await act(async () => { resolve(success(pending.requestId)); });
    expect(callbacks.onPublished).not.toHaveBeenCalled(); expect(readPendingPublication('admin-a')).toEqual(pending); expect(readPendingPublication('admin-b')).toBeNull();
    expect(screen.queryByText('Burmese ruby')).not.toBeInTheDocument();
  });
});
