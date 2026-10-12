import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import * as api from '../../api/gemReference';
import { GemTenantEditor } from './GemTenantEditor';
import { newGemDraft } from './gemEditorDraft';

vi.mock('../../api/gemReference', async original => ({ ...await original<typeof api>(), getGem: vi.fn(), createTenantGem: vi.fn(), updateTenantGem: vi.fn(), saveGemOverrides: vi.fn(), resetGem: vi.fn() }));
const callbacks = { onDirtyChange: vi.fn(), onAuthLost: vi.fn(), onSaved: vi.fn(), onChanged: vi.fn(), onCancel: vi.fn() };
const content = { ...newGemDraft('entry').content, commonName: 'Reference gem', species: 'Corundum' };
function entry(origin: api.GemOrigin = 'workbench', revision = 'shared-v1'): api.GemReferenceDetailResponse {
  return { ...content, origin, rowVersion: revision, layer: origin === 'tenant' ? 'tenantEntry' : 'workbenchReference', sourceAssertions: [], retirement: { isRetired: false, explanation: null, redirectEntryId: null }, workbenchContent: origin === 'workbench' ? content : null, effectiveVersion: { sharedRowVersion: origin === 'workbench' ? revision : null, tenantRowVersion: 'tenant-v1' }, overrides: {} };
}
beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } });
  vi.resetAllMocks();
  vi.mocked(api.getGem).mockResolvedValue(entry());
  vi.mocked(api.createTenantGem).mockImplementation(async value => ({ ...entry('tenant'), ...value }));
  vi.mocked(api.updateTenantGem).mockResolvedValue(entry('tenant'));
  vi.mocked(api.saveGemOverrides).mockResolvedValue(entry());
  vi.mocked(api.resetGem).mockResolvedValue(entry());
});
function mount(origin?: api.GemOrigin) { return render(<GemTenantEditor {...callbacks} id={origin ? 'entry' : undefined} origin={origin} />); }
function change(label: string, value: string) { fireEvent.change(screen.getByLabelText(label, { exact: true }), { target: { value } }); }
function save() { fireEvent.click(screen.getByRole('button', { name: 'Save changes' })); }
async function customize(field: string, value: string) {
  await screen.findByLabelText(`${field} choice`);
  change(`${field} choice`, 'replace'); change(field, value);
}

it('creates a tenant entry with a publication citation and retains all inputs on validation failure', async () => {
  // GIVEN a new personal reference may cite a publication without a URL.
  vi.mocked(api.createTenantGem).mockRejectedValueOnce(new api.GemReferenceApiError(400, { code: 'validation_failed', errors: { species: ['Species is required.'] } }));
  mount(); change('Common name', 'Personal ruby'); change('Species', 'Corundum');
  fireEvent.click(screen.getByRole('button', { name: 'Add Common name source' }));
  change('Common name source 1 title', 'Notebook'); change('Common name source 1 publisher', 'Owner'); change('Common name source 1 publication citation', 'Page 9'); change('Common name source 1 review date', '2026-10-01');
  // WHEN validation fails THEN user values and source text remain editable.
  save(); expect(await screen.findByRole('alert')).toHaveTextContent('Species is required.');
  expect(screen.getByLabelText('Common name', { exact: true })).toHaveValue('Personal ruby');
  expect(screen.getByLabelText('Common name source 1 publication citation')).toHaveValue('Page 9');
  const first = vi.mocked(api.createTenantGem).mock.calls[0][0];
  save(); await waitFor(() => expect(callbacks.onSaved).toHaveBeenCalled());
  expect(vi.mocked(api.createTenantGem).mock.calls[1][0].id).toBe(first.id);
  expect(first.sources[0]).toMatchObject({ url: null, citation: 'Page 9', field: 'commonName' });
});

it('shows Workbench citations and stages a single reset without dropping another unsaved field', async () => {
  // GIVEN two customizations and current shared species citation.
  const shared = { id: 'source', field: 'species', title: 'Current handbook', publisher: 'Institute', url: null, citation: 'Page 3', accessedOn: null, reviewedOn: '2026-10-02' };
  vi.mocked(api.getGem).mockResolvedValue({ ...entry(), description: 'Tenant note', species: 'Tenant species', workbenchContent: { ...content, sources: [shared] }, overrides: { species: { state: 'replace', value: 'Tenant species', sources: [] }, description: { state: 'replace', value: 'Tenant note', sources: [] } } });
  mount('workbench'); await screen.findByText(/Current handbook/);
  // WHEN resetting species and editing description THEN shared sources are displayed, never copied into tenant replacements.
  change('Description', 'Unsaved note'); fireEvent.click(screen.getByRole('button', { name: 'Reset Species to Workbench' })); save();
  await waitFor(() => expect(api.saveGemOverrides).toHaveBeenCalled());
  const overrides = vi.mocked(api.saveGemOverrides).mock.calls[0][1];
  expect(overrides).not.toHaveProperty('species'); expect(overrides.description).toEqual({ state: 'replace', value: 'Unsaved note', sources: [] });
});

it('retains the draft through repeated races and adopts current tokens only after explicit review', async () => {
  // GIVEN another member changed the shared base while this editor is open.
  const current = { ...entry('workbench', 'shared-v2'), description: 'Other member note', workbenchContent: { ...content, description: 'Corrected shared note' } };
  vi.mocked(api.saveGemOverrides).mockRejectedValueOnce(new api.GemReferenceApiError(409, { code: 'stale_entry', current, errors: {} })).mockRejectedValueOnce(new api.GemReferenceApiError(409, { code: 'stale_entry', current: entry('workbench', 'shared-v3'), errors: {} }));
  mount('workbench'); await customize('Description', 'My unsaved draft'); save();
  // WHEN a stale response arrives THEN both versions are visible and a save cannot silently retry.
  await screen.findByRole('region', { name: 'Review current entry' });
  expect(screen.getByLabelText('Description', { exact: true })).toHaveValue('My unsaved draft');
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  expect(screen.getByRole('region', { name: 'Review current entry' })).toHaveTextContent('Other member note');
  fireEvent.click(screen.getByRole('button', { name: 'Keep my draft with current versions' }));
  expect(api.saveGemOverrides).toHaveBeenCalledTimes(1); save();
  await screen.findByRole('region', { name: 'Review current entry' });
  expect(vi.mocked(api.saveGemOverrides).mock.calls[1][2].sharedRowVersion).toBe('shared-v2');
  expect(screen.getByLabelText('Description', { exact: true })).toHaveValue('My unsaved draft');
  fireEvent.click(screen.getByRole('button', { name: 'Use current entry' }));
  expect(screen.getByLabelText('Description choice')).toHaveValue('inherit');
});

it('keeps the edited identity when a duplicate problem supplies a different entry', async () => {
  // GIVEN duplicate identity is a field error, not permission to switch records.
  vi.mocked(api.saveGemOverrides).mockRejectedValue(new api.GemReferenceApiError(409, { code: 'duplicate_identity', current: { ...entry(), id: 'different-entry' }, errors: { identity: ['Already visible.'] } }));
  mount('workbench'); await customize('Common name', 'Collision'); save();
  // WHEN rejected THEN the conflicting entry is never offered as a new save target.
  await screen.findByText('Already visible.');
  expect(screen.queryByRole('button', { name: 'Keep my draft with current versions' })).not.toBeInTheDocument();
  expect(screen.getByLabelText('Common name', { exact: true })).toHaveValue('Collision');
});

it('preserves a multi-field repair draft until the server accepts its complete reconciliation', async () => {
  // GIVEN an upstream correction invalidated a retained species clear.
  const invalid = { ...entry(), needsReview: true, species: null, reviewReasons: { species: ['Mineral species required.'], identity: ['Review name collision.'] }, overrides: { species: { state: 'clear', value: null, sources: [] } } };
  vi.mocked(api.getGem).mockResolvedValue(invalid);
  vi.mocked(api.saveGemOverrides).mockRejectedValueOnce(new api.GemReferenceApiError(409, { code: 'needs_review', current: invalid, errors: invalid.reviewReasons }));
  mount('workbench'); await screen.findByText('Mineral species required.');
  await customize('Description', 'Unrelated note'); save();
  await waitFor(() => expect(api.saveGemOverrides).toHaveBeenCalledTimes(1));
  await screen.findByRole('alert');
  expect(screen.getByLabelText('Description', { exact: true })).toHaveValue('Unrelated note');
  // WHEN the tenant explicitly resets the conflicting field and repairs identity THEN valid save remains an explicit action.
  fireEvent.click(screen.getByRole('button', { name: 'Reset Species to Workbench' }));
  await customize('Common name', 'Reconciled name'); save();
  await waitFor(() => expect(callbacks.onSaved).toHaveBeenCalled());
  expect(vi.mocked(api.saveGemOverrides).mock.calls[1][1]).not.toHaveProperty('species');
});

it('recovers uncertain creation by its original ID without blindly posting again', async () => {
  // GIVEN the save committed but its response was lost.
  vi.mocked(api.createTenantGem).mockRejectedValueOnce(new TypeError('Network failed'));
  mount(); change('Common name', 'My saved gem'); change('Species', 'Corundum'); save();
  await screen.findByRole('button', { name: 'Check current entry' });
  const created = vi.mocked(api.createTenantGem).mock.calls[0][0];
  vi.mocked(api.getGem).mockResolvedValue({ ...entry('tenant'), ...created });
  // WHEN checking THEN the retained ID is read and explicit reconciliation selects update rather than a second POST.
  fireEvent.click(screen.getByRole('button', { name: 'Check current entry' }));
  await screen.findByRole('region', { name: 'Review current entry' });
  expect(api.getGem).toHaveBeenLastCalledWith(created.id, 'tenant');
  fireEvent.click(screen.getByRole('button', { name: 'Keep my draft with current versions' })); save();
  await waitFor(() => expect(api.updateTenantGem).toHaveBeenCalled());
  expect(api.createTenantGem).toHaveBeenCalledTimes(1);
});

it('allows deliberate retry after an uncertain creation is confirmed absent and prevents overlapping writes', async () => {
  // GIVEN a lost response and a subsequent authoritative not-found read.
  vi.mocked(api.createTenantGem).mockRejectedValueOnce(new TypeError('Offline'));
  vi.mocked(api.getGem).mockRejectedValue(new api.GemReferenceApiError(404, {}));
  mount(); change('Common name', 'My gem'); change('Species', 'Species'); save();
  fireEvent.click(await screen.findByRole('button', { name: 'Check current entry' }));
  await screen.findByText(/No saved entry was found/);
  // WHEN the user deliberately retries THEN the same ID is submitted.
  const id = vi.mocked(api.createTenantGem).mock.calls[0][0].id;
  save(); await waitFor(() => expect(callbacks.onSaved).toHaveBeenCalled());
  expect(vi.mocked(api.createTenantGem).mock.calls[1][0].id).toBe(id);
});

it('requires an explicit whole-entry reset confirmation and preserves a draft when reset fails', async () => {
  // GIVEN a shared entry with unsaved tenant choices and a failing reset.
  vi.mocked(api.resetGem).mockRejectedValueOnce(new api.GemReferenceApiError(409, { code: 'needs_review', errors: { identity: ['Reset still conflicts.'] } }));
  mount('workbench'); await customize('Description', 'Keep this draft');
  fireEvent.click(screen.getByRole('button', { name: 'Reset all fields to Workbench' }));
  const dialog = screen.getByRole('dialog', { name: 'Reset tenant customizations?' });
  expect(dialog).toHaveTextContent('unsaved');
  // WHEN confirmed reset is rejected THEN inputs remain available for repair.
  fireEvent.click(within(dialog).getByRole('button', { name: 'Reset all fields' }));
  await screen.findByText('Reset still conflicts.');
  expect(screen.getByLabelText('Description', { exact: true })).toHaveValue('Keep this draft');
});

it('binds notable locality to a tenant source and its review date', async () => {
  // GIVEN a personal reference locality requires its own citation.
  mount(); fireEvent.click(screen.getByLabelText('Include notable locality'));
  change('Locality place', 'Synthetic valley'); change('Locality scope', 'Only known commercial source');
  fireEvent.click(screen.getByRole('button', { name: 'Add Notable locality source' }));
  change('Notable locality source 1 title', 'Locality report'); change('Notable locality source 1 publisher', 'Institute'); change('Notable locality source 1 URL', 'https://example.test/report'); change('Notable locality source 1 review date', '2026-10-01');
  // WHEN a source is selected THEN the saved claim uses that assertion and date, not a displaced Workbench source.
  const select = screen.getByLabelText('Locality supporting source');
  fireEvent.change(select, { target: { value: within(select).getByRole('option', { name: 'Locality report' }).getAttribute('value') } });
  change('Common name', 'Locality gem'); change('Species', 'Species'); save();
  await waitFor(() => expect(callbacks.onSaved).toHaveBeenCalled());
  const saved = vi.mocked(api.createTenantGem).mock.calls[0][0];
  expect(saved.notableLocality).toMatchObject({ sourceAssertionId: saved.sources[0].id, reviewedOn: '2026-10-01' });
});

it('reports inaccessible loads and ends the editor on authentication loss', async () => {
  // GIVEN an entry cannot be accessed.
  vi.mocked(api.getGem).mockRejectedValueOnce(new api.GemReferenceApiError(404, {}));
  mount('tenant'); await screen.findByText('Gem reference not found.');
  // WHEN a retry loses authentication THEN the owning application clears private editor state.
  vi.mocked(api.getGem).mockRejectedValueOnce(new api.GemReferenceApiError(401, {}));
  fireEvent.click(screen.getByRole('button', { name: 'Retry load' }));
  await waitFor(() => expect(callbacks.onAuthLost).toHaveBeenCalled());
});

it('locks overlapping saves and retains an existing-entry draft while checking an uncertain write', async () => {
  // GIVEN a pending update can still commit even after the user loses its response.
  let reject!: (error: Error) => void;
  vi.mocked(api.updateTenantGem).mockImplementationOnce(() => new Promise((_, fail) => { reject = fail; }));
  vi.mocked(api.getGem).mockResolvedValue(entry('tenant'));
  mount('tenant'); await screen.findByLabelText('Common name'); change('Common name', 'Unsaved member edit'); save();
  // WHEN saving THEN overlapping submissions and editing are disabled.
  expect(screen.getByRole('button', { name: 'Saving…' })).toBeDisabled();
  expect(screen.getByLabelText('Common name', { exact: true })).toBeDisabled();
  await act(async () => { reject(new TypeError('Response lost')); });
  fireEvent.click(await screen.findByRole('button', { name: 'Check current entry' }));
  await screen.findByRole('region', { name: 'Review current entry' });
  expect(screen.getByLabelText('Common name', { exact: true })).toHaveValue('Unsaved member edit');
  expect(api.updateTenantGem).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole('button', { name: 'Keep my draft with current versions' }));
  expect(callbacks.onDirtyChange).toHaveBeenLastCalledWith(true, false);
});

it('associates server field and source errors with the editable controls', async () => {
  // GIVEN the server rejects a field value and a source collection.
  vi.mocked(api.createTenantGem).mockRejectedValueOnce(new api.GemReferenceApiError(400, { code: 'validation_failed', errors: { species: ['Species is required.'], sources: ['Review the source and its review date.'] } }));
  mount(); change('Common name', 'Personal gem');
  fireEvent.click(screen.getByRole('button', { name: 'Add Common name source' }));
  // WHEN saving THEN the focused summary and the controls expose their reasons together.
  save(); await screen.findByRole('alert');
  expect(screen.getByRole('textbox', { name: 'Species' })).toHaveAttribute('aria-invalid', 'true');
  expect(screen.getByRole('textbox', { name: 'Species' })).toHaveAccessibleDescription('Species is required.');
  expect(screen.getByRole('textbox', { name: 'Common name source 1 title' })).toHaveAccessibleDescription('Review the source and its review date.');
  expect(screen.getByRole('alert')).toHaveFocus();
});

it('converts description and citation line breaks to spaces with visible contract guidance', async () => {
  // GIVEN pasted prose and a publication citation include ordinary line breaks.
  mount(); change('Common name', 'Personal gem'); change('Species', 'Corundum');
  change('Description', 'First paragraph.\nSecond paragraph.');
  fireEvent.click(screen.getByRole('button', { name: 'Add Description source' }));
  change('Description source 1 title', 'Handbook'); change('Description source 1 publisher', 'Institute');
  change('Description source 1 publication citation', 'Volume 1.\r\nPage 9.');
  change('Description source 1 review date', '2026-10-01');
  // WHEN saving THEN both values satisfy the existing single-line API contract.
  expect(screen.getByRole('textbox', { name: 'Description' })).toHaveAccessibleDescription(/Line breaks are converted to spaces/);
  save(); await waitFor(() => expect(callbacks.onSaved).toHaveBeenCalled());
  expect(vi.mocked(api.createTenantGem).mock.calls[0][0]).toMatchObject({ description: 'First paragraph. Second paragraph.', sources: [expect.objectContaining({ citation: 'Volume 1. Page 9.' })] });
});
