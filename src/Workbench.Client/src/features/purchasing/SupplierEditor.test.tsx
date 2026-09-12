import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { vi } from 'vitest';
import { SupplierEditor } from './SupplierEditor';
import { createSupplier, getSupplier, updateSupplier, archiveSupplier, SupplierError } from '../../api/suppliers';
vi.mock('../../api/suppliers', async original => ({ ...await original<typeof import('../../api/suppliers')>(), createSupplier: vi.fn(), getSupplier: vi.fn(), updateSupplier: vi.fn(), archiveSupplier: vi.fn() }));
const supplier = { name: 'Gems', contactName: 'Owner', email: 'old@example.test', phone: '+1 234', website: 'https://example.test', postalAddress: 'Line one\nLine two' };
const saved = { id: 'one', supplier, version: 'v1', isArchived: false, createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' };
const receipt = { requestId: 'request', replayed: false, supplierId: 'one', savedVersion: 'v1', completedAtUtc: saved.updatedAtUtc };
const props = () => ({ onDirtyChange: vi.fn(), onAuthLost: vi.fn(), onCancel: vi.fn(), onCreated: vi.fn(), onSelected: vi.fn() });
beforeEach(() => { vi.clearAllMocks(); Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } }); });
it('freezes an uncertain independent save and selects only after a current GET confirms its receipt', async () => {
  // GIVEN creation loses its response and the independent supplier form has local contact details.
  vi.mocked(createSupplier).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce(receipt);
  let finish!: (value: typeof saved) => void; vi.mocked(getSupplier).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const callbacks = props(); render(<SupplierEditor inline {...callbacks} />);
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Gems' } });
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'old@example.test' } });
  // WHEN the owner retries the unknown outcome.
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Check and retry supplier save' }));
  await waitFor(() => expect(getSupplier).toHaveBeenCalledWith('one'));
  // THEN the same immutable request is reused, and no order selection occurs before current state is known.
  expect(vi.mocked(createSupplier).mock.calls[1][0]).toEqual(vi.mocked(createSupplier).mock.calls[0][0]);
  expect(screen.getByLabelText('Name')).toBeDisabled(); expect(callbacks.onSelected).not.toHaveBeenCalled();
  await act(async () => finish(saved)); expect(callbacks.onSelected).toHaveBeenCalledWith(saved);
});
it('retries only the read after a confirmed save and preserves validation input', async () => {
  // GIVEN a validation failure, followed by successful creation with failed confirmation read.
  vi.mocked(createSupplier).mockRejectedValueOnce(new SupplierError(400, 'supplier_validation_failed', { 'supplier.email': ['Check the email.'] })).mockResolvedValueOnce(receipt);
  vi.mocked(getSupplier).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce(saved);
  render(<SupplierEditor {...props()} />); fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Gems' } }); fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'bad' } });
  // WHEN correcting the rejected email and retrying the later failed GET.
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' })); await screen.findByRole('link', { name: 'Check the email.' }); expect(screen.getByLabelText('Name')).toHaveValue('Gems');
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'old@example.test' } }); fireEvent.click(screen.getByRole('button', { name: 'Save supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Load current supplier' }));
  // THEN read recovery never repeats the mutation.
  await waitFor(() => expect(screen.getByLabelText('Name')).toBeEnabled()); expect(createSupplier).toHaveBeenCalledTimes(2); expect(getSupplier).toHaveBeenCalledTimes(2);
});
it('compares all supplier contact fields before deliberately adopting a newer version', async () => {
  // GIVEN another editor changed contact details before this update.
  const newer = { ...saved, version: 'v2', supplier: { ...supplier, email: 'new@example.test' }, isArchived: true };
  vi.mocked(getSupplier).mockResolvedValueOnce(saved).mockResolvedValueOnce(newer).mockResolvedValueOnce({ ...newer, version: 'v3' });
  vi.mocked(updateSupplier).mockRejectedValueOnce(new SupplierError(409, 'supplier_version_conflict')).mockResolvedValueOnce({ ...receipt, savedVersion: 'v3' });
  render(<SupplierEditor id="one" {...props()} />); await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Gems'));
  fireEvent.change(screen.getByLabelText('Phone'), { target: { value: '+44 local' } });
  // WHEN a stale save is rejected and the owner reviews both versions.
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' })); const comparison = await screen.findByRole('region', { name: 'Compare supplier versions' });
  expect(within(comparison).getByText('new@example.test')).toBeVisible(); expect(within(comparison).getByText('+44 local')).toBeVisible(); expect(within(comparison).getByText('Archived supplier')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Continue with my changes' })); fireEvent.click(screen.getByRole('button', { name: 'Save supplier' }));
  // THEN saving uses the explicitly reviewed version with retained local details.
  await waitFor(() => expect(updateSupplier).toHaveBeenLastCalledWith('one', expect.objectContaining({ expectedVersion: 'v2', supplier: expect.objectContaining({ phone: '+44 local' }) })));
});
it('requires archive confirmation and freezes an unknown archive request for exact retry', async () => {
  // GIVEN a saved active supplier and a lost archive response.
  vi.mocked(getSupplier).mockResolvedValueOnce(saved).mockResolvedValueOnce({ ...saved, version: 'v2', isArchived: true });
  vi.mocked(archiveSupplier).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce({ ...receipt, savedVersion: 'v2' });
  render(<SupplierEditor id="one" {...props()} />); fireEvent.click(await screen.findByRole('button', { name: 'Archive supplier' }));
  expect(archiveSupplier).not.toHaveBeenCalled();
  // WHEN confirming and checking the unknown outcome.
  fireEvent.click(screen.getByRole('button', { name: 'Confirm archive' })); fireEvent.click(await screen.findByRole('button', { name: 'Check and retry supplier save' }));
  // THEN the same archive request is retried and reactivation is offered after current GET.
  await screen.findByRole('button', { name: 'Reactivate supplier' }); expect(vi.mocked(archiveSupplier).mock.calls[1]).toEqual(vi.mocked(archiveSupplier).mock.calls[0]); expect(updateSupplier).not.toHaveBeenCalled();
});
it('protects unsaved contact changes while a conflict comparison read is still pending', async () => {
  // GIVEN a rejected stale update whose current supplier read has not returned.
  let finish!: (value: typeof saved) => void;
  vi.mocked(getSupplier).mockResolvedValueOnce(saved).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  vi.mocked(updateSupplier).mockRejectedValueOnce(new SupplierError(409, 'supplier_version_conflict'));
  const callbacks = props(); render(<SupplierEditor id="one" {...callbacks} />); await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Gems'));
  fireEvent.change(screen.getByLabelText('Phone'), { target: { value: 'Unsaved phone' } });
  // WHEN fetching the comparison THEN navigation remains protected until reconciliation.
  fireEvent.click(screen.getByRole('button', { name: 'Save supplier' })); await waitFor(() => expect(getSupplier).toHaveBeenCalledTimes(2));
  expect(callbacks.onDirtyChange).toHaveBeenLastCalledWith(true, false);
  await act(async () => finish(saved));
});
