import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import * as api from '../../api/gemReferenceAdmin';
import { GemEditor } from './GemEditor';

vi.mock('../../api/gemReferenceAdmin', async (original) => ({ ...await original<typeof import('../../api/gemReferenceAdmin')>(), getGemDraft: vi.fn(), getSharedGem: vi.fn(), saveGemDraft: vi.fn() }));
const content: api.GemReferenceContent = { id: 'entry', commonName: 'Ruby', materialKind: 'mineral', group: null, species: 'Corundum', variety: null, description: 'Red corundum.', aliases: [], sources: [{ id: 'source', field: 'commonName', title: 'Handbook', publisher: 'Institute', url: null, citation: 'Volume 1', accessedOn: null, reviewedOn: '2026-09-01' }], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null };
function draft(version = 'draft-v1', values = content): api.GemReferenceDraftResponse {
  return { id: 'draft', entryId: values.id, content: values, rowVersion: version, expectedPublishedRowVersion: 'published-v1', createdBy: 'admin', updatedBy: 'admin', createdAtUtc: '2026-10-01T00:00:00Z', updatedAtUtc: '2026-10-02T00:00:00Z', errors: {} };
}
function published(version = 'published-v1'): api.GemReferenceDetailResponse {
  return { ...content, rowVersion: version, layer: 'Shared', sourceAssertions: content.sources.map((source) => ({ ...source, attribution: 'Shared' })), retirement: { isRetired: false, explanation: null, redirectEntryId: null } };
}
const callbacks = { onDirtyChange: vi.fn(), onAuthLost: vi.fn(), onSaved: vi.fn() };
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getGemDraft).mockResolvedValue(draft());
  vi.mocked(api.getSharedGem).mockResolvedValue(published());
  vi.mocked(api.saveGemDraft).mockImplementation(async (id, request) => ({ ...draft('draft-v2', request.content), id, entryId: request.entryId, expectedPublishedRowVersion: request.expectedPublishedRowVersion }));
});

describe('gem editor', () => {
  it('requires a confirmed save after reconciling versions without changing content', async () => {
    // GIVEN a saved unchanged draft whose published baseline has advanced.
    vi.mocked(api.getSharedGem).mockResolvedValue(published('published-v3'));
    vi.mocked(api.saveGemDraft).mockRejectedValueOnce(new api.GemReferenceAdminApiError(409, {}));
    render(<GemEditor {...callbacks} draftId="draft" />);
    await screen.findByText('Draft saved. Publication requires review.');
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    // WHEN the admin keeps identical content with current versions.
    fireEvent.click(await screen.findByRole('button', { name: 'Keep my edits with current versions' }));
    // THEN the pending baseline is visibly unsaved and protected from departure.
    expect(screen.getByText('Unsaved changes')).toBeVisible();
    expect(screen.queryByText('Draft saved. Publication requires review.')).not.toBeInTheDocument();
    expect(callbacks.onDirtyChange).toHaveBeenLastCalledWith(true, false);
    // WHEN the rebase is persisted THEN the saved state and navigation guard clear.
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await screen.findByText('Draft saved. Publication requires review.');
    expect(callbacks.onDirtyChange).toHaveBeenLastCalledWith(false, false);
    expect(api.saveGemDraft).toHaveBeenLastCalledWith('draft', expect.objectContaining({ expectedPublishedRowVersion: 'published-v3', content }));
  });
  it('does not navigate back to a discarded editor when an outstanding save completes', async () => {
    // GIVEN an outstanding draft save and an explicit departure from the editor.
    let confirm!: (value: api.GemReferenceDraftResponse) => void;
    vi.mocked(api.saveGemDraft).mockImplementation(() => new Promise((resolve) => { confirm = resolve; }));
    const view = render(<GemEditor {...callbacks} draftId="draft" />);
    await screen.findByLabelText('Common name');
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    view.unmount();
    // WHEN the server confirms after departure THEN no stale callback redirects the new view.
    await act(async () => { confirm(draft('draft-v2')); });
    expect(callbacks.onSaved).not.toHaveBeenCalled();
  });
  it('creates stable entry, draft and source identities and reloads the confirmed draft', async () => {
    // GIVEN a new unsaved reference.
    const view = render(<GemEditor {...callbacks} />);
    // WHEN a name and supporting source are entered and saved twice.
    fireEvent.change(screen.getByLabelText('Common name'), { target: { value: 'Emerald' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add source' }));
    fireEvent.change(screen.getByLabelText('Source 1 title'), { target: { value: 'Handbook' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await screen.findByText('Draft saved. Publication requires review.');
    const [id, first] = vi.mocked(api.saveGemDraft).mock.calls[0];
    fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'Green beryl.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(api.saveGemDraft).toHaveBeenCalledTimes(2));
    // THEN saves retain all identities and advance only the confirmed draft version.
    expect(first.entryId).toMatch(/^[0-9a-f-]{36}$/);
    expect(first.content.id).toBe(first.entryId);
    expect(first.content.sources[0].id).toMatch(/^[0-9a-f-]{36}$/);
    expect(api.saveGemDraft).toHaveBeenLastCalledWith(id, expect.objectContaining({ entryId: first.entryId, expectedDraftRowVersion: 'draft-v2', content: expect.objectContaining({ sources: first.content.sources }) }));
    view.unmount();
    vi.mocked(api.getGemDraft).mockResolvedValue({ ...draft('draft-v2', first.content), id, entryId: first.entryId });
    // WHEN the saved URL is reopened THEN content and assertion identity remain persisted.
    render(<GemEditor {...callbacks} draftId={id} />);
    expect(await screen.findByLabelText('Common name')).toHaveValue('Emerald');
    expect(screen.getByLabelText('Source 1 title')).toHaveValue('Handbook');
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(api.saveGemDraft).toHaveBeenCalledTimes(3));
    expect(api.saveGemDraft).toHaveBeenLastCalledWith(id, expect.objectContaining({ content: expect.objectContaining({ id: first.entryId, sources: first.content.sources }) }));
  });

  it.each([new api.GemReferenceAdminApiError(400, { errors: { commonName: ['Review the common name.'] } }), new Error('Network')])('keeps unsaved input after a rejected or uncertain save (%s)', async (error) => {
    // GIVEN local corrections and a failing save.
    vi.mocked(api.saveGemDraft).mockRejectedValue(error);
    render(<GemEditor {...callbacks} draftId="draft" />);
    await screen.findByLabelText('Common name');
    fireEvent.change(screen.getByLabelText('Common name'), { target: { value: 'My ruby' } });
    // WHEN saving fails THEN input remains editable and the error summary is focused.
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    const summary = await screen.findByRole('alert');
    expect(summary).toHaveFocus();
    expect(screen.getByLabelText('Common name')).toHaveValue('My ruby');
    expect(screen.getByRole('button', { name: 'Save draft' })).toBeEnabled();
    expect(screen.queryByText('Draft saved. Publication requires review.')).not.toBeInTheDocument();
  });

  it('saves incomplete retirement and citations while showing server field errors', async () => {
    // GIVEN a draft missing required citations and retirement justification.
    vi.mocked(api.saveGemDraft).mockImplementation(async (_id, request) => ({ ...draft('draft-v2', request.content), errors: { sources: ['Every populated field needs a citation.'], retirement: ['Supply an explanation or redirect.'] } }));
    render(<GemEditor {...callbacks} draftId="draft" />);
    await screen.findByLabelText('Common name');
    fireEvent.click(screen.getByLabelText('Retire entry'));
    // WHEN saving incomplete content THEN it persists as a draft with publication errors.
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Every populated field needs a citation.');
    expect(screen.getByRole('alert')).toHaveTextContent('Supply an explanation or redirect.');
    expect(screen.getByText('Draft saved. Publication requires review.')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Publish' })).not.toBeInTheDocument();
    expect(api.saveGemDraft).toHaveBeenCalledWith('draft', expect.objectContaining({ content: expect.objectContaining({ isRetired: true, retirementExplanation: null, redirectEntryId: null }) }));
  });

  it('replaces required-citation errors only after a corrected draft is confirmed', async () => {
    // GIVEN a confirmed draft whose publication validation rejects missing citations.
    vi.mocked(api.saveGemDraft).mockResolvedValueOnce({ ...draft('draft-v2'), errors: { sources: ['Every populated field needs a citation.'] } });
    render(<GemEditor {...callbacks} draftId="draft" />);
    await screen.findByLabelText('Common name');
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Every populated field needs a citation.');
    // WHEN the citation is corrected and saved THEN the confirmed valid response clears the old error.
    fireEvent.change(screen.getByLabelText('Source 1 publication citation'), { target: { value: 'Volume 2' } });
    expect(screen.queryByText('Draft saved. Publication requires review.')).not.toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('Every populated field needs a citation.');
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await screen.findByText('Draft saved. Publication requires review.');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(api.saveGemDraft).toHaveBeenLastCalledWith('draft', expect.objectContaining({ expectedDraftRowVersion: 'draft-v2', content: expect.objectContaining({ sources: [expect.objectContaining({ id: 'source', citation: 'Volume 2' })] }) }));
  });

  it('retains local changes alongside current draft and published content until explicit reconciliation', async () => {
    // GIVEN local changes based on an older persisted version.
    vi.mocked(api.getGemDraft).mockResolvedValueOnce(draft()).mockResolvedValue(draft('draft-v3', { ...content, commonName: 'Other admin ruby' }));
    vi.mocked(api.getSharedGem).mockResolvedValue(published('published-v3'));
    vi.mocked(api.saveGemDraft).mockRejectedValueOnce(new api.GemReferenceAdminApiError(409, { code: 'stale_entry' }));
    render(<GemEditor {...callbacks} draftId="draft" />);
    await screen.findByLabelText('Common name');
    fireEvent.change(screen.getByLabelText('Common name'), { target: { value: 'My ruby' } });
    // WHEN the save conflicts THEN current content appears beside retained local values and save is blocked.
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    const conflict = await screen.findByRole('region', { name: 'Resolve changed versions' });
    expect(within(conflict).getByText('Other admin ruby')).toBeVisible();
    expect(within(conflict).getByText('Ruby')).toBeVisible();
    expect(screen.getByLabelText('Common name')).toHaveValue('My ruby');
    expect(screen.getByRole('button', { name: 'Save draft' })).toBeDisabled();
    // WHEN retaining local content is explicitly chosen THEN the next save uses refreshed versions.
    fireEvent.click(screen.getByRole('button', { name: 'Keep my edits with current versions' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(api.saveGemDraft).toHaveBeenCalledTimes(2));
    expect(api.saveGemDraft).toHaveBeenLastCalledWith('draft', expect.objectContaining({ expectedDraftRowVersion: 'draft-v3', expectedPublishedRowVersion: 'published-v3', content: expect.objectContaining({ commonName: 'My ruby' }) }));
  });

  it('allows explicitly replacing local content with the latest saved draft', async () => {
    // GIVEN a conflicting newer saved draft.
    vi.mocked(api.getGemDraft).mockResolvedValueOnce(draft()).mockResolvedValue(draft('draft-v3', { ...content, commonName: 'Server ruby' }));
    vi.mocked(api.saveGemDraft).mockRejectedValueOnce(new api.GemReferenceAdminApiError(409, {}));
    render(<GemEditor {...callbacks} draftId="draft" />);
    await screen.findByLabelText('Common name');
    fireEvent.change(screen.getByLabelText('Common name'), { target: { value: 'Local ruby' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    // WHEN loading the current saved draft is explicitly chosen THEN local content is replaced, not silently merged.
    fireEvent.click(await screen.findByRole('button', { name: 'Use current saved draft' }));
    expect(screen.getByLabelText('Common name')).toHaveValue('Server ruby');
    expect(callbacks.onDirtyChange).toHaveBeenLastCalledWith(false, false);
  });

  it('blocks writes after session loss, preserves inputs and refreshes versions after reauthentication', async () => {
    // GIVEN local edits and an expired service-admin session.
    vi.mocked(api.saveGemDraft).mockRejectedValueOnce(new api.GemReferenceAdminApiError(401, {}));
    const view = render(<GemEditor {...callbacks} draftId="draft" sessionRevision={0} />);
    await screen.findByLabelText('Common name');
    fireEvent.change(screen.getByLabelText('Common name'), { target: { value: 'Unsaved ruby' } });
    // WHEN saving discovers expiry THEN writes stop and local content stays mounted.
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(callbacks.onAuthLost).toHaveBeenCalledOnce());
    expect(screen.getByRole('button', { name: 'Save draft' })).toBeDisabled();
    expect(screen.getByLabelText('Common name')).toHaveValue('Unsaved ruby');
    vi.mocked(api.getGemDraft).mockResolvedValue(draft('draft-v4'));
    // WHEN the same account reauthenticates THEN fresh versions require reconciliation before another write.
    view.rerender(<GemEditor {...callbacks} draftId="draft" sessionRevision={1} />);
    await screen.findByRole('region', { name: 'Resolve changed versions' });
    expect(screen.getByRole('button', { name: 'Save draft' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Keep my edits with current versions' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(api.saveGemDraft).toHaveBeenCalledTimes(2));
    expect(api.saveGemDraft).toHaveBeenLastCalledWith('draft', expect.objectContaining({ expectedDraftRowVersion: 'draft-v4', content: expect.objectContaining({ commonName: 'Unsaved ruby' }) }));
  });
});
