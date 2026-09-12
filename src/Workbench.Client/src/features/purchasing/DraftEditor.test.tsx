import { createSupplier, getSupplier, getSuppliers } from '../../api/suppliers';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { vi } from 'vitest';
import { DraftEditor } from './DraftEditor';
import { createDraft, updateDraft, getDraft, deleteDraft, DraftError } from '../../api/purchaseOrders';

vi.mock('../../api/purchaseOrders', async importOriginal => ({ ...await importOriginal<typeof import('../../api/purchaseOrders')>(), createDraft: vi.fn(), updateDraft: vi.fn(), getDraft: vi.fn(), deleteDraft: vi.fn() }));
vi.mock('../../api/suppliers', async original => ({ ...await original<typeof import('../../api/suppliers')>(), createSupplier: vi.fn(), getSupplier: vi.fn(), getSuppliers: vi.fn() }));
const content = { title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: null, notes: null, sourceLinks: [], entries: [] };
const saved = { id: 'draft-one', poReference: 'PO-000001', supplierIsArchived: false, draft: content, version: 'v1', createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' };
const receipt = { requestId: 'request', replayed: false, draftOrderId: saved.id, savedVersion: saved.version, completedAtUtc: saved.updatedAtUtc };
const props = () => ({ onDirtyChange: vi.fn(), onAuthLost: vi.fn(), onSaved: vi.fn(), onCancel: vi.fn(), onCreated: vi.fn() });
it('summarizes saved supplier context and exposes it for editing', async () => {
  // GIVEN a saved draft with supplier and transaction platform.
  vi.mocked(getDraft).mockResolvedValue({ ...saved, draft: { ...content, supplierName: 'Gem supplier', platform: 'Instagram' } });
  render(<DraftEditor {...props()} id={saved.id} />);
  const name = await screen.findByDisplayValue('Gem supplier');
  // THEN the supplier remains identifiable while its editing fields are collapsed.
  expect(name.closest('details')).not.toHaveAttribute('open');
  expect(screen.getByText('Gem supplier · Instagram')).toBeVisible();
});
it('keeps empty entry details optional and reveals their validation errors', async () => {
  // GIVEN a new entry whose optional notes and source are empty.
  vi.mocked(createDraft).mockRejectedValue(new DraftError(400, 'draft_validation_failed', { 'draft.entries[0].sourceLink': ['Check this source link.'] }));
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Add entry' }));
  const source = screen.getByLabelText('Entry source link 1');
  const disclosure = source.closest('details');
  expect(disclosure).not.toBeNull();
  expect(disclosure).not.toHaveAttribute('open');
  // WHEN the server reports an error inside the optional details.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await screen.findByRole('link', { name: 'Check this source link.' });
  // THEN the field is revealed and the summary link can focus it.
  expect(disclosure).toHaveAttribute('open');
  // AND a validation link reopens details the user subsequently collapsed.
  disclosure!.removeAttribute('open');
  fireEvent.click(screen.getByRole('link', { name: 'Check this source link.' }));
  expect(disclosure).toHaveAttribute('open');
  expect(source).toHaveFocus();
});
it('reveals newly populated details when adopting a newer saved version of the same entry', async () => {
  // GIVEN an existing empty entry and a newer saved version containing research notes.
  const entry = { id: 'same-entry', description: 'Sapphire', indicativePrice: null, notes: null, sourceLink: null };
  vi.mocked(getDraft).mockResolvedValueOnce({ ...saved, draft: { ...content, entries: [entry] } }).mockResolvedValueOnce({ ...saved, version: 'v2', draft: { ...content, entries: [{ ...entry, notes: 'New research' }] } });
  vi.mocked(updateDraft).mockRejectedValue(new DraftError(409, 'draft_version_conflict'));
  render(<DraftEditor {...props()} id={saved.id} />);
  await screen.findByDisplayValue('Sapphire');
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Local title' } });
  // WHEN conflict recovery adopts the newer saved content.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Use saved version' }));
  // THEN notes added to the same entry are revealed without needing to rediscover them.
  const notes = await screen.findByDisplayValue('New research');
  expect(notes.closest('details')).toHaveAttribute('open');
});
it('reveals populated entry details when reopening a draft', async () => {
  // GIVEN an existing entry with research notes.
  vi.mocked(getDraft).mockResolvedValue({ ...saved, draft: { ...content, entries: [{ id: 'entry', description: 'Sapphire', indicativePrice: null, notes: 'Check inclusions', sourceLink: null }] } });
  render(<DraftEditor {...props()} id={saved.id} />);
  // WHEN the draft loads THEN existing details are visible and retained.
  const notes = await screen.findByDisplayValue('Check inclusions');
  expect(notes.closest('details')).toHaveAttribute('open');
  expect(notes).toBeVisible();
});
beforeEach(() => { Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } }); vi.mocked(deleteDraft).mockReset(); vi.mocked(createDraft).mockReset(); vi.mocked(updateDraft).mockReset(); vi.mocked(getDraft).mockReset(); });
it('saves an empty draft and enables editing only after loading the confirmed current document', async () => {
  // GIVEN all business fields are optional and the confirmation read has not returned.
  let resolve!: (value: typeof saved) => void;
  vi.mocked(createDraft).mockResolvedValue(receipt);
  vi.mocked(getDraft).mockImplementation(() => new Promise(done => { resolve = done; }));
  const callbacks = props(); render(<DraftEditor {...callbacks} />);
  // WHEN saving an empty draft twice before completion.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  fireEvent.submit(screen.getByLabelText('Title').closest('form')!);
  await waitFor(() => expect(getDraft).toHaveBeenCalledWith(saved.id));
  // THEN only one creation occurs, the URL can be replaced, and editing waits for current content.
  expect(createDraft).toHaveBeenCalledTimes(1);
  expect(vi.mocked(createDraft).mock.calls[0][0].draft).toEqual(content);
  expect(callbacks.onCreated).toHaveBeenCalledWith(saved.id);
  expect(screen.getByLabelText('Title')).toBeDisabled();
  await act(async () => resolve(saved));
  expect(screen.getByLabelText('Title')).not.toBeDisabled();
});
it('retries only the current-detail read when a confirmed creation cannot be loaded', async () => {
  // GIVEN saving succeeds but the follow-up GET fails.
  vi.mocked(createDraft).mockResolvedValue(receipt);
  vi.mocked(getDraft).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce(saved);
  render(<DraftEditor {...props()} />);
  fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'Keep this' } });
  // WHEN saving and retrying the failed read.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await screen.findByText('Saved; current version could not be loaded.');
  expect(screen.getByLabelText('Notes')).toHaveValue('Keep this');
  fireEvent.click(screen.getByRole('button', { name: 'Load current draft' }));
  // THEN a second mutation never occurs.
  await waitFor(() => expect(screen.getByLabelText('Title')).not.toBeDisabled());
  expect(createDraft).toHaveBeenCalledTimes(1); expect(getDraft).toHaveBeenCalledTimes(2);
});
it('freezes an uncertain request and resends its exact payload and request ID', async () => {
  // GIVEN a response is lost after the user submits notes.
  vi.mocked(createDraft).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce(receipt);
  vi.mocked(getDraft).mockResolvedValue(saved); render(<DraftEditor {...props()} />);
  fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'Original notes' } });
  // WHEN explicitly checking and retrying the uncertain save.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  const retry = await screen.findByRole('button', { name: 'Check and retry' });
  expect(screen.getByLabelText('Notes')).toBeDisabled(); fireEvent.click(retry);
  // THEN the original immutable request is reused.
  await waitFor(() => expect(getDraft).toHaveBeenCalled());
  expect(vi.mocked(createDraft).mock.calls[1][0]).toEqual(vi.mocked(createDraft).mock.calls[0][0]);
});
it('retains local content for deliberate review when a receipt points to an older saved version', async () => {
  // GIVEN the draft has been edited again after this save succeeded.
  vi.mocked(createDraft).mockResolvedValue(receipt);
  vi.mocked(getDraft).mockResolvedValue({ ...saved, version: 'v2', draft: { ...content, notes: 'Other editor' } });
  vi.mocked(updateDraft).mockResolvedValue({ ...receipt, savedVersion: 'v3' });
  render(<DraftEditor {...props()} />);
  fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'My notes' } });
  // WHEN the saved document is newer than the receipt.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  const comparison = await screen.findByRole('region', { name: 'Compare draft versions' });
  // THEN both complete drafts are shown and adopting the reviewed token requires explicit action.
  expect(within(comparison).getByText('Other editor')).toBeVisible();
  expect(within(comparison).getByText('My notes')).toBeVisible();
  expect(updateDraft).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Continue with my changes' }));
  expect(screen.getByLabelText('Notes')).toHaveValue('My notes');
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await waitFor(() => expect(updateDraft).toHaveBeenCalledWith(saved.id, expect.objectContaining({ expectedVersion: 'v2', draft: expect.objectContaining({ notes: 'My notes' }) })));
});
it('links authoritative validation errors to retained fields', async () => {
  // GIVEN the API rejects currency while preserving the submitted reference amount.
  vi.mocked(createDraft).mockRejectedValue(new DraftError(400, 'draft_validation_failed', { 'draft.currency': ['Choose a currency.'] }));
  render(<DraftEditor {...props()} />);
  fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'Kept notes' } });
  // WHEN saving invalid input.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  // THEN field errors are announced, linked, and input remains editable.
  await screen.findByRole('link', { name: 'Choose a currency.' });
  expect(screen.queryByText('Review the draft fields and save again.')).not.toBeInTheDocument();
  expect(screen.getByRole('heading', { name: 'Review these fields' })).toBeVisible();
  expect(screen.getByLabelText('Currency')).toHaveAttribute('aria-invalid', 'true');
  expect(screen.getByLabelText('Notes')).toHaveValue('Kept notes');
  expect(screen.getByLabelText('Notes')).not.toBeDisabled();
});
it('does not resubmit an unchanged saved draft but allows a new empty draft', async () => {
  // GIVEN a saved draft has been reopened without edits.
  vi.mocked(getDraft).mockResolvedValue(saved); render(<DraftEditor id={saved.id} {...props()} />);
  await waitFor(() => expect(screen.getByLabelText('Title')).not.toBeDisabled());
  // WHEN no content has changed THEN another save is unavailable and direct submission is ignored.
  expect(screen.getByRole('button', { name: 'Save draft' })).toBeDisabled();
  fireEvent.submit(screen.getByLabelText('Title').closest('form')!);
  expect(updateDraft).not.toHaveBeenCalled();
});
it('allows correcting an unsaved currency after authoritative validation with a price still entered', async () => {
  // GIVEN validation rejects an unsaved currency on a priced entry.
  vi.mocked(createDraft).mockRejectedValue(new DraftError(400, 'draft_validation_failed', { 'draft.currency': ['Use a three-letter currency.'] }));
  render(<DraftEditor {...props()} />);
  fireEvent.change(screen.getByLabelText('Currency'), { target: { value: 'US' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add entry' }));
  fireEvent.change(screen.getByLabelText('Reference price 1'), { target: { value: '0' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  // WHEN the user corrects the rejected currency THEN the entered exact price remains available.
  await screen.findByRole('link', { name: 'Use a three-letter currency.' });
  expect(screen.getByLabelText('Currency')).not.toBeDisabled();
  fireEvent.change(screen.getByLabelText('Currency'), { target: { value: 'USD' } });
  expect(screen.getByLabelText('Reference price 1')).toHaveValue('0.00');
});
it('keeps navigation guarded after a request-ID conflict and never silently creates another request', async () => {
  // GIVEN the server rejects a changed-input reuse of a request identifier.
  vi.mocked(createDraft).mockRejectedValue(new DraftError(409, 'draft_request_conflict'));
  const callbacks = props(); render(<DraftEditor {...callbacks} />);
  fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'Keep this private input' } });
  // WHEN the conflict is shown THEN input is retained, save is blocked, and departure needs deliberate discard.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await screen.findByText(/This save request conflicts/);
  expect(screen.getByLabelText('Notes')).toHaveValue('Keep this private input');
  expect(screen.getByRole('button', { name: 'Save draft' })).toBeDisabled();
  expect(callbacks.onDirtyChange).toHaveBeenLastCalledWith(true, false);
  expect(createDraft).toHaveBeenCalledTimes(1);
});
it('retains every local field across a failed conflict read and adopts only the deliberately reviewed version', async () => {
  // GIVEN a draft conflicts after edits and the first comparison read fails.
  const latest = { ...saved, version: 'v2', draft: { ...content, title: 'Other title', sourceLinks: ['https://example.test/current'], entries: [{ id: 'different', description: 'Added elsewhere', notes: 'Other entry note', sourceLink: 'https://example.test/entry', indicativePrice: null }] } };
  vi.mocked(getDraft).mockResolvedValueOnce(saved).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce(latest);
  vi.mocked(updateDraft).mockRejectedValue(new DraftError(409, 'draft_version_conflict'));
  render(<DraftEditor id={saved.id} {...props()} />); await waitFor(() => expect(screen.getByLabelText('Title')).not.toBeDisabled());
  fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'My full notes' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add source link' }));
  fireEvent.change(screen.getByLabelText('Source link 1'), { target: { value: 'https://example.test/local' } });
  // WHEN saving conflicts, then the failed comparison read is retried.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await screen.findByText('Your changes are kept; the current draft could not be loaded for comparison.');
  expect(screen.getByLabelText('Notes')).toHaveValue('My full notes');
  fireEvent.click(screen.getByRole('button', { name: 'Load current draft' }));
  const comparison = await screen.findByRole('region', { name: 'Compare draft versions' });
  // THEN both complete documents are visible; choosing the saved version never sends an overwrite.
  expect(within(comparison).getByText('Added elsewhere')).toBeVisible();
  expect(within(comparison).getByText('Other entry note')).toBeVisible();
  expect(within(comparison).getByRole('link', { name: 'https://example.test/local' })).toHaveAttribute('rel', 'noopener noreferrer');
  fireEvent.click(screen.getByRole('button', { name: 'Use saved version' }));
  expect(screen.getByLabelText('Title')).toHaveValue('Other title');
  expect(screen.getByLabelText('Description 1')).toHaveValue('Added elsewhere');
  expect(updateDraft).toHaveBeenCalledTimes(1);
});
it('requires a clearing save before pricing in a different saved currency and preserves unknown versus zero', async () => {
  // GIVEN an existing USD reference price of exact zero.
  const entry = { id: 'entry', description: null, notes: null, sourceLink: null, indicativePrice: '0.0000' };
  const priced = { ...saved, draft: { ...content, currency: 'USD', entries: [entry] } };
  const cleared = { ...saved, version: 'v2', draft: { ...priced.draft, currency: 'EUR', entries: [{ ...entry, indicativePrice: null }] } };
  vi.mocked(getDraft).mockResolvedValueOnce(priced).mockResolvedValueOnce(cleared);
  vi.mocked(updateDraft).mockResolvedValue({ ...receipt, savedVersion: 'v2' });
  render(<DraftEditor id={saved.id} {...props()} />);
  await waitFor(() => expect(screen.getByLabelText('Reference price 1')).toHaveValue('0.00'));
  // WHEN clearing the price and changing currency THEN a new price cannot be entered before the clearing save.
  expect(screen.getByLabelText('Currency')).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Clear all reference prices' }));
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Clear prices' }));
  expect(screen.getByText('Price: Unknown')).toBeVisible();
  fireEvent.change(screen.getByLabelText('Currency'), { target: { value: 'EUR' } });
  expect(screen.getByLabelText('Reference price 1')).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  // THEN only a confirmed current version permits entering the next currency's exact price.
  await waitFor(() => expect(screen.getByLabelText('Reference price 1')).not.toBeDisabled());
  expect(vi.mocked(updateDraft).mock.calls[0][1].draft.entries[0].indicativePrice).toBeNull();
  fireEvent.click(screen.getByRole('checkbox', { name: 'Use extra precision for entry 1' }));
  fireEvent.change(screen.getByLabelText('Reference price 1'), { target: { value: '999999999999999.9999' } });
  expect(screen.getByLabelText('Reference price 1')).toHaveValue('999999999999999.9999');
});
it('clears private editor content on authentication loss and ignores late results after navigation', async () => {
  // GIVEN a private draft and an unauthorized save.
  vi.mocked(createDraft).mockRejectedValue(new DraftError(401));
  const callbacks = props(); const view = render(<DraftEditor {...callbacks} />);
  fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'Private notes' } });
  // WHEN authentication is lost THEN no private input remains in the mounted form.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await waitFor(() => expect(callbacks.onAuthLost).toHaveBeenCalled());
  expect(screen.getByLabelText('Notes')).toHaveValue('');
  expect(callbacks.onDirtyChange).toHaveBeenLastCalledWith(false, false);
  view.unmount();
  // GIVEN a different editor is closed while its save is pending.
  let resolve!: (value: typeof receipt) => void;
  vi.mocked(createDraft).mockImplementation(() => new Promise(done => { resolve = done; }));
  const later = props(); const pending = render(<DraftEditor {...later} />);
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' })); pending.unmount();
  // WHEN the old response arrives THEN it cannot navigate the new screen or fetch private content.
  await act(async () => resolve(receipt));
  expect(later.onCreated).not.toHaveBeenCalled(); expect(later.onSaved).not.toHaveBeenCalled(); expect(getDraft).not.toHaveBeenCalled();
});

it('requires confirmation to clear prices and lets cancellation preserve them', async () => {
  // GIVEN two reference prices, including zero, and one unknown price.
  const entries = ['125.5000', '0.0000', null].map((indicativePrice, index) => ({ id: String(index), description: null, notes: null, sourceLink: null, indicativePrice }));
  vi.mocked(getDraft).mockResolvedValue({ ...saved, draft: { ...content, currency: 'USD', entries } });
  render(<DraftEditor id={saved.id} {...props()} />);
  await screen.findByRole('button', { name: 'Clear all reference prices' });
  // WHEN opening the confirmation and cancelling.
  fireEvent.click(screen.getByRole('button', { name: 'Clear all reference prices' }));
  const dialog = screen.getByRole('dialog', { name: 'Clear reference prices?' });
  expect(within(dialog).getByText(/2 reference prices/)).toBeVisible();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
  // THEN prices are retained and no save is sent.
  expect(screen.getByLabelText('Reference price 1')).toHaveValue('125.50');
  expect(screen.getByLabelText('Reference price 2')).toHaveValue('0.00');
  expect(screen.getByRole('button', { name: 'Save draft' })).toBeDisabled();
  // WHEN explicitly confirming the clear.
  fireEvent.click(screen.getByRole('button', { name: 'Clear all reference prices' }));
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Clear prices' }));
  // THEN all prices become unknown locally; persistence still requires Save.
  expect(screen.getAllByText('Price: Unknown')).toHaveLength(3);
  expect(updateDraft).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: 'Save draft' })).toBeEnabled();
});

it('adds entries from the end of the list and focuses each new description', () => {
  // GIVEN an empty draft with an add action below the empty-state message.
  render(<DraftEditor {...props()} />);
  const add = screen.getByRole('button', { name: 'Add entry' });
  // WHEN adding successive entries.
  fireEvent.click(add);
  const first = screen.getByLabelText('Description 1');
  // THEN focus moves into the new entry and the add action follows that entry.
  expect(first).toHaveFocus();
  expect(first.compareDocumentPosition(add) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  fireEvent.change(first, { target: { value: 'Keep this description' } });
  fireEvent.click(add);
  // THEN another blank entry receives focus while previous input remains intact.
  expect(screen.getByLabelText('Description 2')).toHaveFocus();
  expect(first).toHaveValue('Keep this description');
  expect(screen.getByLabelText('Description 2').compareDocumentPosition(add) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
});

it('starts reference prices with an empty 0.00 placeholder and offers extra precision', () => {
  // GIVEN a new entry with no reference price.
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Add entry' }));
  const price = screen.getByLabelText('Reference price 1');
  // WHEN the price is first shown THEN zero is only a placeholder and precision is optional.
  expect(price).toHaveValue('');
  expect(price).toHaveAttribute('placeholder', '0.00');
  expect(screen.getByRole('checkbox', { name: 'Use extra precision for entry 1' })).not.toBeChecked();
});

it('confirms a named draft deletion and leaves cancellation unchanged', async () => {
  // GIVEN a persisted draft with unsaved local edits.
  vi.mocked(getDraft).mockResolvedValue({ ...saved, draft: { ...content, title: 'Supplier samples' } });
  vi.mocked(deleteDraft).mockResolvedValue(receipt);
  const callbacks = props(); render(<DraftEditor id={saved.id} {...callbacks} />);
  await waitFor(() => expect(screen.getByLabelText('Title')).toBeEnabled());
  fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'Unsaved notes' } });
  // WHEN requesting deletion THEN the confirmation names the saved draft.
  fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));
  const dialog = screen.getByRole('dialog', { name: 'Delete draft?' });
  expect(within(dialog).getByText(/Supplier samples/)).toBeVisible();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
  expect(deleteDraft).not.toHaveBeenCalled(); expect(screen.getByLabelText('Notes')).toHaveValue('Unsaved notes');
  // WHEN confirming THEN delete uses the loaded version, clears navigation guards, and returns to the list without a GET.
  fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete draft' }));
  await waitFor(() => expect(callbacks.onCancel).toHaveBeenCalled());
  expect(deleteDraft).toHaveBeenCalledWith(saved.id, expect.objectContaining({ expectedVersion: 'v1' }));
  expect(callbacks.onDirtyChange).toHaveBeenLastCalledWith(false, false);
  expect(getDraft).toHaveBeenCalledTimes(1);
});
it('retries an uncertain deletion with the same request and freezes editing', async () => {
  // GIVEN the deletion response was lost.
  vi.mocked(getDraft).mockResolvedValue(saved);
  vi.mocked(deleteDraft).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce(receipt);
  render(<DraftEditor id={saved.id} {...props()} />);
  await waitFor(() => expect(screen.getByLabelText('Title')).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete draft' }));
  // WHEN explicitly retrying THEN the immutable deletion request is reused.
  fireEvent.click(await screen.findByRole('button', { name: 'Check and retry deletion' }));
  expect(screen.getByLabelText('Title')).toBeDisabled();
  await waitFor(() => expect(deleteDraft).toHaveBeenCalledTimes(2));
  expect(vi.mocked(deleteDraft).mock.calls[1]).toEqual(vi.mocked(deleteDraft).mock.calls[0]);
});

it('requires review and a fresh confirmation after a stale deletion', async () => {
  // GIVEN another member changed the draft after it was opened.
  vi.mocked(getDraft).mockResolvedValueOnce(saved).mockResolvedValueOnce({ ...saved, version: 'v2', draft: { ...content, title: 'Newer title' } });
  vi.mocked(deleteDraft).mockRejectedValueOnce(new DraftError(409, 'draft_version_conflict')).mockResolvedValueOnce(receipt);
  render(<DraftEditor id={saved.id} {...props()} />);
  await waitFor(() => expect(screen.getByLabelText('Title')).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete draft' }));
  // THEN current content is reviewed without an automatic deletion retry.
  await screen.findByRole('region', { name: 'Compare draft versions' });
  expect(deleteDraft).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole('button', { name: 'Use saved version' }));
  fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));
  expect(within(screen.getByRole('dialog')).getByText(/Newer title/)).toBeVisible();
  // WHEN confirming again THEN a fresh request targets the reviewed version.
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete draft' }));
  await waitFor(() => expect(deleteDraft).toHaveBeenCalledTimes(2));
  expect(vi.mocked(deleteDraft).mock.calls[1][1].expectedVersion).toBe('v2');
  expect(vi.mocked(deleteDraft).mock.calls[1][1].requestId).not.toBe(vi.mocked(deleteDraft).mock.calls[0][1].requestId);
});
it('retains platform and supplier contact fields through validation and includes them in the save', async () => {
  // GIVEN a one-off supplier with transaction-specific details.
  vi.mocked(createDraft).mockRejectedValue(new DraftError(400, 'draft_validation_failed', { 'draft.supplierEmail': ['Check email.'] }));
  render(<DraftEditor {...props()} />);
  // WHEN the owner enters contacts and a platform and saves.
  fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Instagram' } });
  fireEvent.change(screen.getByLabelText('Supplier email'), { target: { value: 'not-an-email' } });
  fireEvent.change(screen.getByLabelText('Supplier order reference'), { target: { value: 'IG-8' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  // THEN authoritative errors preserve every submitted field.
  await screen.findByRole('link', { name: 'Check email.' });
  expect(vi.mocked(createDraft).mock.calls[0][0].draft).toMatchObject({ platform: 'Instagram', supplierEmail: 'not-an-email', supplierOrderReference: 'IG-8' });
  expect(screen.getByLabelText('Platform')).toHaveValue('Instagram');
});



it('searches and saves an inline supplier independently without submitting the enclosing draft', async () => {
  // GIVEN an unsaved order and a supplier directory.
  const supplier = { id: 'supplier-one', supplier: { name: 'Gem Studio', contactName: null, email: 'hello@example.test', phone: null, website: null, postalAddress: null }, isArchived: false, version: 's1', createdAtUtc: saved.createdAtUtc, updatedAtUtc: saved.updatedAtUtc };
  vi.mocked(getSuppliers).mockResolvedValue({ items: [], nextCursor: null });
  vi.mocked(createSupplier).mockResolvedValue({ requestId: 'supplier-request', replayed: false, supplierId: supplier.id, savedVersion: supplier.version, completedAtUtc: supplier.updatedAtUtc }); vi.mocked(getSupplier).mockResolvedValue(supplier);
  render(<DraftEditor {...props()} />);
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Unsaved order' } });
  // WHEN searching the supplier picker and independently saving a new directory entry.
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  const chooser = await screen.findByRole('dialog', { name: 'Choose supplier' });
  fireEvent.submit(within(chooser).getByRole('searchbox').closest('form')!);
  await waitFor(() => expect(getSuppliers).toHaveBeenCalledTimes(2));
  expect(createDraft).not.toHaveBeenCalled();
  fireEvent.click(within(chooser).getByRole('button', { name: 'Cancel' }));
  fireEvent.click(screen.getByRole('button', { name: 'New supplier' }));
  const dialog = screen.getByRole('dialog', { name: 'New supplier' });
  expect(dialog.closest('#po-draft-form')).toBeNull();
  fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Gem Studio' } });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Use supplier' }));
  // THEN only the supplier was saved; the order retains its local title and selected snapshot.
  expect(createDraft).not.toHaveBeenCalled(); expect(createSupplier).toHaveBeenCalledTimes(1);
  expect(screen.getByLabelText('Title')).toHaveValue('Unsaved order'); expect(screen.getByLabelText('Supplier name')).toHaveValue('Gem Studio');
});
it('previews refresh and supplier changes while preserving platform and requiring a reference decision', async () => {
  // GIVEN a linked draft with its own snapshot, platform and external reference.
  const linked = { ...saved, draft: { ...content, supplierId: 'old', supplierName: 'Old studio', supplierEmail: 'saved@example.test', platform: 'Instagram', supplierOrderReference: 'OLD-42' } };
  const latest = { id: 'old', supplier: { name: 'Old studio', contactName: 'Current owner', email: 'current@example.test', phone: null, website: null, postalAddress: 'Current address' }, isArchived: false, version: 's2', createdAtUtc: saved.createdAtUtc, updatedAtUtc: saved.updatedAtUtc };
  vi.mocked(getDraft).mockResolvedValue(linked); vi.mocked(getSupplier).mockResolvedValue(latest);
  vi.mocked(getSuppliers).mockResolvedValue({ items: [{ ...latest, id: 'new', supplier: { ...latest.supplier, name: 'New studio' } }], nextCursor: null });
  render(<DraftEditor id={saved.id} {...props()} />); await waitFor(() => expect(screen.getByLabelText('Platform')).toHaveValue('Instagram'));
  // WHEN a refresh is first canceled and then explicitly accepted.
  fireEvent.click(screen.getByRole('button', { name: 'Use current supplier details' }));
  let dialog = await screen.findByRole('dialog', { name: 'Review supplier details' });
  expect(within(dialog).getByText('saved@example.test')).toBeVisible(); expect(within(dialog).getByText('current@example.test')).toBeVisible();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' })); expect(screen.getByLabelText('Supplier email')).toHaveValue('saved@example.test');
  fireEvent.click(screen.getByRole('button', { name: 'Use current supplier details' })); fireEvent.click(await screen.findByRole('button', { name: 'Replace supplier details' }));
  expect(screen.getByLabelText('Platform')).toHaveValue('Instagram'); expect(screen.getByLabelText('Supplier order reference')).toHaveValue('OLD-42'); expect(screen.getByLabelText('Supplier email')).toHaveValue('current@example.test');
  // WHEN changing identity, the owner must deliberately keep or clear the old supplier reference.
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' })); fireEvent.click(await screen.findByRole('button', { name: 'Select New studio' }));
  dialog = screen.getByRole('dialog', { name: 'Review supplier details' }); expect(within(dialog).getByRole('button', { name: 'Replace supplier details' })).toBeDisabled();
  fireEvent.click(within(dialog).getByLabelText('Clear supplier order reference')); fireEvent.click(within(dialog).getByRole('button', { name: 'Replace supplier details' }));
  // THEN platform stays independent and nothing is saved implicitly.
  expect(screen.getByLabelText('Supplier name')).toHaveValue('New studio'); expect(screen.getByLabelText('Platform')).toHaveValue('Instagram'); expect(screen.getByLabelText('Supplier order reference')).toHaveValue(''); expect(updateDraft).not.toHaveBeenCalled();
});
it('keeps supplier details as one-off without carrying the prior reference silently', async () => {
  // GIVEN an archived linked supplier with transaction details.
  vi.mocked(getDraft).mockResolvedValue({ ...saved, supplierIsArchived: true, draft: { ...content, supplierId: 'old', supplierName: 'Studio', supplierPhone: '+44 123', platform: 'Retail', supplierOrderReference: 'A-1' } });
  render(<DraftEditor id={saved.id} {...props()} />); fireEvent.click(await screen.findByRole('button', { name: 'Keep details as one-off' }));
  // WHEN unlinking, explicitly retain the reference and snapshot.
  fireEvent.click(screen.getByLabelText('Keep supplier order reference')); fireEvent.click(screen.getByRole('button', { name: 'Confirm one-off details' }));
  vi.mocked(updateDraft).mockRejectedValue(new DraftError(400, 'draft_validation_failed'));
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  // THEN the association alone is removed and the full contact/platform/reference snapshot is retained.
  await waitFor(() => expect(updateDraft).toHaveBeenCalledWith(saved.id, expect.objectContaining({ draft: expect.objectContaining({ supplierId: null, supplierName: 'Studio', supplierPhone: '+44 123', platform: 'Retail', supplierOrderReference: 'A-1' }) })));
});
it('clears the order snapshot immediately when supplier refresh loses business access', async () => {
  // GIVEN private saved and local contact details and an authorization failure on supplier refresh.
  vi.mocked(getDraft).mockResolvedValue({ ...saved, draft: { ...content, supplierId: 'one', supplierName: 'Private studio', platform: 'Private platform' } });
  vi.mocked(getSupplier).mockRejectedValueOnce(new DraftError(403));
  const callbacks = props(); render(<DraftEditor id={saved.id} {...callbacks} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Use current supplier details' }));
  // WHEN the supplier API rejects authority THEN no contact body remains while authentication refresh runs.
  await waitFor(() => expect(callbacks.onAuthLost).toHaveBeenCalled());
  expect(screen.getByLabelText('Supplier name')).toHaveValue(''); expect(screen.getByLabelText('Platform')).toHaveValue('');
  expect(screen.getByLabelText('Title')).toBeDisabled();
});
it('presents a clear new order heading and one draft state before any details are entered', () => {
  // GIVEN a new purchase order with no assigned reference.
  render(<DraftEditor {...props()} />);
  // WHEN its initial editing view is shown THEN the task is visible without duplicated state copy.
  expect(screen.getByRole('heading', { name: 'New purchase order', level: 1 })).toBeVisible();
  expect(screen.getAllByText('Draft', { exact: true })).toHaveLength(1);
  expect(screen.queryByText('Assigned when saved')).not.toBeInTheDocument();
  expect(screen.queryByText('Not saved yet')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Title')).toBeEnabled();
});
