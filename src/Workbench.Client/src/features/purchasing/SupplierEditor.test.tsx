import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { vi } from 'vitest';
import { SupplierEditor } from './SupplierEditor';
import { createSupplier, getSupplier, updateSupplier, archiveSupplier, SupplierError } from '../../api/suppliers';
vi.mock('../../api/suppliers', async original => ({ ...await original<typeof import('../../api/suppliers')>(), createSupplier: vi.fn(), getSupplier: vi.fn(), updateSupplier: vi.fn(), archiveSupplier: vi.fn() }));
const supplier = { name: 'Gems', contactName: 'Owner', email: 'old@example.test', phone: '+1 234', website: 'https://example.test', postalAddress: 'Line one\nLine two' };
const saved = { id: 'one', supplier, version: 'v1', isArchived: false, createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' };
const receipt = { requestId: 'request', replayed: false, supplierId: 'one', savedVersion: 'v1', completedAtUtc: saved.updatedAtUtc };
const props = () => ({ onDirtyChange: vi.fn(), onAuthLost: vi.fn(), onCancel: vi.fn(), onCreated: vi.fn(), onSelected: vi.fn() });
beforeEach(() => { vi.resetAllMocks(); Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } }); });
it('offers contact-specific input controls without imposing browser validation over server feedback', () => {
  // GIVEN a supplier form WHEN entering contact details THEN devices receive the appropriate input semantics.
  render(<SupplierEditor {...props()} />);
  for (const [label, type] of [['Email', 'email'], ['Phone', 'tel'], ['Website', 'url']]) {
    expect(screen.getByLabelText(label)).toHaveAttribute('type', type);
  }
  // AND server validation remains responsible for feedback without losing entered text.
  expect(screen.getByLabelText('Email').closest('form')).toHaveAttribute('novalidate');
});
it('explains an unavailable supplier and keeps navigation available', async () => {
  // GIVEN a bookmarked supplier is no longer available WHEN loading fails with 404.
  vi.mocked(getSupplier).mockRejectedValueOnce(new SupplierError(404));
  const callbacks = props(); render(<SupplierEditor id="missing" {...callbacks} />);
  // THEN the user sees a permanent unavailable state rather than a temporary failure with a futile retry.
  expect(await screen.findByRole('alert')).toHaveTextContent('This supplier is unavailable. Return to suppliers to choose another.');
  expect(screen.queryByRole('button', { name: 'Load current supplier' })).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Save supplier' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Back to suppliers' }));
  expect(callbacks.onCancel).toHaveBeenCalledOnce();
});
it('prevents duplicate submissions while a slow save is pending', async () => {
  // GIVEN entered supplier details and a save that has not returned.
  let finish!: (value: typeof receipt) => void;
  vi.mocked(createSupplier).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  vi.mocked(getSupplier).mockResolvedValueOnce(saved);
  render(<SupplierEditor {...props()} />);
  fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Gems' } });
  const form = screen.getByLabelText('Supplier name').closest('form')!;
  // WHEN repeated submit events arrive THEN only one mutation is sent and the form is frozen.
  for (let attempt = 0; attempt < 10; attempt++) fireEvent.submit(form);
  expect(createSupplier).toHaveBeenCalledOnce();
  expect(screen.getByLabelText('Supplier name')).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Saving supplier…' })).toBeDisabled();
  await act(async () => finish(receipt));
  expect(await screen.findByRole('heading', { name: 'Gems', level: 1 })).toBeVisible();
});
it('leads with the required supplier name and groups the optional contact fields', () => {
  // GIVEN a new supplier WHEN the form opens THEN the minimum record and optional details are clear.
  render(<SupplierEditor {...props()} />);
  expect(screen.getByLabelText('Supplier name')).toBeRequired();
  expect(screen.getByLabelText('Supplier name')).toHaveAccessibleDescription('Required');
  const contacts = screen.getByRole('group', { name: 'Contact details (optional)' });
  expect(within(contacts).getAllByRole('textbox')).toHaveLength(5);
  expect(within(contacts).queryByLabelText('Supplier name')).not.toBeInTheDocument();
  expect(screen.getAllByText('Changes here won’t update existing purchase orders.')).toHaveLength(1);
});
it('saves from the standalone header and keeps identity and status truthful across edits', async () => {
  // GIVEN a saved supplier opened in its standalone editor.
  const renamed = { ...saved, supplier: { ...supplier, name: 'Updated Gems' }, version: 'v2' };
  vi.mocked(getSupplier).mockResolvedValueOnce(saved).mockResolvedValueOnce(renamed);
  vi.mocked(updateSupplier).mockResolvedValueOnce({ ...receipt, savedVersion: 'v2' });
  const callbacks = props(); render(<SupplierEditor id="one" {...callbacks} />);
  const heading = await screen.findByRole('heading', { name: 'Gems', level: 1 });
  const save = screen.getByRole('button', { name: 'Save supplier' });
  expect(save.compareDocumentPosition(heading) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  expect(save.closest('form')).toBeNull();
  expect(save).toBeDisabled();
  expect(screen.getByRole('status')).toHaveTextContent(`Saved ${new Date(saved.updatedAtUtc).toLocaleString()}`);
  // WHEN editing the name THEN saved identity remains stable until saving from the toolbar succeeds.
  fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Updated Gems' } });
  expect(heading).toHaveTextContent('Gems');
  expect(screen.getByRole('status')).toHaveTextContent('Unsaved changes');
  fireEvent.click(save);
  await screen.findByRole('heading', { name: 'Updated Gems', level: 1 });
  expect(updateSupplier).toHaveBeenCalledWith('one', expect.objectContaining({ supplier: expect.objectContaining({ name: 'Updated Gems' }) }));
  expect(screen.getAllByRole('button', { name: 'Save supplier' })).toHaveLength(1);
  // WHEN editing again THEN stale save confirmation is replaced by unsaved feedback.
  fireEvent.change(screen.getByLabelText('Phone'), { target: { value: '+44 new' } });
  expect(screen.getByRole('status')).toHaveTextContent('Unsaved changes');
  expect(screen.queryByText('Supplier saved. Existing orders keep their saved details.')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Back to suppliers' }));
  expect(callbacks.onCancel).toHaveBeenCalledOnce();
});
it('identifies an archived supplier in the standalone heading', async () => {
  // GIVEN an archived supplier WHEN loaded THEN its name and archive state remain visible together.
  vi.mocked(getSupplier).mockResolvedValueOnce({ ...saved, isArchived: true });
  render(<SupplierEditor id="one" {...props()} />);
  const heading = await screen.findByRole('heading', { name: 'Gems', level: 1 });
  expect(within(heading.parentElement!).getByText('Archived')).toBeVisible();
});
it('keeps inline cancel beside save without submitting supplier details', () => {
  // GIVEN the supplier form inside an order dialog.
  const callbacks = props(); render(<SupplierEditor inline {...callbacks} />);
  expect(screen.queryByRole('button', { name: 'Back to suppliers' })).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Save supplier' }).closest('form')).not.toBeNull();
  fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Unsaved supplier' } });
  // WHEN cancelling through the form footer THEN the parent handles discard protection and no save occurs.
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(callbacks.onCancel).toHaveBeenCalledOnce();
  expect(createSupplier).not.toHaveBeenCalled();
});
it('freezes an uncertain independent save and selects only after a current GET confirms its receipt', async () => {
  // GIVEN creation loses its response and the independent supplier form has local contact details.
  vi.mocked(createSupplier).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce(receipt);
  let finish!: (value: typeof saved) => void; vi.mocked(getSupplier).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const callbacks = props(); render(<SupplierEditor inline {...callbacks} />);
  fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Gems' } });
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'old@example.test' } });
  // WHEN the owner retries the unknown outcome.
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Retry save' }));
  await waitFor(() => expect(getSupplier).toHaveBeenCalledWith('one'));
  // THEN the same immutable request is reused, and no order selection occurs before current state is known.
  expect(vi.mocked(createSupplier).mock.calls[1][0]).toEqual(vi.mocked(createSupplier).mock.calls[0][0]);
  expect(screen.getByLabelText('Supplier name')).toBeDisabled(); expect(callbacks.onSelected).not.toHaveBeenCalled();
  await act(async () => finish(saved)); expect(callbacks.onSelected).toHaveBeenCalledWith(saved);
});
it('retries only the read after a confirmed save and preserves validation input', async () => {
  // GIVEN a validation failure, followed by successful creation with failed confirmation read.
  vi.mocked(createSupplier).mockRejectedValueOnce(new SupplierError(400, 'supplier_validation_failed', { 'supplier.email': ['Check the email.'] })).mockResolvedValueOnce(receipt);
  vi.mocked(getSupplier).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce(saved);
  render(<SupplierEditor {...props()} />); fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Gems' } }); fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'bad' } });
  // WHEN correcting the rejected email and retrying the later failed GET.
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' })); await screen.findByRole('link', { name: 'Check the email.' }); expect(screen.getByLabelText('Supplier name')).toHaveValue('Gems');
  // THEN the rejected field receives focus and its error is available to assistive technology.
  await waitFor(() => expect(screen.getByLabelText('Email')).toHaveFocus());
  expect(screen.getByLabelText('Email')).toHaveAccessibleDescription('Check the email.');
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'old@example.test' } }); fireEvent.click(screen.getByRole('button', { name: 'Save supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Load current supplier' }));
  // THEN read recovery never repeats the mutation.
  await waitFor(() => expect(screen.getByLabelText('Supplier name')).toBeEnabled()); expect(createSupplier).toHaveBeenCalledTimes(2); expect(getSupplier).toHaveBeenCalledTimes(2);
});
it('compares all supplier contact fields before deliberately adopting a newer version', async () => {
  // GIVEN another editor changed contact details before this update.
  const newer = { ...saved, version: 'v2', supplier: { ...supplier, email: 'new@example.test' }, isArchived: true };
  vi.mocked(getSupplier).mockResolvedValueOnce(saved).mockResolvedValueOnce(newer).mockResolvedValueOnce({ ...newer, version: 'v3' });
  vi.mocked(updateSupplier).mockRejectedValueOnce(new SupplierError(409, 'supplier_version_conflict')).mockResolvedValueOnce({ ...receipt, savedVersion: 'v3' });
  render(<SupplierEditor id="one" {...props()} />); await waitFor(() => expect(screen.getByLabelText('Supplier name')).toHaveValue('Gems'));
  fireEvent.change(screen.getByLabelText('Phone'), { target: { value: '+44 local' } });
  // WHEN a stale save is rejected and the owner reviews both versions.
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' })); const comparison = await screen.findByRole('region', { name: 'Compare supplier versions' });
  expect(within(comparison).getByText('new@example.test')).toBeVisible(); expect(within(comparison).getByText('+44 local')).toBeVisible(); expect(within(comparison).getByText('Archived supplier')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Continue with my changes' })); fireEvent.click(screen.getByRole('button', { name: 'Save supplier' }));
  // THEN saving uses the explicitly reviewed version with retained local details.
  await waitFor(() => expect(updateSupplier).toHaveBeenLastCalledWith('one', expect.objectContaining({ expectedVersion: 'v2', supplier: expect.objectContaining({ phone: '+44 local' }) })));
});
it.each([false, true])('confirms and retries the correct archive operation from archived=%s', async isArchived => {
  // GIVEN a saved supplier and a lost response to changing its archive state.
  vi.mocked(getSupplier).mockResolvedValueOnce({ ...saved, isArchived }).mockResolvedValueOnce({ ...saved, version: 'v2', isArchived: !isArchived });
  vi.mocked(archiveSupplier).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce({ ...receipt, savedVersion: 'v2' });
  render(<SupplierEditor id="one" {...props()} />); fireEvent.click(await screen.findByRole('button', { name: isArchived ? 'Reactivate supplier' : 'Archive supplier' }));
  expect(archiveSupplier).not.toHaveBeenCalled();
  // WHEN confirming and checking the unknown outcome.
  fireEvent.click(screen.getByRole('button', { name: isArchived ? 'Confirm reactivation' : 'Confirm archive' })); fireEvent.click(await screen.findByRole('button', { name: isArchived ? 'Retry reactivation' : 'Retry archive' }));
  // THEN the label names the intended operation, its immutable request is retried, and current state is confirmed.
  await screen.findByRole('button', { name: isArchived ? 'Archive supplier' : 'Reactivate supplier' }); expect(vi.mocked(archiveSupplier).mock.calls[1]).toEqual(vi.mocked(archiveSupplier).mock.calls[0]); expect(updateSupplier).not.toHaveBeenCalled();
  expect(archiveSupplier).toHaveBeenLastCalledWith('one', expect.objectContaining({ isArchived: !isArchived }));
});
it('protects unsaved contact changes while a conflict comparison read is still pending', async () => {
  // GIVEN a rejected stale update whose current supplier read has not returned.
  let finish!: (value: typeof saved) => void;
  vi.mocked(getSupplier).mockResolvedValueOnce(saved).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  vi.mocked(updateSupplier).mockRejectedValueOnce(new SupplierError(409, 'supplier_version_conflict'));
  const callbacks = props(); render(<SupplierEditor id="one" {...callbacks} />); await waitFor(() => expect(screen.getByLabelText('Supplier name')).toHaveValue('Gems'));
  fireEvent.change(screen.getByLabelText('Phone'), { target: { value: 'Unsaved phone' } });
  // WHEN fetching the comparison THEN navigation remains protected until reconciliation.
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' })); await waitFor(() => expect(getSupplier).toHaveBeenCalledTimes(2));
  await waitFor(() => expect(callbacks.onDirtyChange).toHaveBeenLastCalledWith(true, false));
  await act(async () => finish(saved));
});
it('keeps supplier edits keyboard-copyable while preserving the uncertain command', async () => {
  // GIVEN a supplier save with no confirmed response.
  vi.mocked(createSupplier).mockRejectedValue(new TypeError('Network'));
  render(<SupplierEditor {...props()} />);
  fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Retained supplier' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' }));
  await screen.findByRole('button', { name: 'Retry save' });
  const original = vi.mocked(createSupplier).mock.calls.at(-1)![0];
  // WHEN selecting recovery text THEN it is focusable while the source stays frozen.
  fireEvent.click(screen.getByRole('button', { name: 'Select supplier text' }));
  const recovery = screen.getByRole('textbox', { name: 'Supplier recovery text' });
  expect(recovery).toHaveFocus();
  expect((recovery as HTMLTextAreaElement).value).toContain('Retained supplier');
  expect(screen.getByLabelText('Supplier name')).toBeDisabled();
  // AND a retry reuses the same command.
  fireEvent.click(screen.getByRole('button', { name: 'Retry save' }));
  await waitFor(() => expect(vi.mocked(createSupplier).mock.calls.at(-1)![0]).toBe(original));
});
it.each(['conflict-read-failed', 'saved-read-failed', 'blocked', 'comparison'] as const)('keeps frozen supplier text selectable after %s without resubmitting', async state => {
  // GIVEN local contact edits whose save conflicts, is blocked, or needs a confirmation read.
  vi.mocked(getSupplier).mockResolvedValueOnce(saved);
  if (state === 'comparison') vi.mocked(getSupplier).mockResolvedValueOnce({ ...saved, version: 'v2' });
  else vi.mocked(getSupplier).mockRejectedValueOnce(new TypeError('Network'));
  if (state === 'saved-read-failed') vi.mocked(updateSupplier).mockResolvedValueOnce(receipt);
  else vi.mocked(updateSupplier).mockRejectedValueOnce(new SupplierError(409, state === 'blocked' ? 'supplier_request_conflict' : 'supplier_version_conflict'));
  render(<SupplierEditor id="one" {...props()} />);
  await waitFor(() => expect(screen.getByLabelText('Supplier name')).toBeEnabled());
  fireEvent.change(screen.getByLabelText('Phone'), { target: { value: 'Retain local phone' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' }));
  if (state === 'comparison') await screen.findByRole('region', { name: 'Compare supplier versions' });
  else await screen.findByRole('alert');
  // WHEN selecting retained text THEN all contact details remain keyboard-accessible and readonly.
  fireEvent.click(screen.getByRole('button', { name: 'Select supplier text' }));
  const recovery = screen.getByRole('textbox', { name: 'Supplier recovery text' }) as HTMLTextAreaElement;
  expect(recovery).toHaveFocus();
  expect(recovery).toHaveAttribute('readonly');
  expect(recovery.value).toContain('Retain local phone');
  expect(recovery.selectionStart).toBe(0);
  expect(recovery.selectionEnd).toBe(recovery.value.length);
  expect(screen.getByLabelText('Phone')).toBeDisabled();
  // AND selecting text does not mutate or bypass conflict reconciliation.
  expect(screen.getByRole('button', { name: 'Save supplier' })).toBeDisabled();
  expect(updateSupplier).toHaveBeenCalledOnce();
});
